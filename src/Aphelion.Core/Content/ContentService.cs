using System.Collections.Concurrent;
using Aphelion.Core.Install;
using Aphelion.Core.Instances;
using Aphelion.Core.Logging;
using Aphelion.Core.Minecraft;
using Aphelion.Core.Net;

namespace Aphelion.Core.Content;

public sealed record LocalContent(
    string FileName, string Folder, bool Enabled, long Size, string Sha1, DateTimeOffset Modified,
    string? Id, string? Name, string? Version, string? Description, string? Authors, string? Loader, string? IconDataUrl,
    string? ProjectId);

public sealed record ContentUpdate(string FileName, string Folder, string ProjectId, string CurrentVersion, ModrinthVersion Latest);

/// <summary>Mods, resource packs and shader packs of an instance: listing, toggling, installing and updating.</summary>
public sealed class ContentService(ModrinthClient modrinth, Func<Downloader> downloaderFactory, Action<string> recycle)
{
    public static readonly string[] Folders = ["mods", "resourcepacks", "shaderpacks"];
    private const string DisabledSuffix = ".disabled";

    private readonly ConcurrentDictionary<(string Path, long Size, DateTime Time), (string Sha1, ContentMetadata? Meta)> _cache = new();

    public async Task<List<LocalContent>> ListAsync(Instance instance, string folder, CancellationToken ct)
    {
        if (!Folders.Contains(folder)) throw AppException.Of("content.folder", "Unknown content folder", ("folder", folder));
        var dir = Path.Combine(instance.GameDirectory, folder);
        if (!Directory.Exists(dir)) return [];
        var meta = InstanceStore.ReadMeta(instance.Directory) ?? new InstanceMeta();
        var files = new DirectoryInfo(dir).EnumerateFiles()
            .Where(f => IsContentFile(f.Name, folder))
            .ToList();

        var result = new ConcurrentBag<LocalContent>();
        await Parallel.ForEachAsync(files, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, async (f, token) =>
        {
            var key = (f.FullName, f.Length, f.LastWriteTimeUtc);
            if (!_cache.TryGetValue(key, out var info))
            {
                var sha1 = await Hashing.Sha1FileAsync(f.FullName, token).ConfigureAwait(false);
                var md = folder == "shaderpacks" ? null : ContentMetadataReader.Read(f.FullName);
                info = (sha1, md);
                _cache[key] = info;
            }
            var enabled = !f.Name.EndsWith(DisabledSuffix, StringComparison.OrdinalIgnoreCase);
            var relative = $"{folder}/{BaseName(f.Name)}";
            meta.Content.TryGetValue(relative, out var source);
            var icon = info.Meta?.Icon is { } png ? "data:image/png;base64," + Convert.ToBase64String(png) : null;
            result.Add(new LocalContent(f.Name, folder, enabled, f.Length, info.Sha1, f.LastWriteTimeUtc,
                info.Meta?.Id, info.Meta?.Name, info.Meta?.Version, info.Meta?.Description, info.Meta?.Authors, info.Meta?.Loader, icon,
                source?.ProjectId));
        }).ConfigureAwait(false);
        return result.OrderBy(c => c.Name ?? c.FileName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static bool IsContentFile(string name, string folder)
    {
        var n = BaseName(name);
        return folder switch
        {
            "mods" => n.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) || n.EndsWith(".litemod", StringComparison.OrdinalIgnoreCase),
            _ => n.EndsWith(".zip", StringComparison.OrdinalIgnoreCase),
        };
    }

    private static string BaseName(string fileName) =>
        fileName.EndsWith(DisabledSuffix, StringComparison.OrdinalIgnoreCase) ? fileName[..^DisabledSuffix.Length] : fileName;

    public void SetEnabled(Instance instance, string folder, string fileName, bool enabled)
    {
        var dir = Path.Combine(instance.GameDirectory, folder);
        var current = SafePath.Combine(dir, fileName);
        if (!File.Exists(current)) throw AppException.Of("content.missing", $"{fileName} no longer exists", ("file", fileName));
        var target = SafePath.Combine(dir, enabled ? BaseName(fileName) : BaseName(fileName) + DisabledSuffix);
        if (string.Equals(current, target, StringComparison.OrdinalIgnoreCase)) return;
        if (File.Exists(target)) throw AppException.Of("content.conflict", $"{Path.GetFileName(target)} already exists", ("file", Path.GetFileName(target)));
        File.Move(current, target);
    }

    public void Remove(Instance instance, string folder, string fileName)
    {
        var path = SafePath.Combine(Path.Combine(instance.GameDirectory, folder), fileName);
        if (!File.Exists(path)) return;
        recycle(path);
        InstanceStore.UpdateMeta(new GameRoot(instance.RootPath), instance.Id, m => m.Content.Remove($"{folder}/{BaseName(fileName)}"));
    }

    /// <summary>Copies files the user dropped onto the instance into the right folder.</summary>
    public List<string> Import(Instance instance, string folder, IEnumerable<string> sources)
    {
        var dir = Path.Combine(instance.GameDirectory, folder);
        Directory.CreateDirectory(dir);
        var copied = new List<string>();
        foreach (var src in sources)
        {
            var name = Path.GetFileName(src);
            if (!IsContentFile(name, folder)) continue;
            var target = SafePath.Combine(dir, name);
            File.Copy(src, target, overwrite: true);
            copied.Add(name);
        }
        return copied;
    }

    /// <summary>Picks the newest version of a project that fits the instance (release preferred over beta/alpha).</summary>
    public async Task<ModrinthVersion?> BestVersionAsync(string projectId, ProjectType type, Instance instance, CancellationToken ct)
    {
        var loader = type is ProjectType.Mod ? ModrinthClient.LoaderSlug(instance.Loader.Kind) : null;
        if (type == ProjectType.Mod && loader is null) throw AppException.Of("content.noLoader", "This instance has no mod loader.");
        var versions = await modrinth.GetVersionsAsync(projectId, instance.GameVersion, loader, ct).ConfigureAwait(false);
        return versions
            .OrderBy(v => v.VersionType switch { "release" => 0, "beta" => 1, _ => 2 })
            .ThenByDescending(v => v.DatePublished)
            .FirstOrDefault();
    }

    /// <summary>
    /// Installs a version and its required dependencies. Dependencies already present (installed by Aphelion or
    /// recognised by hash) are skipped; a project already installed in another version is replaced.
    /// </summary>
    public async Task<List<string>> InstallAsync(Instance instance, ProjectType type, string projectId, string? versionId,
        IProgress<StageProgress>? progress, CancellationToken ct)
    {
        var folder = ModrinthClient.FolderFor(type);
        var root = new GameRoot(instance.RootPath);
        var present = await PresentProjectsAsync(instance, folder, ct).ConfigureAwait(false);

        var plan = new List<ModrinthVersion>();
        var visited = new HashSet<string>();
        async Task Visit(string pid, string? vid, int depth)
        {
            if (depth > 6 || !visited.Add(pid)) return;
            if (depth > 0 && present.ContainsKey(pid)) return;
            var version = vid is not null
                ? await modrinth.GetVersionAsync(vid, ct).ConfigureAwait(false)
                : await BestVersionAsync(pid, type, instance, ct).ConfigureAwait(false);
            if (version is null)
            {
                if (depth == 0) throw AppException.Of("content.noCompatible", "No version fits this instance's game version and loader.");
                Log.Warn($"Dependency {pid} has no compatible version; skipped");
                return;
            }
            plan.Add(version);
            foreach (var dep in version.Dependencies.Where(d => d.DependencyType == "required"))
            {
                var depProject = dep.ProjectId;
                if (depProject is null && dep.VersionId is not null)
                    depProject = (await modrinth.GetVersionAsync(dep.VersionId, ct).ConfigureAwait(false)).ProjectId;
                if (depProject is not null) await Visit(depProject, dep.VersionId, depth + 1).ConfigureAwait(false);
            }
        }
        progress?.Report(new StageProgress("content.resolve"));
        await Visit(projectId, versionId, 0).ConfigureAwait(false);

        var dir = Path.Combine(instance.GameDirectory, folder);
        Directory.CreateDirectory(dir);
        var items = new List<DownloadItem>();
        foreach (var v in plan)
        {
            var file = v.PrimaryFile ?? throw AppException.Of("content.noFile", "The version has no downloadable file.");
            file.Hashes.TryGetValue("sha1", out var sha1);
            items.Add(new DownloadItem(file.Url, SafePath.Combine(dir, Path.GetFileName(file.Filename)), sha1, file.Size));
        }
        progress?.Report(new StageProgress("content.download"));
        await downloaderFactory().RunAsync(items, progress.ForDownloads("content.download"), ct).ConfigureAwait(false);

        // Replace older files of the same projects only after the new ones are safely on disk.
        var installed = new List<string>();
        InstanceStore.UpdateMeta(root, instance.Id, m =>
        {
            foreach (var v in plan)
            {
                var file = v.PrimaryFile!;
                var name = Path.GetFileName(file.Filename);
                if (present.TryGetValue(v.ProjectId, out var oldFile) && !string.Equals(oldFile, name, StringComparison.OrdinalIgnoreCase))
                {
                    var oldPath = Path.Combine(dir, oldFile);
                    if (File.Exists(oldPath)) recycle(oldPath);
                    if (File.Exists(oldPath + DisabledSuffix)) recycle(oldPath + DisabledSuffix);
                    m.Content.Remove($"{folder}/{oldFile}");
                }
                file.Hashes.TryGetValue("sha1", out var sha1);
                m.Content[$"{folder}/{name}"] = new ContentSource { ProjectId = v.ProjectId, VersionId = v.Id, Sha1 = sha1 };
                installed.Add(name);
            }
        });
        Log.Info($"Installed {string.Join(", ", installed)} into {instance.Id}/{folder}");
        return installed;
    }

    /// <summary>Project id → file name for content already in the folder (Aphelion records first, then a hash lookup).</summary>
    private async Task<Dictionary<string, string>> PresentProjectsAsync(Instance instance, string folder, CancellationToken ct)
    {
        var present = new Dictionary<string, string>();
        var meta = InstanceStore.ReadMeta(instance.Directory) ?? new InstanceMeta();
        var local = await ListAsync(instance, folder, ct).ConfigureAwait(false);
        foreach (var c in local)
        {
            if (meta.Content.TryGetValue($"{folder}/{BaseName(c.FileName)}", out var s)) present[s.ProjectId] = BaseName(c.FileName);
        }
        var unknown = local.Where(c => !present.ContainsValue(BaseName(c.FileName))).ToList();
        if (unknown.Count > 0)
        {
            try
            {
                var identified = await modrinth.IdentifyAsync(unknown.Select(c => c.Sha1).ToList(), ct).ConfigureAwait(false);
                foreach (var c in unknown)
                    if (identified.TryGetValue(c.Sha1, out var v)) present.TryAdd(v.ProjectId, BaseName(c.FileName));
            }
            catch (Exception e) when (e is HttpRequestException or TimeoutException)
            {
                Log.Warn("Could not identify installed files on Modrinth", e);
            }
        }
        return present;
    }

    /// <summary>Finds newer compatible versions for installed content that Modrinth recognises.</summary>
    public async Task<List<ContentUpdate>> CheckUpdatesAsync(Instance instance, string folder, CancellationToken ct)
    {
        var local = await ListAsync(instance, folder, ct).ConfigureAwait(false);
        if (local.Count == 0) return [];
        var identified = await modrinth.IdentifyAsync(local.Select(c => c.Sha1).ToList(), ct).ConfigureAwait(false);
        var type = folder switch { "resourcepacks" => ProjectType.ResourcePack, "shaderpacks" => ProjectType.Shader, _ => ProjectType.Mod };
        var updates = new ConcurrentBag<ContentUpdate>();
        await Parallel.ForEachAsync(local.Where(c => identified.ContainsKey(c.Sha1)), new ParallelOptions { MaxDegreeOfParallelism = 6, CancellationToken = ct }, async (c, token) =>
        {
            var current = identified[c.Sha1];
            try
            {
                var best = await BestVersionAsync(current.ProjectId, type, instance, token).ConfigureAwait(false);
                if (best is not null && best.Id != current.Id && best.DatePublished > current.DatePublished)
                    updates.Add(new ContentUpdate(c.FileName, folder, current.ProjectId, current.VersionNumber, best));
            }
            catch (Exception e) when (e is HttpRequestException or TimeoutException or AppException)
            {
                Log.Debug($"Update check skipped for {c.FileName}: {e.Message}");
            }
        }).ConfigureAwait(false);
        return updates.OrderBy(u => u.FileName, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
