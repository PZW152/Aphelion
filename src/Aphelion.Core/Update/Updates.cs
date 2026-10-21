using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aphelion.Core.Net;
using Aphelion.Core.Storage;

namespace Aphelion.Core.Update;

/// <summary>
/// What a release publishes next to the executable as <c>update.json</c>, signed in <c>update.json.sig</c>.
/// <see cref="Product"/> ties the manifest to this application, so a manifest signed for anything else is refused.
/// </summary>
public sealed class UpdateManifest
{
    public int Schema { get; set; } = 1;
    public string Product { get; set; } = "";
    public string Version { get; set; } = "";
    public DateTimeOffset Published { get; set; }
    public UpdateFile File { get; set; } = new();
    /// <summary>Release notes by language ("zh-Hant", "zh-Hans", "en").</summary>
    public Dictionary<string, string> Notes { get; set; } = [];
    public string? Page { get; set; }
}

public sealed class UpdateFile
{
    public string Name { get; set; } = "";
    public long Size { get; set; }
    public string Sha256 { get; set; } = "";
    /// <summary>Every place the same signed bytes can be fetched from, tried in order.</summary>
    public List<string> Urls { get; set; } = [];
}

/// <summary>Release versions: major.minor.patch with an optional pre-release tag, which sorts before the release.</summary>
public readonly record struct AppVersion(int Major, int Minor, int Patch, string? Pre) : IComparable<AppVersion>
{
    public static bool TryParse(string? text, out AppVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var s = text.Trim().TrimStart('v', 'V').Split('+')[0];
        var dash = s.IndexOf('-', StringComparison.Ordinal);
        var pre = dash >= 0 ? s[(dash + 1)..] : null;
        var parts = (dash >= 0 ? s[..dash] : s).Split('.');
        if (parts.Length is < 1 or > 3) return false;
        var n = new int[3];
        for (var i = 0; i < parts.Length; i++)
            if (!int.TryParse(parts[i], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out n[i])) return false;
        version = new AppVersion(n[0], n[1], n[2], string.IsNullOrEmpty(pre) ? null : pre);
        return true;
    }

    public int CompareTo(AppVersion other)
    {
        var c = Major.CompareTo(other.Major);
        if (c == 0) c = Minor.CompareTo(other.Minor);
        if (c == 0) c = Patch.CompareTo(other.Patch);
        if (c != 0) return c;
        if (Pre == other.Pre) return 0;
        if (Pre is null) return 1;
        if (other.Pre is null) return -1;
        return string.CompareOrdinal(Pre, other.Pre);
    }

    public static bool operator >(AppVersion a, AppVersion b) => a.CompareTo(b) > 0;
    public static bool operator <(AppVersion a, AppVersion b) => a.CompareTo(b) < 0;
    public static bool operator >=(AppVersion a, AppVersion b) => a.CompareTo(b) >= 0;
    public static bool operator <=(AppVersion a, AppVersion b) => a.CompareTo(b) <= 0;

    public override string ToString() => $"{Major}.{Minor}.{Patch}{(Pre is null ? "" : "-" + Pre)}";
}

