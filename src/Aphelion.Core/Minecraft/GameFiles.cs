using System.Text.Json;
using Aphelion.Core.Net;
using Aphelion.Core.Storage;

namespace Aphelion.Core.Minecraft;

/// <summary>Builds the complete list of files a resolved version needs and finds the ones that are missing.</summary>
public static class GameFiles
{
    public static async Task<List<DownloadItem>> CollectAsync(GameRoot root, ResolvedVersion version, RuntimePlatform platform,
        MojangMeta meta, CancellationToken ct)
    {
        var items = new List<DownloadItem>();
        var json = version.Json;

        if (json.Downloads?.Client is { Url: { Length: > 0 } url } client)
            items.Add(new DownloadItem(url, root.VersionJarPath(version.ClientJarId), client.Sha1, client.Size));

        foreach (var lib in LibraryResolver.ResolveAll(json, root, platform))
        {
            if (lib.Artifact is { Url: not null } a) items.Add(new DownloadItem(a.Url, a.Path, a.Sha1, a.Size));
            if (lib.Native is { Url: not null } n) items.Add(new DownloadItem(n.Url, n.Path, n.Sha1, n.Size));
        }

        if (json.Logging?.Client?.File is { Url: { Length: > 0 } logUrl } logFile)
            items.Add(new DownloadItem(logUrl, Path.Combine(root.LogConfigs, logFile.Id), logFile.Sha1, logFile.Size));

        if (json.AssetIndex is { } assetIndex)
        {
            var index = await LoadAssetIndexAsync(root, assetIndex, meta, ct).ConfigureAwait(false);
            foreach (var obj in index.Objects.Values.DistinctBy(o => o.Hash))
            {
                if (obj.Hash.Length != 40) continue;
                items.Add(new DownloadItem($"https://resources.download.minecraft.net/{obj.Hash[..2]}/{obj.Hash}", root.AssetObjectPath(obj.Hash), obj.Hash, obj.Size));
            }
        }
        return items;
    }

    /// <summary>Files whose presence cannot be restored by downloading (no URL) and are missing — the install is broken.</summary>
    public static List<string> MissingLocalOnly(GameRoot root, ResolvedVersion version, RuntimePlatform platform)
    {
        var missing = new List<string>();
        foreach (var lib in LibraryResolver.ResolveAll(version.Json, root, platform))
        {
            if (lib.Artifact is { Url: null } a && !File.Exists(a.Path)) missing.Add(a.Path);
        }
        if (!File.Exists(root.VersionJarPath(version.ClientJarId)) && version.Json.Downloads?.Client is null)
            missing.Add(root.VersionJarPath(version.ClientJarId));
        return missing;
    }

    public static async Task<AssetIndexFile> LoadAssetIndexAsync(GameRoot root, AssetIndexRef reference, MojangMeta meta, CancellationToken ct)
    {
        var path = root.AssetIndexPath(reference.Id);
        if (File.Exists(path) && (reference.Sha1 is null || string.Equals(await Hashing.Sha1FileAsync(path, ct).ConfigureAwait(false), reference.Sha1, StringComparison.OrdinalIgnoreCase)))
        {
            var cached = AtomicFile.ReadJson<AssetIndexFile>(path, Json.External);
            if (cached is not null) return cached;
        }
        var bytes = await meta.FetchBytesAsync(reference.Url, ct).ConfigureAwait(false);
        if (reference.Sha1 is not null && !string.Equals(Hashing.Sha1(bytes), reference.Sha1, StringComparison.OrdinalIgnoreCase))
            throw new IntegrityException($"asset index {reference.Id}");
        AtomicFile.WriteAllBytes(path, bytes, keepBackup: false);
        return JsonSerializer.Deserialize<AssetIndexFile>(bytes, Json.External) ?? new AssetIndexFile();
    }

    /// <summary>
    /// Old asset layouts: pre-1.6 "virtual" indexes need named copies under assets/virtual/&lt;id&gt;,
    /// and "map_to_resources" indexes need copies in the game directory's resources folder.
    /// </summary>
    public static string PrepareLegacyAssets(GameRoot root, AssetIndexRef reference, AssetIndexFile index, string gameDirectory)
    {
        if (!index.Virtual && !index.MapToResources) return root.Assets;
        var target = index.MapToResources ? Path.Combine(gameDirectory, "resources") : Path.Combine(root.Assets, "virtual", reference.Id);
        foreach (var (name, obj) in index.Objects)
        {
            var source = root.AssetObjectPath(obj.Hash);
            if (!File.Exists(source)) continue;
            string destination;
            try { destination = SafePath.Combine(target, name); }
            catch (InvalidDataException) { continue; }
            var info = new FileInfo(destination);
            if (info.Exists && info.Length == obj.Size) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination, overwrite: true);
        }
        return index.MapToResources ? root.Assets : target;
    }
}
