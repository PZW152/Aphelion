using System.Collections.Concurrent;
using Aphelion.Core.Logging;

namespace Aphelion.Core.Accounts;

/// <summary>Sign-in flows and launch-time credential refresh on top of <see cref="AccountStore"/>.</summary>
public sealed class AccountService(AccountStore store, Func<string> microsoftClientId)
{
    private readonly ConcurrentDictionary<string, DeviceCodeInfo> _pendingDeviceCodes = new();
    private readonly ConcurrentDictionary<string, (YggdrasilServer Server, YggdrasilSession Session)> _pendingProfileChoices = new();

    public AccountStore Store => store;

    public Account AddOffline(string name)
    {
        name = name.Trim();
        if (!OfflineAccounts.IsValidName(name))
            throw AppException.Of("account.invalidName", "Names use 3–16 letters, digits or underscores.");
        var existing = store.All.FirstOrDefault(a => a.Kind == AccountKind.Offline && a.Name == name);
        if (existing is not null)
        {
            store.Select(existing.Id);
            return existing;
        }
        return store.Upsert(new Account { Kind = AccountKind.Offline, Name = name, Uuid = OfflineAccounts.UuidFor(name) }, null);
    }

    public async Task<(string FlowId, DeviceCodeInfo Code)> BeginMicrosoftAsync(CancellationToken ct)
    {
        var code = await new MicrosoftAuth(microsoftClientId()).BeginDeviceCodeAsync(ct).ConfigureAwait(false);
        var flowId = Guid.NewGuid().ToString("N");
        _pendingDeviceCodes[flowId] = code;
        return (flowId, code);
    }

    public async Task<Account> CompleteMicrosoftAsync(string flowId, CancellationToken ct)
    {
        if (!_pendingDeviceCodes.TryRemove(flowId, out var code)) throw AppException.Of("msa.flow", "This sign-in request is no longer active.");
        var (profile, secret) = await new MicrosoftAuth(microsoftClientId()).CompleteDeviceCodeAsync(code, ct).ConfigureAwait(false);
        return store.Upsert(new Account
        {
            Kind = AccountKind.Microsoft, Name = profile.Name, Uuid = profile.Id, SkinUrl = profile.SkinUrl, SkinSlim = profile.SlimSkin,
        }, secret);
    }

    /// <summary>Returns the account, or the profiles to choose from when the server holds several.</summary>
    public async Task<(Account? Account, string? ChoiceId, IReadOnlyList<YggdrasilProfile> Profiles)> AddYggdrasilAsync(
        string address, string username, string password, CancellationToken ct)
    {
        var server = await YggdrasilAuth.DiscoverAsync(address, ct).ConfigureAwait(false);
        var session = await YggdrasilAuth.AuthenticateAsync(server, username, password, ct).ConfigureAwait(false);
        if (session.Selected is null && session.Profiles.Count == 0)
            throw AppException.Of("ygg.noProfile", "This account has no character on the server yet.");
        if (session.Selected is null && session.Profiles.Count > 1)
        {
            var choice = Guid.NewGuid().ToString("N");
            _pendingProfileChoices[choice] = (server, session);
            return (null, choice, session.Profiles);
        }
        if (session.Selected is null)
            session = await YggdrasilAuth.RefreshAsync(server, session.AccessToken, session.ClientToken, session.Profiles[0], ct).ConfigureAwait(false);
        return (await SaveYggdrasilAsync(server, session, ct).ConfigureAwait(false), null, []);
    }

    public async Task<Account> ChooseYggdrasilProfileAsync(string choiceId, string profileId, CancellationToken ct)
    {
        if (!_pendingProfileChoices.TryRemove(choiceId, out var pending)) throw AppException.Of("ygg.flow", "This sign-in request is no longer active.");
        var profile = pending.Session.Profiles.FirstOrDefault(p => p.Id == profileId) ?? throw AppException.Of("ygg.noProfile", "Unknown character.");
        var session = await YggdrasilAuth.RefreshAsync(pending.Server, pending.Session.AccessToken, pending.Session.ClientToken, profile, ct).ConfigureAwait(false);
        return await SaveYggdrasilAsync(pending.Server, session, ct).ConfigureAwait(false);
    }

