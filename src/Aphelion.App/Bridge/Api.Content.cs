using System.Text.Json;
using Aphelion.Core;
using Aphelion.Core.Content;
using Aphelion.Core.Instances;

namespace Aphelion.App.Bridge;

internal sealed partial class Api
{
    private void RegisterContent()
    {
        _rpc.Add<ContentListArgs>("content.list", async (a, ct) => await _s.Content.ListAsync(Instance(a.InstanceId), a.Folder, ct).ConfigureAwait(false));
        _rpc.Add<ToggleArgs>("content.toggle", a => { _s.Content.SetEnabled(Instance(a.InstanceId), a.Folder, a.File, a.Enabled); return null; });
        _rpc.Add<FileArgs>("content.remove", a => { _s.Content.Remove(Instance(a.InstanceId), a.Folder, a.File); return null; });
        _rpc.Add<ImportArgs>("content.import", a => _s.Content.Import(Instance(a.InstanceId), a.Folder, a.Paths));
        _rpc.Add<ContentListArgs>("content.checkUpdates", async (a, ct) => await _s.Content.CheckUpdatesAsync(Instance(a.InstanceId), a.Folder, ct).ConfigureAwait(false));
        _rpc.Add<InstallArgs>("content.install", a =>
        {
            var instance = Instance(a.InstanceId);
            return _s.Tasks.Start("content", a.Title ?? a.ProjectId, async (p, ct) =>
            {
                var files = await _s.Content.InstallAsync(instance, a.Type, a.ProjectId, a.VersionId, p, ct).ConfigureAwait(false);
                Emit("content.changed", new { instanceId = instance.Id, folder = ModrinthClient.FolderFor(a.Type) });
                return files;
            });
        });

        _rpc.Add<SearchArgs>("modrinth.search", async (a, ct) =>
            await _s.Modrinth.SearchAsync(a.Query ?? "", a.Type, a.GameVersion, a.Loader, a.Sort ?? "relevance", a.Offset, a.Limit is > 0 ? a.Limit : 24, ct).ConfigureAwait(false));
        _rpc.Add<IdArgs>("modrinth.project", async (a, ct) => await _s.Modrinth.GetProjectAsync(a.Id, ct).ConfigureAwait(false));
        _rpc.Add<VersionsArgs>("modrinth.versions", async (a, ct) => await _s.Modrinth.GetVersionsAsync(a.Id, a.GameVersion, a.Loader, ct).ConfigureAwait(false));

        _rpc.Add<ModpackArgs>("modpack.install", a =>
        {
            var root = _s.ActiveRoot();
            var problem = InstanceStore.ValidateNewId(root, a.Id);
            if (problem is not null) throw AppException.Of(problem, "Invalid instance name", ("id", a.Id));
            if (a.VersionId is null && a.Path is null) throw AppException.Of("modpack.source", "Choose a modpack first.");
            return _s.Tasks.Start("modpack", a.DisplayName ?? a.Id, async (p, ct) =>
            {
                var file = a.Path ?? await _s.Modpacks.DownloadAsync(a.VersionId!, p, ct).ConfigureAwait(false);
                try
                {
                    var instance = await _s.Modpacks.InstallAsync(file, a.Id, a.DisplayName, _s.CreateInstaller(root), root, p, ct).ConfigureAwait(false);
                    _s.Settings.Update(s => s.SelectedInstance = instance.Id);
                    Emit("instances.changed", null);
                    return instance.Id;
                }
                finally
                {
                    if (a.Path is null) Core.Storage.AtomicFile.TryDelete(file);
                }
            });
        });
        _rpc.Add("modpack.pickFile", () => _window.OnUiThread<object?>(() =>
        {
            using var d = new OpenFileDialog { Filter = "Modrinth 整合包 (*.mrpack)|*.mrpack", Title = "選擇整合包" };
            if (d.ShowDialog() != DialogResult.OK) return null;
            using var zip = System.IO.Compression.ZipFile.OpenRead(d.FileName);
            var index = ModpackService.ReadIndex(zip);
            var (loader, loaderVersion) = ModpackService.LoaderOf(index);
            return new { path = d.FileName, index.Name, index.VersionId, gameVersion = index.Dependencies["minecraft"], loader, loaderVersion, files = index.Files.Count };
        }));
        _rpc.Add<IdArgs>("modpack.export", a =>
        {
            var instance = Instance(a.Id);
            var target = _window.OnUiThread<string?>(() =>
            {
                using var d = new SaveFileDialog { Filter = "Modrinth 整合包 (*.mrpack)|*.mrpack", FileName = $"{instance.Name}.mrpack" };
                return d.ShowDialog() == DialogResult.OK ? d.FileName : null;
            });
            if (target is null) return null;
            return _s.Tasks.Start("export", instance.Name, async (_, ct) =>
                await _s.Modpacks.ExportAsync(instance, target, DateTime.Now.ToString("yyyy.MM.dd", System.Globalization.CultureInfo.InvariantCulture), null, ct).ConfigureAwait(false));
        });
    }

    private Instance Instance(string id) => InstanceStore.Load(_s.ActiveRoot(), id);

    /// <summary>Files dropped onto the window arrive with their real paths (WebView2 additional objects).</summary>
    public void OnFilesDropped(string context, IReadOnlyList<string> paths)
    {
        Emit("files.dropped", new { context, paths });
    }

    private sealed record ContentListArgs(string InstanceId, string Folder);
    private sealed record ToggleArgs(string InstanceId, string Folder, string File, bool Enabled);
    private sealed record FileArgs(string InstanceId, string Folder, string File);
    private sealed record ImportArgs(string InstanceId, string Folder, List<string> Paths);
    private sealed record InstallArgs(string InstanceId, ProjectType Type, string ProjectId, string? VersionId, string? Title);
    private sealed record SearchArgs(string? Query, ProjectType Type, string? GameVersion, string? Loader, string? Sort, int Offset, int Limit);
    private sealed record VersionsArgs(string Id, string? GameVersion, string? Loader);
    private sealed record ModpackArgs(string Id, string? DisplayName, string? VersionId, string? Path);
}
