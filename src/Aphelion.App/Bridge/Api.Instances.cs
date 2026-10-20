using System.Text.Json;
using Aphelion.App.Platform;
using Aphelion.Core;
using Aphelion.Core.Content;
using Aphelion.Core.Install;
using Aphelion.Core.Instances;
using Aphelion.Core.Java;
using Aphelion.Core.Minecraft;
using Aphelion.Core.Settings;
using Aphelion.Core.Storage;

namespace Aphelion.App.Bridge;

internal sealed partial class Api
{
    private void RegisterInstances()
    {
        // Game roots (".minecraft" folders).
        _rpc.Add("roots.list", () =>
        {
            var s = _s.Settings.Current;
            var active = _s.ActiveRoot().Path;
            return s.GameRoots.Select(r => new
            {
                r.Path,
                r.Name,
                exists = Directory.Exists(r.Path),
                isDefault = _s.IsDefault(r.Path),
                active = string.Equals(Path.GetFullPath(r.Path), Path.GetFullPath(active), StringComparison.OrdinalIgnoreCase),
            });
        });
        _rpc.Add<RootArgs>("roots.add", a =>
        {
            var path = a.Path ?? _window.OnUiThread<string?>(() =>
            {
                using var d = new FolderBrowserDialog { UseDescriptionForTitle = true, Description = "選擇 .minecraft 資料夾" };
                return d.ShowDialog() == DialogResult.OK ? d.SelectedPath : null;
            });
            if (path is null) return null;
            path = Path.GetFullPath(path);
            if (!Directory.Exists(path)) throw AppException.Of("roots.missing", "That folder does not exist.");
            var name = a.Name ?? (Path.GetFileName(path) is ".minecraft" ? Path.GetFileName(Path.GetDirectoryName(path)) ?? path : Path.GetFileName(path));
            _s.Settings.Update(s =>
            {
                if (!s.GameRoots.Any(r => string.Equals(Path.GetFullPath(r.Path), path, StringComparison.OrdinalIgnoreCase)))
                    s.GameRoots.Add(new GameRootEntry { Path = path, Name = name });
                s.ActiveGameRoot = path;
                s.SelectedInstance = null;
            });
            return path;
        });
        _rpc.Add<RootArgs>("roots.remove", a =>
        {
            if (a.Path is null || _s.IsDefault(a.Path)) throw AppException.Of("roots.default", "The built-in game folder cannot be removed.");
            // Only forgets the folder; nothing on disk is touched.
            _s.Settings.Update(s =>
            {
                s.GameRoots.RemoveAll(r => string.Equals(r.Path, a.Path, StringComparison.OrdinalIgnoreCase));
                if (string.Equals(s.ActiveGameRoot, a.Path, StringComparison.OrdinalIgnoreCase)) { s.ActiveGameRoot = null; s.SelectedInstance = null; }
            });
            return null;
        });
        _rpc.Add<RootArgs>("roots.select", a =>
        {
            _s.Settings.Update(s => { s.ActiveGameRoot = a.Path; s.SelectedInstance = null; });
            return null;
        });

        // Instances.
        _rpc.Add("instances.list", (JsonElement _, CancellationToken ct) => Task.Run<object?>(() =>
            InstanceStore.List(_s.ActiveRoot()).Select(ToDto).ToList(), ct));
        _rpc.Add<IdArgs>("instances.get", a => ToDto(InstanceStore.Load(_s.ActiveRoot(), a.Id)));
        _rpc.Add<IdArgs>("instances.select", a => { _s.Settings.Update(s => s.SelectedInstance = a.Id); return null; });
        _rpc.Add<CreateArgs>("instances.create", a =>
        {
            var root = _s.ActiveRoot();
            var problem = InstanceStore.ValidateNewId(root, a.Id);
            if (problem is not null) throw AppException.Of(problem, "Invalid instance name", ("id", a.Id));
            var request = new CreateInstanceRequest(a.Id, a.GameVersion, a.Loader, a.LoaderVersion, a.DisplayName, a.Color, a.Isolated ?? true);
            return _s.Tasks.Start("install", a.Id, async (p, ct) =>
            {
                var instance = await _s.CreateInstaller(root).CreateAsync(request, p, ct).ConfigureAwait(false);
                _s.Settings.Update(s => s.SelectedInstance = instance.Id);
                Emit("instances.changed", null);
                return instance.Id;
            });
        });
        _rpc.Add<RenameArgs>("instances.rename", a =>
        {
            EnsureNotRunning(a.Id);
            InstanceStore.Rename(_s.ActiveRoot(), a.Id, a.NewId.Trim());
            _s.Settings.Update(s => { if (s.SelectedInstance == a.Id) s.SelectedInstance = a.NewId.Trim(); });
            Emit("instances.changed", null);
            return a.NewId.Trim();
        });
        _rpc.Add<UpdateArgs>("instances.update", a =>
        {
            InstanceStore.UpdateMeta(_s.ActiveRoot(), a.Id, m =>
            {
                if (a.DisplayName is not null) m.DisplayName = string.IsNullOrWhiteSpace(a.DisplayName) ? null : a.DisplayName.Trim();
                if (a.Color is not null) m.Color = a.Color;
                if (a.Isolated is not null) m.Isolated = a.Isolated;
                if (a.Overrides is not null) m.Overrides = a.Overrides;
            });
            Emit("instances.changed", null);
            return ToDto(InstanceStore.Load(_s.ActiveRoot(), a.Id));
        });
        _rpc.Add<IdArgs>("instances.delete", a =>
        {
            EnsureNotRunning(a.Id);
            var dir = _s.ActiveRoot().VersionDir(a.Id);
            if (!Directory.Exists(dir)) throw AppException.Of("instance.notFound", "Instance not found");
            Shell.Recycle(dir);
            Emit("instances.changed", null);
            return null;
        });
        _rpc.Add<IdArgs>("instances.repair", a =>
        {
            var root = _s.ActiveRoot();
            return _s.Tasks.Start("repair", a.Id, async (p, ct) => await _s.CreateInstaller(root).RepairAsync(a.Id, p, ct).ConfigureAwait(false));
        });
        _rpc.Add<FolderArgs>("instances.openFolder", a =>
        {
            var instance = InstanceStore.Load(_s.ActiveRoot(), a.Id);
            var target = a.Sub is null ? instance.GameDirectory : SafePath.Combine(instance.GameDirectory, a.Sub);
            Shell.OpenFolder(target);
            return null;
        });
        _rpc.Add<IdArgs>("instances.saves", a => Saves.List(InstanceStore.Load(_s.ActiveRoot(), a.Id).GameDirectory));

        // Game versions and loaders.
        _rpc.Add<RefreshArgs>("versions.list", async (a, ct) =>
        {
            var manifest = await _s.Meta.GetManifestAsync(ct, a.Refresh).ConfigureAwait(false);
            return new { latest = manifest.Latest, versions = manifest.Versions.Select(v => new { v.Id, v.Type, v.ReleaseTime }) };
        });
        _rpc.Add<LoaderListArgs>("loaders.list", async (a, ct) => await _s.Loaders.ListAsync(a.Kind, a.GameVersion, ct).ConfigureAwait(false));

        // Java.
        _rpc.Add<RefreshArgs>("java.list", async (a, ct) => await _s.Java.ListAsync(a.Refresh, ct).ConfigureAwait(false));
        _rpc.Add("java.add", (JsonElement _, CancellationToken ct) =>
        {
            var file = _window.OnUiThread<string?>(() =>
            {
                using var d = new OpenFileDialog { Filter = "Java (java.exe; javaw.exe)|java.exe;javaw.exe", Title = "選擇 Java" };
                return d.ShowDialog() == DialogResult.OK ? d.FileName : null;
            });
            if (file is null) return Task.FromResult<object?>(null);
            var home = JavaProbe.HomeOf(file) ?? throw AppException.Of("java.invalid", "That is not a Java installation.");
            var install = JavaProbe.Inspect(home, JavaSource.UserAdded) ?? throw AppException.Of("java.invalid", "That Java could not be identified.");
            _s.Settings.Update(s => { if (!s.ExtraJavaPaths.Contains(home, StringComparer.OrdinalIgnoreCase)) s.ExtraJavaPaths.Add(home); });
            return Task.FromResult<object?>(install);
        });
        _rpc.Add<MajorArgs>("java.install", a => _s.Tasks.Start("java", $"Java {a.Major}", async (p, ct) =>
            await _s.Java.InstallRuntimeAsync(JavaManager.ComponentFor(a.Major), a.Major, p, ct).ConfigureAwait(false)));
    }

