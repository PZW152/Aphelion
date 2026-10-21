using System.Text.Json;
using Aphelion.Core.Storage;

namespace Aphelion.Core.Minecraft;

/// <summary>A version with its <c>inheritsFrom</c> chain flattened into one effective definition.</summary>
public sealed class ResolvedVersion
{
    public required VersionJson Json { get; init; }
    /// <summary>Ids from the requested version down to its root ancestor.</summary>
    public required IReadOnlyList<string> Chain { get; init; }
    /// <summary>Version folder whose jar is the game client.</summary>
    public required string ClientJarId { get; init; }
    public string Id => Json.Id;
}

public static class VersionResolver
{
    public const int MaxDepth = 8;

    public static VersionJson ReadVersionFile(string path)
    {
        using var fs = File.OpenRead(path);
        return JsonSerializer.Deserialize<VersionJson>(fs, Json.External) ?? throw new InvalidDataException($"Empty version file {path}");
    }

    public static ResolvedVersion Resolve(GameRoot root, string id)
    {
        var chain = new List<VersionJson>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? current = id;
        while (current is not null)
        {
            if (!seen.Add(current)) throw AppException.Of("version.cycle", $"Version inheritance loops at '{current}'", ("id", current));
            if (chain.Count >= MaxDepth) throw AppException.Of("version.depth", "Version inheritance is too deep", ("id", id));
            var path = root.VersionJsonPath(current);
            if (!File.Exists(path))
                throw AppException.Of("version.missingParent", $"Version '{current}' required by '{id}' is not installed", ("id", current), ("child", id));
            var json = ReadVersionFile(path);
            if (string.IsNullOrEmpty(json.Id)) json.Id = current;
            chain.Add(json);
            current = json.InheritsFrom;
        }
        return Flatten(chain);
    }

    /// <summary>Merges a chain ordered child → root.</summary>
    public static ResolvedVersion Flatten(IReadOnlyList<VersionJson> chain)
    {
        var merged = chain[^1];
        for (var i = chain.Count - 2; i >= 0; i--) merged = Merge(merged, chain[i]);

        // The client jar belongs to the most derived version that declares one (or an explicit legacy "jar").
        var jarId = chain.FirstOrDefault(v => !string.IsNullOrEmpty(v.Jar))?.Jar
                    ?? chain.FirstOrDefault(v => v.Downloads?.Client is not null)?.Id
                    ?? chain[^1].Id;
        merged.InheritsFrom = null;
        return new ResolvedVersion { Json = merged, Chain = chain.Select(c => c.Id).ToList(), ClientJarId = jarId };
    }

    /// <summary>Overlay <paramref name="child"/> onto <paramref name="parent"/>. The child's libraries win on conflicts.</summary>
    public static VersionJson Merge(VersionJson parent, VersionJson child)
    {
        var libraries = new List<Library>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var lib in child.Libraries.Concat(parent.Libraries))
        {
            var key = LibraryKey(lib);
            if (keys.Add(key)) libraries.Add(lib);
        }

        VersionArguments? arguments = null;
        if (parent.Arguments is not null || child.Arguments is not null)
        {
            arguments = new VersionArguments
            {
                Game = [.. parent.Arguments?.Game ?? [], .. child.Arguments?.Game ?? []],
                Jvm = [.. parent.Arguments?.Jvm ?? [], .. child.Arguments?.Jvm ?? []],
            };
        }

        return new VersionJson
        {
            Id = child.Id,
            InheritsFrom = parent.InheritsFrom,
            Type = child.Type ?? parent.Type,
            MainClass = child.MainClass ?? parent.MainClass,
            MinecraftArguments = child.MinecraftArguments ?? parent.MinecraftArguments,
            Arguments = arguments,
            Libraries = libraries,
            AssetIndex = child.AssetIndex ?? parent.AssetIndex,
            Assets = child.Assets ?? parent.Assets,
            Downloads = child.Downloads ?? parent.Downloads,
            JavaVersion = child.JavaVersion ?? parent.JavaVersion,
            Logging = child.Logging ?? parent.Logging,
            Jar = child.Jar ?? parent.Jar,
            ReleaseTime = child.ReleaseTime ?? parent.ReleaseTime,
            Time = child.Time ?? parent.Time,
            ComplianceLevel = child.ComplianceLevel ?? parent.ComplianceLevel,
            MinimumLauncherVersion = child.MinimumLauncherVersion ?? parent.MinimumLauncherVersion,
            Extra = MergeExtra(parent.Extra, child.Extra),
        };
    }

    private static string LibraryKey(Library lib) =>
        MavenCoordinate.TryParse(lib.Name, out var c) ? c.Key + (lib.Natives is null ? "" : ":natives") : lib.Name;

    private static Dictionary<string, JsonElement>? MergeExtra(Dictionary<string, JsonElement>? parent, Dictionary<string, JsonElement>? child)
    {
        if (parent is null) return child;
        if (child is null) return parent;
        var result = new Dictionary<string, JsonElement>(parent);
        foreach (var (k, v) in child) result[k] = v;
        return result;
    }

    /// <summary>The Minecraft release a (possibly modded) version is built on.</summary>
    public static string? GameVersionOf(ResolvedVersion version)
    {
        var extra = version.Json.Extra;
        // "aphelionGameVersion" is ours; "clientVersion" is written by other launchers for merged versions.
        foreach (var key in new[] { "aphelionGameVersion", "clientVersion" })
        {
            if (extra is not null && extra.TryGetValue(key, out var gv) && gv.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(gv.GetString()))
                return gv.GetString();
        }
        if (extra is not null && extra.TryGetValue("patches", out var patches) && patches.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in patches.EnumerateArray())
            {
                if (p.ValueKind == JsonValueKind.Object && p.TryGetProperty("id", out var pid) && pid.GetString() == "game"
                    && p.TryGetProperty("version", out var pv) && pv.ValueKind == JsonValueKind.String)
                    return pv.GetString();
            }
        }
        return version.Chain.Count > 1 ? version.Chain[^1] : version.ClientJarId;
    }
}
