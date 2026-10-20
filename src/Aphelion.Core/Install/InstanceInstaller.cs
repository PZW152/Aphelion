using System.Text.Json;
using Aphelion.Core.Instances;
using Aphelion.Core.Logging;
using Aphelion.Core.Minecraft;
using Aphelion.Core.Net;
using Aphelion.Core.Storage;

namespace Aphelion.Core.Install;

public sealed record CreateInstanceRequest(string Id, string GameVersion, LoaderKind Loader = LoaderKind.Vanilla, string? LoaderVersion = null,
    string? DisplayName = null, string? Color = null, bool Isolated = true);

/// <summary>
/// Creates self-contained instances: the vanilla definition and the loader are merged into one version JSON with
/// its own client jar, so an instance is exactly one folder (shared libraries and assets stay in the game root).
/// The folder is assembled in a hidden staging directory and moved into place only when complete, so an
/// interrupted install never leaves a half-made instance behind.
/// </summary>
public sealed class InstanceInstaller(GameRoot root, MojangMeta meta, Downloader downloader, JavaResolver java, string tempDirectory)
{
    public const string StagingPrefix = ".aphelion-staging-";

    public async Task<Instance> CreateAsync(CreateInstanceRequest request, IProgress<StageProgress>? progress, CancellationToken ct)
    {
        var problem = InstanceStore.ValidateNewId(root, request.Id);
        if (problem is not null) throw AppException.Of(problem, $"Cannot create instance '{request.Id}'", ("id", request.Id));
        if (request.Loader != LoaderKind.Vanilla && string.IsNullOrWhiteSpace(request.LoaderVersion))
            throw AppException.Of("install.loaderVersion", "A loader version is required");

        Directory.CreateDirectory(root.Versions);
        var staging = Path.Combine(root.Versions, StagingPrefix + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(staging);
        try
        {
            progress?.Report(new StageProgress("install.metadata"));
            var vanilla = await FetchVanillaAsync(request.GameVersion, ct).ConfigureAwait(false);

            // The client jar is needed before Forge processors run, so it is fetched first.
            var clientJar = Path.Combine(staging, request.Id + ".jar");
            var client = vanilla.Downloads?.Client ?? throw AppException.Of("install.noClient", $"{request.GameVersion} has no client download");
            progress?.Report(new StageProgress("install.client"));
            await downloader.RunAsync([new DownloadItem(client.Url!, clientJar, client.Sha1, client.Size)], progress.ForDownloads("install.client"), ct).ConfigureAwait(false);

            var merged = vanilla;
            if (request.Loader != LoaderKind.Vanilla)
            {
                progress?.Report(new StageProgress("install.loader"));
                var loaderJson = await InstallLoaderAsync(request, vanilla, clientJar, progress, ct).ConfigureAwait(false);
                merged = VersionResolver.Merge(vanilla, loaderJson);
            }
            merged.Id = request.Id;
            merged.InheritsFrom = null;
            merged.Jar = null;
            merged.Extra ??= [];
            merged.Extra["aphelionGameVersion"] = JsonSerializer.SerializeToElement(request.GameVersion);
            var resolved = new ResolvedVersion { Json = merged, Chain = [request.Id], ClientJarId = request.Id };

            // Libraries, natives, assets and log configuration — everything else lives in the shared root.
            progress?.Report(new StageProgress("install.files"));
            var stagingRoot = new StagingView(root, staging, request.Id);
            var items = await GameFiles.CollectAsync(root, resolved, RuntimePlatform.Current, meta, ct).ConfigureAwait(false);
            items = items.Select(i => stagingRoot.Redirect(i)).ToList();
            await downloader.RunAsync(items, progress.ForDownloads("install.files"), ct).ConfigureAwait(false);

            var missing = GameFiles.MissingLocalOnly(root, resolved, RuntimePlatform.Current)
                .Where(p => !p.Equals(root.VersionJarPath(request.Id), StringComparison.OrdinalIgnoreCase)).ToList();
            if (missing.Count > 0)
                throw AppException.Of("install.incomplete", $"Installation produced no {Path.GetFileName(missing[0])}", ("file", Path.GetFileName(missing[0])));

            progress?.Report(new StageProgress("install.finalize"));
            AtomicFile.WriteAllText(Path.Combine(staging, request.Id + ".json"), JsonSerializer.Serialize(merged, Json.VersionFile), keepBackup: false);
            InstanceStore.WriteMeta(staging, new InstanceMeta
            {
                DisplayName = string.IsNullOrWhiteSpace(request.DisplayName) || request.DisplayName == request.Id ? null : request.DisplayName,
                Color = request.Color,
                Created = DateTimeOffset.UtcNow,
                Isolated = request.Isolated,
                GameVersion = request.GameVersion,
                Loader = new LoaderInfo { Kind = request.Loader, Version = request.LoaderVersion },
            });

            var final = Path.Combine(root.Versions, request.Id);
            if (Directory.Exists(final)) throw AppException.Of("instance.exists", $"Instance '{request.Id}' appeared during installation", ("id", request.Id));
            Directory.Move(staging, final);
            Log.Info($"Instance {request.Id} created ({request.GameVersion}, {request.Loader} {request.LoaderVersion})");
            return InstanceStore.Load(root, request.Id);
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                try { Directory.Delete(staging, recursive: true); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Warn($"Could not remove {staging}", e); }
            }
        }
    }

