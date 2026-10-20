using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aphelion.Core.Install;
using Aphelion.Core.Instances;
using Aphelion.Core.Logging;
using Aphelion.Core.Minecraft;
using Aphelion.Core.Net;
using Aphelion.Core.Storage;

namespace Aphelion.Core.Content;

public sealed class MrpackIndex
{
    public int FormatVersion { get; set; } = 1;
    public string Game { get; set; } = "minecraft";
    public string VersionId { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Summary { get; set; }
    public List<MrpackFile> Files { get; set; } = [];
    public Dictionary<string, string> Dependencies { get; set; } = [];
}

public sealed class MrpackFile
{
    public string Path { get; set; } = "";
    public Dictionary<string, string> Hashes { get; set; } = [];
    public MrpackEnv? Env { get; set; }
    public List<string> Downloads { get; set; } = [];
    public long FileSize { get; set; }
}

public sealed class MrpackEnv
{
    public string Client { get; set; } = "required";
    public string Server { get; set; } = "required";
}

/// <summary>Modrinth modpacks (.mrpack): install into a new instance, or export an instance.</summary>
public sealed class ModpackService(ModrinthClient modrinth, Func<Downloader> downloaderFactory, string tempDirectory)
{
    private static readonly JsonSerializerOptions IndexJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static MrpackIndex ReadIndex(ZipArchive zip)
    {
        var entry = zip.GetEntry("modrinth.index.json") ?? throw AppException.Of("modpack.invalid", "This file is not a Modrinth modpack.");
        using var s = entry.Open();
        var index = JsonSerializer.Deserialize<MrpackIndex>(s, IndexJson) ?? throw AppException.Of("modpack.invalid", "The modpack index is empty.");
        if (index.Game != "minecraft") throw AppException.Of("modpack.invalid", $"Unsupported game '{index.Game}'.");
        if (!index.Dependencies.ContainsKey("minecraft")) throw AppException.Of("modpack.invalid", "The modpack does not name a Minecraft version.");
        return index;
    }

    public static (LoaderKind Kind, string? Version) LoaderOf(MrpackIndex index)
    {
        foreach (var (key, kind) in new[] { ("fabric-loader", LoaderKind.Fabric), ("quilt-loader", LoaderKind.Quilt), ("neoforge", LoaderKind.NeoForge), ("forge", LoaderKind.Forge) })
            if (index.Dependencies.TryGetValue(key, out var v)) return (kind, v);
        return (LoaderKind.Vanilla, null);
    }

    /// <summary>Downloads a Modrinth modpack version and installs it. Returns the local .mrpack path.</summary>
    public async Task<string> DownloadAsync(string versionId, IProgress<StageProgress>? progress, CancellationToken ct)
    {
        var version = await modrinth.GetVersionAsync(versionId, ct).ConfigureAwait(false);
        var file = version.PrimaryFile ?? throw AppException.Of("content.noFile", "The modpack version has no file.");
        var path = Path.Combine(tempDirectory, "modpack-" + Guid.NewGuid().ToString("N")[..10] + ".mrpack");
        file.Hashes.TryGetValue("sha1", out var sha1);
        progress?.Report(new StageProgress("modpack.download"));
        await downloaderFactory().RunAsync([new DownloadItem(file.Url, path, sha1, file.Size)], progress.ForDownloads("modpack.download"), ct).ConfigureAwait(false);
        return path;
    }

    public async Task<Instance> InstallAsync(string mrpackPath, string instanceId, string? displayName, InstanceInstaller installer, GameRoot root,
        IProgress<StageProgress>? progress, CancellationToken ct)
    {
        MrpackIndex index;
        using (var zip = ZipFile.OpenRead(mrpackPath)) index = ReadIndex(zip);
        var (loader, loaderVersion) = LoaderOf(index);

        var instance = await installer.CreateAsync(new CreateInstanceRequest(instanceId, index.Dependencies["minecraft"], loader, loaderVersion,
            displayName ?? index.Name), progress, ct).ConfigureAwait(false);
        try
        {
            var gameDir = instance.GameDirectory;
            var items = new List<DownloadItem>();
            foreach (var f in index.Files)
            {
                if (f.Env?.Client == "unsupported") continue;
                var target = SafePath.Combine(gameDir, f.Path);
                f.Hashes.TryGetValue("sha1", out var sha1);
                var urls = f.Downloads.Where(u => Uri.TryCreate(u, UriKind.Absolute, out var x) && x.Scheme == Uri.UriSchemeHttps).ToList();
                if (urls.Count == 0) throw AppException.Of("modpack.invalid", $"No HTTPS download for {f.Path}", ("file", f.Path));
                items.Add(new DownloadItem(urls[0], target, sha1, f.FileSize) { Fallbacks = urls.Skip(1).ToList() });
            }
            progress?.Report(new StageProgress("modpack.files"));
            await downloaderFactory().RunAsync(items, progress.ForDownloads("modpack.files"), ct).ConfigureAwait(false);

            progress?.Report(new StageProgress("modpack.overrides"));
            using (var zip = ZipFile.OpenRead(mrpackPath))
            {
                ExtractFolder(zip, "overrides/", gameDir);
                ExtractFolder(zip, "client-overrides/", gameDir);
            }

            InstanceStore.UpdateMeta(root, instance.Id, m =>
            {
                foreach (var f in index.Files.Where(f => f.Env?.Client != "unsupported"))
                {
                    var rel = f.Path.Replace('\\', '/');
                    if (f.Hashes.TryGetValue("sha1", out var sha1)) m.Content[rel] = new ContentSource { Sha1 = sha1 };
                }
            });
            Log.Info($"Modpack {index.Name} {index.VersionId} installed as {instance.Id}");
            return InstanceStore.Load(root, instance.Id);
        }
        catch
        {
            // The instance itself was created successfully; leave it so the user can retry or remove it.
            Log.Warn($"Modpack files for {instance.Id} are incomplete");
            throw;
        }
    }