    private void EnsureNotRunning(string instanceId)
    {
        if (_s.Launcher.Sessions.Any(s => s.InstanceId == instanceId && s.State != Core.Launch.SessionState.Exited))
            throw AppException.Of("instance.running", "Close the game before changing this instance.");
    }

    internal static object ToDto(Instance i) => new
    {
        i.Id,
        i.Name,
        i.GameVersion,
        loader = i.Loader,
        i.Isolated,
        i.GameDirectory,
        i.Directory,
        i.Color,
        i.LastPlayed,
        i.Created,
        i.PlaySeconds,
        i.ReleaseType,
        i.Error,
        overrides = i.Meta.Overrides,
        modCount = CountFiles(Path.Combine(i.GameDirectory, "mods"), "*.jar"),
        disabledModCount = CountFiles(Path.Combine(i.GameDirectory, "mods"), "*.jar.disabled"),
    };

    private static int CountFiles(string dir, string pattern)
    {
        try { return Directory.Exists(dir) ? Directory.EnumerateFiles(dir, pattern).Count() : 0; }
        catch (IOException) { return 0; }
    }

    private sealed record RootArgs(string? Path, string? Name);
    private sealed record CreateArgs(string Id, string GameVersion, LoaderKind Loader, string? LoaderVersion, string? DisplayName, string? Color, bool? Isolated);
    private sealed record RenameArgs(string Id, string NewId);
    private sealed record UpdateArgs(string Id, string? DisplayName, string? Color, bool? Isolated, InstanceOverrides? Overrides);
    private sealed record FolderArgs(string Id, string? Sub);
    private sealed record RefreshArgs(bool Refresh);
    private sealed record LoaderListArgs(LoaderKind Kind, string GameVersion);
    private sealed record MajorArgs(int Major);
}
