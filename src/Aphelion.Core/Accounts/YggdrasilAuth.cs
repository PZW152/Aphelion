using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aphelion.Core.Net;
using Aphelion.Core.Storage;

namespace Aphelion.Core.Accounts;

public sealed record YggdrasilServer(string ApiRoot, string Name, string MetadataBase64);
public sealed record YggdrasilProfile(string Id, string Name);
public sealed record YggdrasilSession(string AccessToken, string ClientToken, IReadOnlyList<YggdrasilProfile> Profiles, YggdrasilProfile? Selected);

/// <summary>Third-party Yggdrasil servers (LittleSkin, Blessing Skin, …) used through authlib-injector.</summary>
public static class YggdrasilAuth
{
    /// <summary>Resolves a user-entered address via API Location Indication and fetches the server metadata.</summary>
    public static async Task<YggdrasilServer> DiscoverAsync(string address, CancellationToken ct)
    {
        var text = address.Trim();
        if (!text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            throw AppException.Of("ygg.badAddress", "Enter the server's HTTPS address.");

        using (var probe = await Http.Client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
        {
            if (probe.Headers.TryGetValues("X-Authlib-Injector-API-Location", out var values) && values.FirstOrDefault() is { } location
                && Uri.TryCreate(uri, location, out var resolved) && resolved.Scheme == Uri.UriSchemeHttps)
                uri = resolved;
        }

        var root = uri.ToString().TrimEnd('/') + "/";
        string metadata;
        try
        {
            metadata = await Http.GetStringAsync(root, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            throw AppException.Of("ygg.unreachable", "The authentication server did not respond.", ("server", uri.Host));
        }
        using var doc = JsonDocument.Parse(metadata);
        if (!doc.RootElement.TryGetProperty("meta", out var meta))
            throw AppException.Of("ygg.notYggdrasil", "That address is not an authlib-injector compatible server.", ("server", uri.Host));
        var name = meta.TryGetProperty("serverName", out var n) ? n.GetString() ?? uri.Host : uri.Host;
        return new YggdrasilServer(root, name, Convert.ToBase64String(Encoding.UTF8.GetBytes(metadata)));
    }

    public static async Task<YggdrasilSession> AuthenticateAsync(YggdrasilServer server, string username, string password, CancellationToken ct)
    {
        var clientToken = Guid.NewGuid().ToString("N");
        var body = new JsonObject
        {
            ["agent"] = new JsonObject { ["name"] = "Minecraft", ["version"] = 1 },
            ["username"] = username,
            ["password"] = password,
            ["clientToken"] = clientToken,
            ["requestUser"] = true,
        };
        var json = await PostAsync(server.ApiRoot + "authserver/authenticate", body, ct).ConfigureAwait(false);
        return ParseSession(json, clientToken);
    }

    /// <summary>Validates the token; refreshes it (optionally binding a profile) when it is no longer valid.</summary>
    public static async Task<YggdrasilSession> RefreshAsync(YggdrasilServer server, string accessToken, string clientToken, YggdrasilProfile? select, CancellationToken ct)
    {
        if (select is null)
        {
            using var validate = await Http.Client.PostAsync(server.ApiRoot + "authserver/validate",
                new StringContent(new JsonObject { ["accessToken"] = accessToken, ["clientToken"] = clientToken }.ToJsonString(), Encoding.UTF8, "application/json"), ct).ConfigureAwait(false);
            if (validate.StatusCode == HttpStatusCode.NoContent) return new YggdrasilSession(accessToken, clientToken, [], null);
        }

        var body = new JsonObject { ["accessToken"] = accessToken, ["clientToken"] = clientToken, ["requestUser"] = true };
        if (select is not null) body["selectedProfile"] = new JsonObject { ["id"] = select.Id, ["name"] = select.Name };
        var json = await PostAsync(server.ApiRoot + "authserver/refresh", body, ct).ConfigureAwait(false);
        return ParseSession(json, clientToken);
    }

    public static async Task<(string? Url, bool Slim)> GetSkinAsync(YggdrasilServer server, string uuid, CancellationToken ct)
    {
        try
        {
            var profile = await Http.GetJsonAsync<JsonElement>($"{server.ApiRoot}sessionserver/session/minecraft/profile/{uuid}", ct).ConfigureAwait(false);
            return SkinFromProperties(profile);
        }
        catch (Exception e) when (e is HttpRequestException or JsonException or TimeoutException or FormatException)
        {
            return (null, false);
        }
    }

    internal static (string? Url, bool Slim) SkinFromProperties(JsonElement profile)
    {
        if (!profile.TryGetProperty("properties", out var props)) return (null, false);
        foreach (var p in props.EnumerateArray())
        {
            if (p.GetProperty("name").GetString() != "textures") continue;
            using var textures = JsonDocument.Parse(Convert.FromBase64String(p.GetProperty("value").GetString()!));
            if (!textures.RootElement.GetProperty("textures").TryGetProperty("SKIN", out var skin)) return (null, false);
            var slim = skin.TryGetProperty("metadata", out var md) && md.TryGetProperty("model", out var model) && model.GetString() == "slim";
            return (skin.GetProperty("url").GetString(), slim);
        }
        return (null, false);
    }

    private static YggdrasilSession ParseSession(JsonElement json, string fallbackClientToken)
    {
        var profiles = json.TryGetProperty("availableProfiles", out var ap)
            ? ap.EnumerateArray().Select(p => new YggdrasilProfile(p.GetProperty("id").GetString()!, p.GetProperty("name").GetString()!)).ToList()
            : [];
        YggdrasilProfile? selected = json.TryGetProperty("selectedProfile", out var sp) && sp.ValueKind == JsonValueKind.Object
            ? new YggdrasilProfile(sp.GetProperty("id").GetString()!, sp.GetProperty("name").GetString()!)
            : null;
        return new YggdrasilSession(json.GetProperty("accessToken").GetString()!,
            json.TryGetProperty("clientToken", out var ctok) ? ctok.GetString() ?? fallbackClientToken : fallbackClientToken, profiles, selected);
    }

    private static async Task<JsonElement> PostAsync(string url, JsonObject body, CancellationToken ct)
    {
        using var response = await Http.Client.PostAsync(url, new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"), ct).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            string? message = null;
            try
            {
                using var err = JsonDocument.Parse(text);
                message = err.RootElement.TryGetProperty("errorMessage", out var m) ? m.GetString() : null;
            }
            catch (JsonException) { }
            if (response.StatusCode == HttpStatusCode.Forbidden)
                throw AppException.Of("ygg.credentials", message ?? "Wrong e-mail or password.", ("message", message ?? ""));
            throw AppException.Of("ygg.error", message ?? $"Server answered {(int)response.StatusCode}", ("message", message ?? ""));
        }
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }
}

/// <summary>Downloads and verifies authlib-injector (needed to launch with Yggdrasil accounts).</summary>
public static class AuthlibInjector
{
    public const string LatestUrl = "https://authlib-injector.yushi.moe/artifact/latest.json";

    public static async Task<string> EnsureAsync(string directory, DownloadSources sources, CancellationToken ct)
    {
        Directory.CreateDirectory(directory);
        JsonElement latest = default;
        Exception? last = null;
        foreach (var url in sources.Candidates(LatestUrl))
        {
            try { latest = await Http.GetJsonAsync<JsonElement>(url, ct).ConfigureAwait(false); last = null; break; }
            catch (Exception e) when (e is HttpRequestException or TimeoutException or JsonException) { last = e; }
        }
        if (last is not null)
        {
            var existing = Directory.EnumerateFiles(directory, "authlib-injector-*.jar").OrderByDescending(f => f).FirstOrDefault();
            return existing ?? throw AppException.Of("ygg.injector", "authlib-injector could not be downloaded.");
        }

        var version = latest.GetProperty("version").GetString()!;
        var target = SafeFile(directory, $"authlib-injector-{version}.jar");
        var sha256 = latest.GetProperty("checksums").GetProperty("sha256").GetString()!;
        if (File.Exists(target) && await Sha256Async(target, ct).ConfigureAwait(false) == sha256) return target;

        var downloadUrl = latest.GetProperty("download_url").GetString()!;
        await new Downloader(sources, 1).RunAsync([new DownloadItem(downloadUrl, target)], null, ct).ConfigureAwait(false);
        if (await Sha256Async(target, ct).ConfigureAwait(false) != sha256)
        {
            AtomicFile.TryDelete(target);
            throw new IntegrityException("authlib-injector sha256");
        }
        foreach (var old in Directory.EnumerateFiles(directory, "authlib-injector-*.jar").Where(f => !f.Equals(target, StringComparison.OrdinalIgnoreCase)))
            AtomicFile.TryDelete(old);
        return target;
    }

    private static string SafeFile(string dir, string name) => Minecraft.SafePath.Combine(dir, name);

    private static async Task<string> Sha256Async(string path, CancellationToken ct)
    {
        await using var fs = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false));
    }
}
