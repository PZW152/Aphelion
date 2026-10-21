using System.Text.Json;
using Aphelion.App.Platform;
using Aphelion.Core;
using Aphelion.Core.Launch;
using Aphelion.Core.Logging;
using Aphelion.Core.Settings;
using Aphelion.Core.Storage;

namespace Aphelion.App.Bridge;

/// <summary>What the API needs from the window: dialogs, chrome actions and an event channel.</summary>
internal interface IHostWindow
{
    void Post(string json);
    T OnUiThread<T>(Func<T> action);
    void Minimize();
    void ToggleMaximize();
    void CloseWindow();
    bool IsMaximized { get; }
    void BringToFront();
}

/// <summary>Every method the page may call. Grouped by area in the partial files next to this one.</summary>
internal sealed partial class Api
{
    private readonly AppServices _s;
    private readonly IHostWindow _window;
    private readonly RpcRouter _rpc = new();

    public RpcRouter Router => _rpc;

    public Api(AppServices services, IHostWindow window)
    {
        _s = services;
        _window = window;
        RegisterApp();
        RegisterInstances();
        RegisterAccounts();
        RegisterContent();
        RegisterGame();
        RegisterMode();
        WireEvents();
    }

    private void Emit(string name, object? data) => _window.Post(RpcRouter.Event(name, data));