    private async Task<Account> SaveYggdrasilAsync(YggdrasilServer server, YggdrasilSession session, CancellationToken ct)
    {
        var selected = session.Selected!;
        var (skin, slim) = await YggdrasilAuth.GetSkinAsync(server, selected.Id, ct).ConfigureAwait(false);
        return store.Upsert(new Account
        {
            Kind = AccountKind.Yggdrasil, Name = selected.Name, Uuid = selected.Id, Server = server.ApiRoot, ServerName = server.Name,
            SkinUrl = skin, SkinSlim = slim,
        }, new AccountSecret { AccessToken = session.AccessToken, ClientToken = session.ClientToken });
    }

    /// <summary>Produces launch credentials, refreshing tokens when needed. Marks the account when sign-in is required.</summary>
    public async Task<LaunchIdentity> PrepareLaunchAsync(Account account, CancellationToken ct)
    {
        switch (account.Kind)
        {
            case AccountKind.Offline:
                store.Touch(account.Id);
                return new LaunchIdentity(account.Name, account.Uuid, account.Uuid, "legacy", "", null, null);

            case AccountKind.Microsoft:
            {
                var secret = store.ReadSecret(account) ?? throw RequireSignIn(account);
                if (secret.AccessToken is null || secret.AccessExpires is null || secret.AccessExpires < DateTimeOffset.UtcNow.AddMinutes(10))
                {
                    if (secret.RefreshToken is null) throw RequireSignIn(account);
                    try
                    {
                        var (profile, fresh) = await new MicrosoftAuth(microsoftClientId()).RefreshAsync(secret.RefreshToken, ct).ConfigureAwait(false);
                        store.UpdateSecret(account.Id, fresh, a => { a.Name = profile.Name; a.SkinUrl = profile.SkinUrl; a.SkinSlim = profile.SlimSkin; });
                        secret = fresh;
                        account = store.Get(account.Id) ?? account;
                    }
                    catch (AppException e) when (e.Code is "msa.relogin" or "msa.noGame")
                    {
                        throw RequireSignIn(account, e);
                    }
                }
                store.Touch(account.Id);
                return new LaunchIdentity(account.Name, account.Uuid, secret.AccessToken!, "msa", secret.Xuid ?? "", null, null);
            }

            case AccountKind.Yggdrasil:
            {
                var secret = store.ReadSecret(account) ?? throw RequireSignIn(account);
                var server = await YggdrasilAuth.DiscoverAsync(account.Server!, ct).ConfigureAwait(false);
                YggdrasilSession session;
                try
                {
                    session = await YggdrasilAuth.RefreshAsync(server, secret.AccessToken!, secret.ClientToken!, null, ct).ConfigureAwait(false);
                }
                catch (AppException e) when (e.Code is "ygg.credentials" or "ygg.error")
                {
                    throw RequireSignIn(account, e);
                }
                if (session.AccessToken != secret.AccessToken)
                    store.UpdateSecret(account.Id, new AccountSecret { AccessToken = session.AccessToken, ClientToken = session.ClientToken });
                store.Touch(account.Id);
                return new LaunchIdentity(account.Name, account.Uuid, session.AccessToken, "mojang", "", server.ApiRoot, server.MetadataBase64);
            }

            default:
                throw new InvalidOperationException($"Unknown account kind {account.Kind}");
        }
    }

    private AppException RequireSignIn(Account account, Exception? inner = null)
    {
        store.MarkNeedsSignIn(account.Id);
        Log.Warn($"Account {account.Name} needs to sign in again: {inner?.Message}");
        return new AppException("account.signInRequired", $"{account.Name} needs to sign in again.",
            new Dictionary<string, string> { ["name"] = account.Name }, inner);
    }
}