    /// <summary>Re-verifies every file of an instance by hash and downloads whatever is missing or damaged.</summary>
    public async Task<int> RepairAsync(string instanceId, IProgress<StageProgress>? progress, CancellationToken ct, VerifyMode mode = VerifyMode.Hash)
    {
        var resolved = VersionResolver.Resolve(root, instanceId);
        progress?.Report(new StageProgress("repair.scan"));
        var items = await GameFiles.CollectAsync(root, resolved, RuntimePlatform.Current, meta, ct).ConfigureAwait(false);
        var broken = new System.Collections.Concurrent.ConcurrentBag<DownloadItem>();
        var checkedCount = 0;
        await Parallel.ForEachAsync(items, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = ct }, async (item, token) =>
        {
            if (!await Downloader.IsValidAsync(item, mode, token).ConfigureAwait(false)) broken.Add(item);
            var n = Interlocked.Increment(ref checkedCount);
            if (n % 200 == 0) progress?.Report(new StageProgress("repair.scan", (double)n / items.Count));
        }).ConfigureAwait(false);
        if (!broken.IsEmpty)
        {
            progress?.Report(new StageProgress("repair.download"));
            await downloader.RunAsync(broken.ToList(), progress.ForDownloads("repair.download"), ct, VerifyMode.Hash).ConfigureAwait(false);
        }
        var missing = GameFiles.MissingLocalOnly(root, resolved, RuntimePlatform.Current);
        if (missing.Count > 0)
            throw AppException.Of("repair.unrecoverable", $"{missing.Count} generated file(s) are missing; reinstall the loader", ("count", missing.Count), ("file", Path.GetFileName(missing[0])));
        return broken.Count;
    }

    public async Task<VersionJson> FetchVanillaAsync(string gameVersion, CancellationToken ct)
    {
        var manifest = await meta.GetManifestAsync(ct).ConfigureAwait(false);
        var entry = manifest.Versions.FirstOrDefault(v => v.Id == gameVersion)
                    ?? (await meta.GetManifestAsync(ct, forceRefresh: true).ConfigureAwait(false)).Versions.FirstOrDefault(v => v.Id == gameVersion)
                    ?? throw AppException.Of("install.unknownVersion", $"Minecraft {gameVersion} is not in the version list", ("version", gameVersion));
        var bytes = await meta.FetchBytesAsync(entry.Url, ct).ConfigureAwait(false);
        if (entry.Sha1 is not null && !string.Equals(Hashing.Sha1(bytes), entry.Sha1, StringComparison.OrdinalIgnoreCase))
            throw new IntegrityException($"version json {gameVersion}");
        return JsonSerializer.Deserialize<VersionJson>(bytes, Json.External) ?? throw new InvalidDataException("Empty version JSON");
    }

    private async Task<VersionJson> InstallLoaderAsync(CreateInstanceRequest request, VersionJson vanilla, string clientJar,
        IProgress<StageProgress>? progress, CancellationToken ct)
    {
        var loaderVersion = request.LoaderVersion!;
        switch (request.Loader)
        {
            case LoaderKind.Fabric:
                return await meta.FetchJsonAsync<VersionJson>(LoaderCatalog.FabricProfileUrl(request.GameVersion, loaderVersion), ct).ConfigureAwait(false);
            case LoaderKind.Quilt:
                return await meta.FetchJsonAsync<VersionJson>(LoaderCatalog.QuiltProfileUrl(request.GameVersion, loaderVersion), ct).ConfigureAwait(false);
            case LoaderKind.Forge:
            case LoaderKind.NeoForge:
                var url = request.Loader == LoaderKind.Forge
                    ? LoaderCatalog.ForgeInstallerUrl(request.GameVersion, loaderVersion)
                    : LoaderCatalog.NeoForgeInstallerUrl(loaderVersion);
                var forge = new ForgeInstaller(root, downloader, java, tempDirectory);
                return await forge.InstallAsync(url, request.GameVersion, clientJar, vanilla.JavaVersion?.MajorVersion ?? 8, progress, ct).ConfigureAwait(false);
            default:
                throw AppException.Of("install.unsupportedLoader", $"Loader {request.Loader} cannot be installed", ("loader", request.Loader));
        }
    }

    /// <summary>Removes staging folders left by an install that was killed mid-way.</summary>
    public static void CleanupStaging(GameRoot root)
    {
        if (!Directory.Exists(root.Versions)) return;
        foreach (var dir in Directory.EnumerateDirectories(root.Versions, StagingPrefix + "*"))
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Warn($"Stale staging folder {dir} could not be removed", e); }
        }
    }

    /// <summary>Points files that belong to the new version folder at the staging folder.</summary>
    private sealed class StagingView(GameRoot root, string staging, string id)
    {
        private readonly string _final = root.VersionDir(id);

        public DownloadItem Redirect(DownloadItem item) =>
            AppPaths.IsUnder(item.Path, _final) ? item with { Path = Path.Combine(staging, Path.GetRelativePath(_final, item.Path)) } : item;
    }
}