    private void RegisterApp()
    {
        _rpc.Add("app.info", () => new
        {
            name = AppInfo.Name,
            version = AppInfo.Version,
            mode = _s.Paths.Mode.ToString().ToLowerInvariant(),
            installed = Installation.IsInstalledCopy(),
            installedVersion = Installation.InstalledVersion(),
            dataRoot = _s.Paths.Root,
            executable = Environment.ProcessPath,
            os = Environment.OSVersion.VersionString,
            runtime = Environment.Version.ToString(),
            logFile = Log.CurrentFile,
            uiCulture = System.Globalization.CultureInfo.CurrentUICulture.Name,
            movedFrom = _s.MovedFrom,
        });

        _rpc.Add("app.storage", (JsonElement _, CancellationToken ct) => Task.Run<object?>(() => new
        {
            config = DirectorySize(_s.Paths.Config),
            runtime = DirectorySize(_s.Paths.Runtimes),
            cache = DirectorySize(_s.Paths.Cache) + DirectorySize(_s.Paths.Temp),
            logs = DirectorySize(_s.Paths.Logs),
            paths = new { config = _s.Paths.Config, runtime = _s.Paths.Runtimes, cache = _s.Paths.Cache, logs = _s.Paths.Logs, root = _s.Paths.Root },
        }, ct));

        _rpc.Add<OpenFolderArgs>("app.openFolder", a =>
        {
            var path = a.Kind switch
            {
                "data" => _s.Paths.Root,
                "logs" => _s.Paths.Logs,
                "runtime" => _s.Paths.Runtimes,
                "cache" => _s.Paths.Cache,
                "gameRoot" => _s.ActiveRoot().Path,
                _ => throw AppException.Of("app.folder", "Unknown folder"),
            };
            Shell.OpenFolder(path);
            return null;
        });
        _rpc.Add<UrlArgs>("app.openUrl", a => { Shell.OpenUrl(a.Url); return null; });
        _rpc.Add<PathArgs>("app.revealFile", a =>
        {
            // Only files inside places the launcher manages may be revealed.
            var allowed = _s.Settings.Current.GameRoots.Select(r => r.Path).Append(_s.Paths.Root);
            if (!allowed.Any(r => AppPaths.IsUnder(a.Path, r))) throw AppException.Of("app.path", "That file is outside Aphelion's folders.");
            Shell.RevealFile(a.Path);
            return null;
        });
        _rpc.Add("app.clearCache", (JsonElement _, CancellationToken ct) => Task.Run<object?>(() =>
        {
            long freed = 0;
            foreach (var dir in Directory.EnumerateDirectories(_s.Paths.Cache).Where(d => !string.Equals(d, _s.Paths.WebViewData, StringComparison.OrdinalIgnoreCase)))
            {
                freed += DirectorySize(dir);
                try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            foreach (var f in Directory.EnumerateFiles(_s.Paths.Cache))
            {
                freed += new FileInfo(f).Length;
                AtomicFile.TryDelete(f);
            }
            return freed;
        }, ct));
        _rpc.Add("app.install", () => { Installation.InstallFromCurrent(_s.Paths); return null; });
        _rpc.Add("app.uninstall", () => { Installation.LaunchUninstaller(); return null; });

        _rpc.Add("window.minimize", () => { _window.OnUiThread(() => { _window.Minimize(); return 0; }); return null; });
        _rpc.Add("window.toggleMaximize", () => { _window.OnUiThread(() => { _window.ToggleMaximize(); return 0; }); return null; });
        _rpc.Add("window.close", () => { _window.OnUiThread(() => { _window.CloseWindow(); return 0; }); return null; });
        _rpc.Add("window.state", () => new { maximized = _window.OnUiThread(() => _window.IsMaximized) });

        _rpc.Add("settings.get", () => _s.Settings.Current);
        _rpc.Add("settings.update", (JsonElement patch, CancellationToken _) =>
        {
            var next = RpcRouter.Patch(_s.Settings.Current, patch);
            return Task.FromResult<object?>(_s.Settings.Update(s => Copy(next, s)));
        });

        _rpc.Add("system.memory", () => SystemMemory.Read());

        _rpc.Add("tasks.list", () => _s.Tasks.All);
        _rpc.Add<IdArgs>("tasks.cancel", a => _s.Tasks.Cancel(a.Id));
        _rpc.Add<IdArgs>("tasks.dismiss", a => { _s.Tasks.Dismiss(a.Id); return null; });

        _rpc.Add("dialog.pickFolder", () => _window.OnUiThread<string?>(() =>
        {
            using var d = new FolderBrowserDialog { UseDescriptionForTitle = true, ShowNewFolderButton = true };
            return d.ShowDialog() == DialogResult.OK ? d.SelectedPath : null;
        }));
    }

    private static void Copy(LauncherSettings from, LauncherSettings to)
    {
        foreach (var p in typeof(LauncherSettings).GetProperties().Where(p => p.CanWrite))
            p.SetValue(to, p.GetValue(from));
    }

    private void WireEvents()
    {
        _s.Settings.Changed += s => Emit("settings.changed", s);
        _s.Accounts.Store.Changed += () => Emit("accounts.changed", null);
        var throttle = new EventThrottle<Core.Tasks.TaskSnapshot>(TimeSpan.FromMilliseconds(120), t => t.Id, batch => Emit("tasks.changed", batch));
        _s.Tasks.Changed += t =>
        {
            if (t.Status != Core.Tasks.TaskStatus.Running) throttle.Flush(t); else throttle.Push(t);
        };
        WireGameEvents();
    }

    private static long DirectorySize(string path)
    {
        if (!Directory.Exists(path)) return 0;
        try
        {
            return new DirectoryInfo(path).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint })
                .Sum(f => f.Length);
        }
        catch (IOException) { return 0; }
    }

    private sealed record IdArgs(string Id);
    private sealed record UrlArgs(string Url);
    private sealed record PathArgs(string Path);
    private sealed record OpenFolderArgs(string Kind);
}

/// <summary>Coalesces rapid updates per key and emits them in batches at most once per interval.</summary>
internal sealed class EventThrottle<T>(TimeSpan interval, Func<T, string> key, Action<IReadOnlyList<T>> emit)
{
    private readonly Dictionary<string, T> _pending = [];
    private readonly Lock _gate = new();
    private bool _scheduled;

    public void Push(T item)
    {
        lock (_gate)
        {
            _pending[key(item)] = item;
            if (_scheduled) return;
            _scheduled = true;
        }
        _ = Task.Delay(interval).ContinueWith(_ => Drain(), TaskScheduler.Default);
    }

    /// <summary>Sends this item (and anything pending) immediately — used for final states that must not be lost.</summary>
    public void Flush(T item)
    {
        lock (_gate) _pending[key(item)] = item;
        Drain();
    }

    private void Drain()
    {
        List<T> batch;
        lock (_gate)
        {
            batch = _pending.Values.ToList();
            _pending.Clear();
            _scheduled = false;
        }
        if (batch.Count > 0) emit(batch);
    }
}