/// <summary>
/// Checks for, verifies and downloads updates. Trust rests on one thing: an ECDSA P-256 signature over the exact
/// manifest bytes, made with the release key whose public half is compiled in (<see cref="UpdateKeys"/>). The
/// manifest pins the executable's size and SHA-256, so the download location (GitHub or any other mirror of the
/// release) needs no trust. Older or equal versions are ignored, so a replayed old manifest cannot downgrade.
/// </summary>
public sealed class UpdateService(IReadOnlyList<string> channels, string downloadDirectory, Func<Downloader> downloader, ECDsa? key = null)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private static readonly JsonSerializerOptions Indented = new(Options) { WriteIndented = true };
    private readonly ECDsa _key = key ?? UpdateKeys.Release;

    /// <summary>The newest verified release if it is newer than <paramref name="current"/>; null if this is the newest.</summary>
    public async Task<UpdateManifest?> CheckAsync(string current, CancellationToken ct)
    {
        Exception? last = null;
        foreach (var channel in channels)
        {
            try
            {
                var manifestBytes = await GetBytesAsync(channel + "/update.json", ct).ConfigureAwait(false);
                var signature = await GetBytesAsync(channel + "/update.json.sig", ct).ConfigureAwait(false);
                var manifest = Verify(manifestBytes, signature, _key);
                if (!AppVersion.TryParse(current, out var mine) || !AppVersion.TryParse(manifest.Version, out var theirs))
                    throw AppException.Of("update.version", $"Cannot compare {current} with {manifest.Version}");
                return theirs > mine ? manifest : null;
            }
            catch (Exception e) when (e is HttpRequestException or TimeoutException or IOException or AppException)
            {
                last = e;
                Logging.Log.Warn($"Update channel {Url.Describe(channel)} failed: {e.Message}");
            }
        }
        throw last is AppException a ? a : AppException.Of("update.unreachable", last?.Message ?? "No update channel");
    }

    /// <summary>Parses the manifest only after its signature verifies against <paramref name="key"/>.</summary>
    public static UpdateManifest Verify(byte[] manifest, byte[] signatureFile, ECDsa key)
    {
        byte[] signature;
        try { signature = Convert.FromBase64String(System.Text.Encoding.ASCII.GetString(signatureFile).Trim()); }
        catch (FormatException) { throw AppException.Of("update.signature", "The update signature is malformed."); }
        if (!key.VerifyData(manifest, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            throw AppException.Of("update.signature", "The update manifest is not signed by the Aphelion release key.");

        UpdateManifest? m;
        try { m = JsonSerializer.Deserialize<UpdateManifest>(manifest, Options); }
        catch (JsonException e) { throw AppException.Of("update.manifest", e.Message); }
        if (m is null || m.Schema != 1 || !string.Equals(m.Product, AppInfo.AppId, StringComparison.OrdinalIgnoreCase)
            || m.File.Urls.Count == 0 || m.File.Size <= 0 || m.File.Sha256.Length != 64
            || !m.File.Urls.All(IsAllowedUrl))
            throw AppException.Of("update.manifest", "The update manifest is not for this application or is incomplete.");
        return m;
    }

    /// <summary>
    /// HTTPS only, as defence in depth beside the signature. Plain HTTP is accepted for this machine's own loopback
    /// address, which no remote party can answer for (local release testing).
    /// </summary>
    internal static bool IsAllowedUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u)
        && (u.Scheme == Uri.UriSchemeHttps || (u.Scheme == Uri.UriSchemeHttp && u.IsLoopback));

    /// <summary>Downloads the new executable into its own folder and returns its path once size and SHA-256 match.</summary>
    public async Task<string> DownloadAsync(UpdateManifest manifest, IProgress<DownloadProgress>? progress, CancellationToken ct)
    {
        var folder = Path.Combine(downloadDirectory, "update-" + manifest.Version);
        var target = Path.Combine(folder, SafeName(manifest.File.Name));
        var item = new DownloadItem(manifest.File.Urls[0], target, Size: manifest.File.Size)
        {
            Sha256 = manifest.File.Sha256,
            Fallbacks = manifest.File.Urls.Skip(1).ToList(),
        };
        await downloader().RunAsync([item], progress, ct, VerifyMode.Hash).ConfigureAwait(false);
        return target;
    }

    /// <summary>Sign a manifest (release tooling and tests).</summary>
    public static string Sign(byte[] manifest, ECDsa privateKey) =>
        Convert.ToBase64String(privateKey.SignData(manifest, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

    public static byte[] Serialize(UpdateManifest manifest) => JsonSerializer.SerializeToUtf8Bytes(manifest, Indented);

    private static string SafeName(string name)
    {
        var file = Path.GetFileName(name);
        return file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && file.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 ? file : AppInfo.Name + ".exe";
    }

    private static async Task<byte[]> GetBytesAsync(string url, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            using var response = await Http.Client.GetAsync(url, cts.Token).ConfigureAwait(false);
            await Http.EnsureSuccessAsync(response, url).ConfigureAwait(false);
            if (response.Content.Headers.ContentLength > 256 * 1024) throw new IOException("Update manifest too large");
            return await response.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"Request timed out: {Url.Describe(url)}");
        }
    }
}

/// <summary>The public half of the release signing key. The private half never enters the repository.</summary>
public static class UpdateKeys
{
    public const string ReleasePublicKey = """
        -----BEGIN PUBLIC KEY-----
        MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEOjxRATNHPp8a82edBmOePgXlPXLH
        th4JNzOl/fqPQCr1MSrdAEcB8UZt44G+hej4HzAToXT9mu0uIwZhTJtDQw==
        -----END PUBLIC KEY-----
        """;

    public static ECDsa Release { get; } = Load(ReleasePublicKey);

    public static ECDsa Load(string pem)
    {
        var key = ECDsa.Create();
        key.ImportFromPem(pem);
        return key;
    }
}
