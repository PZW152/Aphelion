using System.Collections.Concurrent;
using Aphelion.App.Platform;
using Aphelion.Core.Accounts;

namespace Aphelion.App.Bridge;

internal sealed partial class Api
{
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _microsoftFlows = new();

    private void RegisterAccounts()
    {
        _rpc.Add("accounts.list", () => new
        {
            selected = _s.Accounts.Store.SelectedId,
            microsoftAvailable = !string.IsNullOrWhiteSpace(_s.Settings.Current.MicrosoftClientId),
            accounts = _s.Accounts.Store.All.Select(a => new
            {
                a.Id, a.Kind, a.Name, a.Uuid, a.ServerName, a.Server, a.SkinUrl, a.SkinSlim, a.LastUsed, a.NeedsSignIn, a.Added,
            }),
        });
        _rpc.Add<NameArgs>("accounts.addOffline", a => _s.Accounts.AddOffline(a.Name).Id);
        _rpc.Add<IdArgs>("accounts.select", a => { _s.Accounts.Store.Select(a.Id); return null; });
        _rpc.Add<IdArgs>("accounts.remove", a => { _s.Accounts.Store.Remove(a.Id); return null; });

        _rpc.Add("accounts.beginMicrosoft", async (System.Text.Json.JsonElement _, CancellationToken ct) =>
        {
            var (flowId, code) = await _s.Accounts.BeginMicrosoftAsync(ct).ConfigureAwait(false);
            _microsoftFlows[flowId] = new CancellationTokenSource(code.ExpiresAt - DateTimeOffset.UtcNow);
            return (object?)new { flowId, code.UserCode, code.VerificationUri, code.ExpiresAt };
        });
        // Long-running: resolves when the user finishes signing in on the Microsoft page.
        _rpc.Add<FlowArgs>("accounts.completeMicrosoft", async (a, ct) =>
        {
            if (!_microsoftFlows.TryGetValue(a.FlowId, out var flowCts)) throw Core.AppException.Of("msa.flow", "This sign-in request is no longer active.");
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, flowCts.Token);
            try
            {
                var account = await _s.Accounts.CompleteMicrosoftAsync(a.FlowId, linked.Token).ConfigureAwait(false);
                return account.Id;
            }
            finally
            {
                if (_microsoftFlows.TryRemove(a.FlowId, out var c)) c.Dispose();
            }
        });
        _rpc.Add<FlowArgs>("accounts.cancelMicrosoft", a =>
        {
            if (_microsoftFlows.TryGetValue(a.FlowId, out var c)) c.Cancel();
            return null;
        });
        _rpc.Add<UrlArgs>("accounts.openVerification", a => { Shell.OpenUrl(a.Url); return null; });

        _rpc.Add<YggArgs>("accounts.addYggdrasil", async (a, ct) =>
        {
            var (account, choice, profiles) = await _s.Accounts.AddYggdrasilAsync(a.Server, a.Username, a.Password, ct).ConfigureAwait(false);
            return account is not null ? new { accountId = account.Id } : (object)new { choiceId = choice, profiles };
        });
        _rpc.Add<ChoiceArgs>("accounts.chooseProfile", async (a, ct) =>
            (await _s.Accounts.ChooseYggdrasilProfileAsync(a.ChoiceId, a.ProfileId, ct).ConfigureAwait(false)).Id);
    }

    private sealed record NameArgs(string Name);
    private sealed record FlowArgs(string FlowId);
    private sealed record YggArgs(string Server, string Username, string Password);
    private sealed record ChoiceArgs(string ChoiceId, string ProfileId);
}
