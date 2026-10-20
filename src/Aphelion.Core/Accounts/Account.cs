using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Aphelion.Core.Accounts;

public enum AccountKind { Offline, Microsoft, Yggdrasil }

/// <summary>Encrypts secrets at rest. The desktop app supplies DPAPI (current user); tests use a pass-through.</summary>
public interface ISecretProtector
{
    byte[] Protect(byte[] plain);
    byte[] Unprotect(byte[] protectedData);
}

public sealed class Account
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public AccountKind Kind { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Profile UUID without dashes.</summary>
    public string Uuid { get; set; } = "";
    /// <summary>Yggdrasil API root (authlib-injector accounts only).</summary>
    public string? Server { get; set; }
    public string? ServerName { get; set; }
    public string? SkinUrl { get; set; }
    public bool SkinSlim { get; set; }
    public DateTimeOffset Added { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastUsed { get; set; }
    /// <summary>Set when stored credentials no longer work (expired, revoked, or not decryptable on this machine).</summary>
    public bool NeedsSignIn { get; set; }
    /// <summary>Base64 of the protected <see cref="AccountSecret"/> JSON. Never sent to the UI.</summary>
    [JsonPropertyName("secret")]
    public string? ProtectedSecret { get; set; }
}

public sealed class AccountSecret
{
    public string? AccessToken { get; set; }
    public DateTimeOffset? AccessExpires { get; set; }
    public string? RefreshToken { get; set; }
    public string? ClientToken { get; set; }
    public string? Xuid { get; set; }
}

/// <summary>What the game is launched with.</summary>
public sealed record LaunchIdentity(string Name, string Uuid, string AccessToken, string UserType, string Xuid, string? YggdrasilServer, string? YggdrasilMetadataBase64);

public static partial class OfflineAccounts
{
    /// <summary>Same algorithm as the vanilla server: UUID v3 of "OfflinePlayer:&lt;name&gt;".</summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5351", Justification = "MD5 is the algorithm Minecraft defines for offline UUIDs.")]
    public static string UuidFor(string name)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes("OfflinePlayer:" + name));
        hash[6] = (byte)((hash[6] & 0x0f) | 0x30);
        hash[8] = (byte)((hash[8] & 0x3f) | 0x80);
        return Convert.ToHexStringLower(hash);
    }

    public static bool IsValidName(string name) => NamePattern().IsMatch(name);

    [GeneratedRegex("^[A-Za-z0-9_]{3,16}$")]
    private static partial Regex NamePattern();

    public static string Dashed(string uuid) =>
        uuid.Length == 32 ? $"{uuid[..8]}-{uuid[8..12]}-{uuid[12..16]}-{uuid[16..20]}-{uuid[20..]}" : uuid;
}
