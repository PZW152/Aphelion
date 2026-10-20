using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Aphelion.Core.Instances;
using Aphelion.Core.Minecraft;

namespace Aphelion.Core.Install;

public sealed record LoaderVersion(string Version, bool Stable, string? Label = null);

/// <summary>Lists loader versions available for a given Minecraft release.</summary>
public sealed partial class LoaderCatalog(MojangMeta meta)
{
    public const string FabricMeta = "https://meta.fabricmc.net/v2";
    public const string QuiltMeta = "https://meta.quiltmc.org/v3";
    public const string ForgeMaven = "https://maven.minecraftforge.net/net/minecraftforge/forge";
    public const string ForgeMetadataJson = "https://files.minecraftforge.net/net/minecraftforge/forge/maven-metadata.json";
    public const string ForgePromotions = "https://files.minecraftforge.net/net/minecraftforge/forge/promotions_slim.json";
    public const string NeoForgeMaven = "https://maven.neoforged.net/releases/net/neoforged/neoforge";

    public Task<List<LoaderVersion>> ListAsync(LoaderKind kind, string gameVersion, CancellationToken ct) => kind switch
    {
        LoaderKind.Fabric => FabricLikeAsync($"{FabricMeta}/versions/loader/{Uri.EscapeDataString(gameVersion)}", ct),
        LoaderKind.Quilt => FabricLikeAsync($"{QuiltMeta}/versions/loader/{Uri.EscapeDataString(gameVersion)}", ct),
        LoaderKind.Forge => ForgeAsync(gameVersion, ct),
        LoaderKind.NeoForge => NeoForgeAsync(gameVersion, ct),
        _ => Task.FromResult(new List<LoaderVersion>()),
    };

    private async Task<List<LoaderVersion>> FabricLikeAsync(string url, CancellationToken ct)
    {
        var doc = await meta.FetchJsonAsync<JsonElement>(url, ct).ConfigureAwait(false);
        var list = new List<LoaderVersion>();
        if (doc.ValueKind != JsonValueKind.Array) return list;
        foreach (var entry in doc.EnumerateArray())
        {
            if (!entry.TryGetProperty("loader", out var loader)) continue;
            var version = loader.GetProperty("version").GetString();
            if (version is null) continue;
            // Quilt has no "stable" flag; treat versions without a pre-release suffix as stable.
            var stable = loader.TryGetProperty("stable", out var s) ? s.GetBoolean() : !version.Contains('-', StringComparison.Ordinal);
            list.Add(new LoaderVersion(version, stable));
        }
        return list;
    }

    private async Task<List<LoaderVersion>> ForgeAsync(string gameVersion, CancellationToken ct)
    {
        var all = await meta.FetchJsonAsync<Dictionary<string, List<string>>>(ForgeMetadataJson, ct).ConfigureAwait(false);
        if (!all.TryGetValue(gameVersion, out var versions)) return [];
        Dictionary<string, string> promos = [];
        try
        {
            var p = await meta.FetchJsonAsync<JsonElement>(ForgePromotions, ct).ConfigureAwait(false);
            if (p.TryGetProperty("promos", out var map))
                foreach (var prop in map.EnumerateObject()) promos[prop.Name] = prop.Value.GetString() ?? "";
        }
        catch (Exception e) when (e is HttpRequestException or TimeoutException or JsonException) { }

        promos.TryGetValue($"{gameVersion}-recommended", out var recommended);
        promos.TryGetValue($"{gameVersion}-latest", out var latest);
        return versions
            .Select(full => full.StartsWith(gameVersion + "-", StringComparison.Ordinal) ? full[(gameVersion.Length + 1)..] : full)
            .Reverse()
            .Select(v => new LoaderVersion(v, true, v == recommended ? "recommended" : v == latest ? "latest" : null))
            .ToList();
    }

    private async Task<List<LoaderVersion>> NeoForgeAsync(string gameVersion, CancellationToken ct)
    {
        var xml = await meta.FetchStringAsync($"{NeoForgeMaven}/maven-metadata.xml", ct).ConfigureAwait(false);
        var doc = XDocument.Parse(xml);
        var versions = doc.Descendants("version").Select(e => e.Value.Trim()).Where(v => v.Length > 0);
        return versions
            .Where(v => GameVersionOfNeoForge(v) == gameVersion)
            .Reverse()
            .Select(v => new LoaderVersion(v, !v.Contains("beta", StringComparison.OrdinalIgnoreCase) && !v.Contains("alpha", StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    /// <summary>NeoForge "21.1.77" targets Minecraft 1.21.1, "20.4.237" targets 1.20.4, "21.0.x" targets 1.21.</summary>
    public static string? GameVersionOfNeoForge(string neoVersion)
    {
        var m = NeoVersionPattern().Match(neoVersion);
        if (!m.Success) return null;
        var major = int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        var minor = int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
        if (major >= 26) return minor == 0 ? $"{major}.1" : $"{major}.{minor}"; // year-based Minecraft versions
        return minor == 0 ? $"1.{major}" : $"1.{major}.{minor}";
    }

    [GeneratedRegex(@"^(\d+)\.(\d+)\.")]
    private static partial Regex NeoVersionPattern();

    public static string ForgeInstallerUrl(string gameVersion, string forgeVersion)
    {
        var full = $"{gameVersion}-{forgeVersion}";
        return $"{ForgeMaven}/{full}/forge-{full}-installer.jar";
    }

    public static string NeoForgeInstallerUrl(string version) => $"{NeoForgeMaven}/{version}/neoforge-{version}-installer.jar";

    public static string FabricProfileUrl(string gameVersion, string loader) =>
        $"{FabricMeta}/versions/loader/{Uri.EscapeDataString(gameVersion)}/{Uri.EscapeDataString(loader)}/profile/json";

    public static string QuiltProfileUrl(string gameVersion, string loader) =>
        $"{QuiltMeta}/versions/loader/{Uri.EscapeDataString(gameVersion)}/{Uri.EscapeDataString(loader)}/profile/json";
}
