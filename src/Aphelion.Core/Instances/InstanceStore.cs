using System.Text.Json;
using Aphelion.Core.Logging;
using Aphelion.Core.Minecraft;
using Aphelion.Core.Storage;

namespace Aphelion.Core.Instances;

/// <summary>Discovers and edits instances inside a game root. Reads versions created by any launcher.</summary>
public sealed class InstanceStore
{
    private static readonly string[] IsolationHints = ["saves", "mods", "options.txt", "config", "resourcepacks", "shaderpacks"];
    private static readonly string[] Palette = ["#FF5B14", "#4FA3FF", "#F2B33D", "#6FCF97", "#B48CFF", "#E6ECE8", "#FF8A57", "#5FD4D0"];

    public static List<Instance> List(GameRoot root)
    {
        var result = new List<Instance>();
        if (!Directory.Exists(root.Versions)) return result;
        foreach (var dir in Directory.EnumerateDirectories(root.Versions))
        {
            var id = Path.GetFileName(dir);
            if (!File.Exists(Path.Combine(dir, id + ".json"))) continue;
            try { result.Add(Load(root, id)); }
            catch (Exception e)
            {
                Log.Warn($"Cannot read version {id}", e);
                result.Add(new Instance { Id = id, RootPath = root.Path, Name = id, Directory = dir, Error = e.Message });
            }
        }
        return result.OrderByDescending(i => i.LastPlayed ?? DateTimeOffset.MinValue).ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static Instance Load(GameRoot root, string id)
    {
        var dir = root.VersionDir(id);
        var meta = ReadMeta(dir) ?? new InstanceMeta();
        string? gameVersion = meta.GameVersion;
        LoaderInfo? loader = meta.Loader;
        string? type = null;
        string? error = null;

        try
        {
            var resolved = VersionResolver.Resolve(root, id);
            gameVersion ??= VersionResolver.GameVersionOf(resolved);
            loader ??= DetectLoader(resolved.Json);
            type = resolved.Json.Type;
        }
        catch (Exception e) when (e is AppException or JsonException or IOException or InvalidDataException)
        {
            error = e.Message;
            loader ??= new LoaderInfo { Kind = LoaderKind.Unknown };
        }

        var isolated = meta.Isolated ?? GuessIsolation(dir, loader);
        return new Instance
        {
            Id = id,
            RootPath = root.Path,
            Name = string.IsNullOrWhiteSpace(meta.DisplayName) ? id : meta.DisplayName,
            Directory = dir,
            GameVersion = gameVersion,
            Loader = loader,
            Isolated = isolated,
            Color = meta.Color ?? Palette[(int)((uint)StableHash(id) % Palette.Length)],
            LastPlayed = meta.LastPlayed,
            Created = meta.Created ?? SafeCreationTime(dir),
            PlaySeconds = meta.PlaySeconds,
            ReleaseType = type,
            Error = error,
            HasAphelionMeta = File.Exists(Path.Combine(dir, InstanceMeta.FileName)),
            Meta = meta,
        };
    }

    public static InstanceMeta? ReadMeta(string versionDir) =>
        AtomicFile.ReadJson<InstanceMeta>(Path.Combine(versionDir, InstanceMeta.FileName));

    public static void WriteMeta(string versionDir, InstanceMeta meta)
    {
        meta.Schema = InstanceMeta.CurrentSchema;
        AtomicFile.WriteJson(Path.Combine(versionDir, InstanceMeta.FileName), meta);
    }

    public static InstanceMeta UpdateMeta(GameRoot root, string id, Action<InstanceMeta> mutate)
    {
        var dir = root.VersionDir(id);
        if (!Directory.Exists(dir)) throw AppException.Of("instance.notFound", $"Instance '{id}' does not exist", ("id", id));
        var current = Load(root, id);
        var meta = ReadMeta(dir) ?? new InstanceMeta
        {
            // First write for a version made by another launcher: freeze what we inferred so behaviour stays stable.
            Isolated = current.Isolated,
            GameVersion = current.GameVersion,
            Loader = current.Loader,
            Created = current.Created,
        };
        mutate(meta);
        WriteMeta(dir, meta);
        return meta;
    }

    public static LoaderInfo DetectLoader(VersionJson json)
    {
        foreach (var lib in json.Libraries)
        {
            if (!MavenCoordinate.TryParse(lib.Name, out var c)) continue;
            switch (c.Group, c.Artifact)
            {
                case ("net.fabricmc", "fabric-loader"): return new LoaderInfo { Kind = LoaderKind.Fabric, Version = c.Version };
                case ("org.quiltmc", "quilt-loader"): return new LoaderInfo { Kind = LoaderKind.Quilt, Version = c.Version };
                case ("net.neoforged", "neoforge"): return new LoaderInfo { Kind = LoaderKind.NeoForge, Version = c.Version };
                case ("net.neoforged.fancymodloader", _): return new LoaderInfo { Kind = LoaderKind.NeoForge };
                case ("net.minecraftforge", "forge") or ("net.minecraftforge", "fmlloader"):
                    return new LoaderInfo { Kind = LoaderKind.Forge, Version = ForgeVersionFrom(c.Version) };
                case ("optifine", "OptiFine"): return new LoaderInfo { Kind = LoaderKind.OptiFine, Version = c.Version };
            }
        }
        if (json.MainClass == "net.minecraft.launchwrapper.Launch" && (json.MinecraftArguments ?? "").Contains("FMLTweaker", StringComparison.Ordinal))
            return new LoaderInfo { Kind = LoaderKind.LegacyForge };
        return new LoaderInfo { Kind = LoaderKind.Vanilla };
    }

    private static string ForgeVersionFrom(string version)
    {
        // "1.20.1-47.3.0" → "47.3.0"
        var dash = version.IndexOf('-', StringComparison.Ordinal);
        return dash >= 0 ? version[(dash + 1)..] : version;
    }

    private static bool GuessIsolation(string dir, LoaderInfo loader)
    {
        if (IsolationHints.Any(h => Path.Exists(Path.Combine(dir, h)))) return true;
        return loader.Kind is not (LoaderKind.Vanilla or LoaderKind.Unknown);
    }

    private static DateTimeOffset? SafeCreationTime(string dir)
    {
        try { return Directory.GetCreationTimeUtc(dir); }
        catch (IOException) { return null; }
    }

    private static int StableHash(string s)
    {
        unchecked
        {
            var h = 23;
            foreach (var ch in s) h = h * 31 + ch;
            return h;
        }
    }

    /// <summary>Validates a proposed instance folder name against the file system and existing instances.</summary>
    public static string? ValidateNewId(GameRoot root, string id)
    {
        if (!SafePath.IsValidName(id)) return "instance.invalidName";
        if (Directory.Exists(Path.Combine(root.Versions, id))) return "instance.exists";
        return null;
    }

    /// <summary>
    /// Renames the folder, the version JSON/jar and the id inside the JSON. Other versions that inherit from
    /// this one are updated too. Rolls the folder move back if the JSON rewrite fails.
    /// </summary>
    public static void Rename(GameRoot root, string id, string newId)
    {
        if (string.Equals(id, newId, StringComparison.Ordinal)) return;
        var problem = string.Equals(id, newId, StringComparison.OrdinalIgnoreCase) ? (SafePath.IsValidName(newId) ? null : "instance.invalidName") : ValidateNewId(root, newId);
        if (problem is not null) throw AppException.Of(problem, $"Cannot rename to '{newId}'", ("id", newId));

        var from = root.VersionDir(id);
        var to = Path.Combine(root.Versions, newId);
        // Two-step move so case-only renames work on case-insensitive file systems.
        var staging = Path.Combine(root.Versions, ".rename-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.Move(from, staging);
        try
        {
            Directory.Move(staging, to);
            File.Move(Path.Combine(to, id + ".json"), Path.Combine(to, newId + ".json"));
            if (File.Exists(Path.Combine(to, id + ".jar"))) File.Move(Path.Combine(to, id + ".jar"), Path.Combine(to, newId + ".jar"));
            RewriteId(Path.Combine(to, newId + ".json"), newId);
        }
        catch
        {
            if (Directory.Exists(to) && !Directory.Exists(from))
            {
                if (File.Exists(Path.Combine(to, newId + ".json")) && !File.Exists(Path.Combine(to, id + ".json")))
                    File.Move(Path.Combine(to, newId + ".json"), Path.Combine(to, id + ".json"));
                if (File.Exists(Path.Combine(to, newId + ".jar")) && !File.Exists(Path.Combine(to, id + ".jar")))
                    File.Move(Path.Combine(to, newId + ".jar"), Path.Combine(to, id + ".jar"));
                Directory.Move(to, from);
            }
            else if (Directory.Exists(staging)) Directory.Move(staging, from);
            throw;
        }

        // Children cannot be resolved while their parent has its old name, so read their files directly.
        foreach (var dir in Directory.EnumerateDirectories(root.Versions))
        {
            var otherId = Path.GetFileName(dir);
            var path = Path.Combine(dir, otherId + ".json");
            if (otherId.StartsWith('.') || !File.Exists(path)) continue;
            try
            {
                var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))?.AsObject();
                if (json?["inheritsFrom"]?.GetValue<string>() == id)
                {
                    json["inheritsFrom"] = newId;
                    AtomicFile.WriteAllText(path, json.ToJsonString(Json.VersionFile));
                }
            }
            catch (Exception e) when (e is JsonException or IOException or InvalidOperationException)
            {
                Log.Warn($"Could not update the parent reference in {otherId}", e);
            }
        }
    }

    private static void RewriteId(string jsonPath, string newId)
    {
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(jsonPath))?.AsObject()
                   ?? throw new InvalidDataException("Version JSON is empty");
        json["id"] = newId;
        AtomicFile.WriteAllText(jsonPath, json.ToJsonString(Json.VersionFile), keepBackup: false);
    }
}