    internal static void ExtractFolder(ZipArchive zip, string prefix, string destination)
    {
        foreach (var entry in zip.Entries)
        {
            if (!entry.FullName.StartsWith(prefix, StringComparison.Ordinal) || entry.FullName.EndsWith('/')) continue;
            var relative = entry.FullName[prefix.Length..];
            if (relative.Length == 0) continue;
            var target = SafePath.Combine(destination, relative); // rejects "../" and absolute paths (zip slip)
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }
    }

    /// <summary>
    /// Exports an instance: files Modrinth recognises become download references, everything else goes into
    /// overrides. Config, options and the selected content folders are included; saves are not.
    /// </summary>
    public async Task<string> ExportAsync(Instance instance, string outputPath, string versionId, string? summary, CancellationToken ct)
    {
        var index = new MrpackIndex { Name = instance.Name, VersionId = versionId, Summary = summary };
        index.Dependencies["minecraft"] = instance.GameVersion ?? throw AppException.Of("modpack.export", "The instance's Minecraft version is unknown.");
        var loaderKey = instance.Loader.Kind switch
        {
            LoaderKind.Fabric => "fabric-loader",
            LoaderKind.Quilt => "quilt-loader",
            LoaderKind.Forge => "forge",
            LoaderKind.NeoForge => "neoforge",
            _ => null,
        };
        if (loaderKey is not null && instance.Loader.Version is { } lv) index.Dependencies[loaderKey] = lv;

        var gameDir = instance.GameDirectory;
        var candidates = new List<(string Relative, string Full)>();
        foreach (var folder in ContentService.Folders)
        {
            var dir = Path.Combine(gameDir, folder);
            if (!Directory.Exists(dir)) continue;
            foreach (var f in Directory.EnumerateFiles(dir))
            {
                if (f.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)) continue;
                candidates.Add(($"{folder}/{Path.GetFileName(f)}", f));
            }
        }
        var hashes = new Dictionary<string, string>();
        foreach (var (rel, full) in candidates) hashes[rel] = await Hashing.Sha1FileAsync(full, ct).ConfigureAwait(false);
        var identified = await modrinth.IdentifyAsync(hashes.Values.Distinct().ToList(), ct).ConfigureAwait(false);

        var overrides = new List<(string Relative, string Full)>();
        foreach (var (rel, full) in candidates)
        {
            var sha1 = hashes[rel];
            var file = identified.TryGetValue(sha1, out var v) ? v.Files.FirstOrDefault(x => x.Hashes.TryGetValue("sha1", out var h) && h == sha1) : null;
            if (file is null) { overrides.Add((rel, full)); continue; }
            index.Files.Add(new MrpackFile
            {
                Path = rel,
                Hashes = new Dictionary<string, string> { ["sha1"] = sha1, ["sha512"] = await Hashing.Sha512FileAsync(full, ct).ConfigureAwait(false) },
                Downloads = [file.Url],
                FileSize = new FileInfo(full).Length,
                Env = new MrpackEnv { Client = "required", Server = "required" },
            });
        }
        foreach (var extra in new[] { "config", "defaultconfigs", "kubejs", "options.txt", "servers.dat" })
        {
            var p = Path.Combine(gameDir, extra);
            if (File.Exists(p)) overrides.Add((extra, p));
            else if (Directory.Exists(p))
                foreach (var f in Directory.EnumerateFiles(p, "*", SearchOption.AllDirectories))
                    overrides.Add((Path.GetRelativePath(gameDir, f).Replace('\\', '/'), f));
        }

        var temp = outputPath + ".part";
        await using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            var indexEntry = zip.CreateEntry("modrinth.index.json", CompressionLevel.Optimal);
            await using (var s = indexEntry.Open()) await JsonSerializer.SerializeAsync(s, index, IndexJson, ct).ConfigureAwait(false);
            foreach (var (rel, full) in overrides) zip.CreateEntryFromFile(full, "overrides/" + rel, CompressionLevel.Optimal);
        }
        File.Move(temp, outputPath, overwrite: true);
        Log.Info($"Exported {instance.Id} to {outputPath}: {index.Files.Count} referenced, {overrides.Count} overrides");
        return outputPath;
    }
}
