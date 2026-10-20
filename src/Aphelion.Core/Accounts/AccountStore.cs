using System.Security.Cryptography;
using System.Text.Json;
using Aphelion.Core.Logging;
using Aphelion.Core.Storage;

namespace Aphelion.Core.Accounts;

public sealed class AccountFile
{
    public const int CurrentSchema = 1;
    public int Schema { get; set; } = CurrentSchema;
    public string? Selected { get; set; }
    public List<Account> Accounts { get; set; } = [];
}

/// <summary>Persists accounts in config/accounts.json. Secrets are encrypted with the injected protector.</summary>
public sealed class AccountStore(string path, ISecretProtector protector)
{
    private readonly Lock _gate = new();
    private AccountFile _file = AtomicFile.ReadJson<AccountFile>(path) ?? new AccountFile();

    public event Action? Changed;

    public IReadOnlyList<Account> All
    {
        get { lock (_gate) return _file.Accounts.ToList(); }
    }

    public string? SelectedId
    {
        get { lock (_gate) return _file.Selected is { } s && _file.Accounts.Any(a => a.Id == s) ? s : _file.Accounts.FirstOrDefault()?.Id; }
    }

    public Account? Get(string id)
    {
        lock (_gate) return _file.Accounts.FirstOrDefault(a => a.Id == id);
    }

    public Account? Selected => SelectedId is { } id ? Get(id) : null;

    public void Select(string id) => Mutate(f =>
    {
        if (f.Accounts.All(a => a.Id != id)) throw AppException.Of("account.notFound", "Account not found");
        f.Selected = id;
    });

    /// <summary>Adds or replaces an account. Re-signing into the same profile updates it instead of duplicating it.</summary>
    public Account Upsert(Account account, AccountSecret? secret)
    {
        account.ProtectedSecret = secret is null ? null : Seal(secret);
        account.NeedsSignIn = false;
        Mutate(f =>
        {
            var existing = f.Accounts.FindIndex(a => a.Id == account.Id
                || (a.Kind == account.Kind && a.Uuid == account.Uuid && string.Equals(a.Server, account.Server, StringComparison.OrdinalIgnoreCase)));
            if (existing >= 0)
            {
                account.Id = f.Accounts[existing].Id;
                account.Added = f.Accounts[existing].Added;
                f.Accounts[existing] = account;
            }
            else f.Accounts.Add(account);
            f.Selected = account.Id;
        });
        return account;
    }

    public void UpdateSecret(string id, AccountSecret secret, Action<Account>? also = null) => Mutate(f =>
    {
        var a = f.Accounts.FirstOrDefault(x => x.Id == id) ?? throw AppException.Of("account.notFound", "Account not found");
        a.ProtectedSecret = Seal(secret);
        a.NeedsSignIn = false;
        also?.Invoke(a);
    });

    public void MarkNeedsSignIn(string id) => Mutate(f =>
    {
        var a = f.Accounts.FirstOrDefault(x => x.Id == id);
        if (a is not null) a.NeedsSignIn = true;
    });

    public void Touch(string id) => Mutate(f =>
    {
        var a = f.Accounts.FirstOrDefault(x => x.Id == id);
        if (a is not null) a.LastUsed = DateTimeOffset.UtcNow;
    });

    public void Remove(string id) => Mutate(f =>
    {
        f.Accounts.RemoveAll(a => a.Id == id);
        if (f.Selected == id) f.Selected = f.Accounts.FirstOrDefault()?.Id;
    });

    /// <summary>Decrypts an account's secret; null (and the account flagged) when it cannot be read on this machine.</summary>
    public AccountSecret? ReadSecret(Account account)
    {
        if (account.ProtectedSecret is null) return null;
        try
        {
            var plain = protector.Unprotect(Convert.FromBase64String(account.ProtectedSecret));
            return JsonSerializer.Deserialize<AccountSecret>(plain, Json.App);
        }
        catch (Exception e) when (e is CryptographicException or FormatException or JsonException)
        {
            Log.Warn($"Credentials of account {account.Name} cannot be decrypted (moved to another user or machine?)");
            MarkNeedsSignIn(account.Id);
            return null;
        }
    }

    private string Seal(AccountSecret secret) =>
        Convert.ToBase64String(protector.Protect(JsonSerializer.SerializeToUtf8Bytes(secret, Json.App)));

    private void Mutate(Action<AccountFile> change)
    {
        lock (_gate)
        {
            var copy = JsonSerializer.Deserialize<AccountFile>(JsonSerializer.SerializeToUtf8Bytes(_file, Json.App), Json.App)!;
            change(copy);
            copy.Schema = AccountFile.CurrentSchema;
            AtomicFile.WriteJson(path, copy);
            _file = copy;
        }
        Changed?.Invoke();
    }
}

/// <summary>For tests and for platforms without DPAPI. Stores secrets unencrypted.</summary>
public sealed class PlainSecretProtector : ISecretProtector
{
    public byte[] Protect(byte[] plain) => plain;
    public byte[] Unprotect(byte[] protectedData) => protectedData;
}
