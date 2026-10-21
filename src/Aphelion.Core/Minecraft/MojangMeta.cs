using System.Text.Json;
using Aphelion.Core.Net;
using Aphelion.Core.Storage;

namespace Aphelion.Core.Minecraft;

/// <summary>Fetches Mojang metadata with a small on-disk cache so the version list works offline.</summary>
public sealed class MojangMeta(string cacheDirectory)
{
    public const string ManifestUrl = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";
    public static readonly TimeSpan ManifestMaxAge = TimeSpan.FromMinutes(30);

    private VersionManifest? _memory;
    private DateTime _memoryAt;

    public async Task<VersionManifest> GetManifestAsync(CancellationToken ct, bool forceRefresh = false)
    {
        if (!forceRefresh && _memory is not null && DateTime.UtcNow - _memoryAt < ManifestMaxAge) return _memory;
        var cachePath = Path.Combine(cacheDirectory, "version_manifest_v2.json");
        try
        {
            var manifest = await FetchJsonAsync<VersionManifest>(ManifestUrl, ct).ConfigureAwait(false);
            AtomicFile.WriteJson(cachePath, manifest, Json.External, keepBackup: false);
            _memory = manifest;
            _memoryAt = DateTime.UtcNow;
            return manifest;
        }
        catch (Exception e) when (e is HttpRequestException or TimeoutException or IOException or JsonException)
        {
            var cached = AtomicFile.ReadJson<VersionManifest>(cachePath, Json.External);
            if (cached is null) throw AppException.Of("network.manifest", "Could not fetch the Minecraft version list and no cached copy exists.");
            Logging.Log.Warn("Using cached version manifest", e);
            return cached;
        }
    }

    public async Task<T> FetchJsonAsync<T>(string url, CancellationToken ct)
    {
        Exception? last = null;
        foreach (var candidate in OfficialSources.Candidates(url))
        {
            try
            {
                return await Http.GetJsonAsync<T>(candidate, ct, Json.External).ConfigureAwait(false);
            }
            catch (Exception e) when (e is HttpRequestException or TimeoutException or IOException or JsonException)
            {
                last = e;
            }
        }
        throw last!;
    }

    public async Task<byte[]> FetchBytesAsync(string url, CancellationToken ct)
    {
        Exception? last = null;
        foreach (var candidate in OfficialSources.Candidates(url))
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(60));
            try
            {
                using var response = await Http.Client.GetAsync(candidate, cts.Token).ConfigureAwait(false);
                await Http.EnsureSuccessAsync(response, candidate).ConfigureAwait(false);
                return await response.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                last = new TimeoutException($"Request timed out: {Url.Describe(candidate)}");
            }
            catch (Exception e) when (e is HttpRequestException or IOException)
            {
                last = e;
            }
        }
        throw last!;
    }

    public async Task<string> FetchStringAsync(string url, CancellationToken ct)
    {
        Exception? last = null;
        foreach (var candidate in OfficialSources.Candidates(url))
        {
            try
            {
                return await Http.GetStringAsync(candidate, ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is HttpRequestException or TimeoutException or IOException)
            {
                last = e;
            }
        }
        throw last!;
    }
}
