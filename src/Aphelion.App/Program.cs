using Aphelion.App.Platform;
using Aphelion.Core;
using Aphelion.Core.Logging;
using Aphelion.Core.Storage;

namespace Aphelion.App;

internal static class Program
{
    /// <summary>
    /// Command line:
    ///   (none)                     start the launcher
    ///   --install                  install or upgrade this build for the current user
    ///   --uninstall [--quiet [--purge]]  remove the installation (Settings › Apps calls this);
    ///                              --purge also deletes Aphelion's settings, cache, logs and runtimes
    ///   --uninstall-run &lt;pid&gt;     internal: the temporary uninstaller copy
    ///   --after-switch &lt;pid&gt; &lt;old data root&gt;  internal: first start after switching standard/portable mode
    ///   --apply-update &lt;pid&gt; &lt;exe&gt; &lt;from&gt;   internal: a downloaded update replacing the executable that started it
    ///   --after-update &lt;from version&gt;          internal: first start after an update
    /// </summary>
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        var quiet = args.Contains("--quiet");
        var purge = args.Contains("--purge");

        if (args.Length >= 2 && args[0] == "--uninstall-run" && int.TryParse(args[1], out var parent))
            return Installation.RunUninstall(parent, quiet, purge);
        // Before anything touches a data folder: this copy only replaces the executable that started it.
        if (args.Length >= 4 && args[0] == "--apply-update" && int.TryParse(args[1], out var updating))
            return Updater.Run(updating, args[2], args[3]);
        var updateAt = Array.IndexOf(args, "--after-update");
        var updatedFrom = updateAt >= 0 && updateAt + 1 < args.Length ? args[updateAt + 1] : null;
        if (args.Contains("--uninstall"))
        {
            Installation.LaunchUninstaller(quiet, purge);
            return 0;
        }

        // After a mode switch, the previous process may still be closing its window and browser.
        string? movedFrom = null;
        var switchAt = Array.IndexOf(args, "--after-switch");
        if (switchAt >= 0 && switchAt + 2 < args.Length && int.TryParse(args[switchAt + 1], out var previous))
        {
            movedFrom = args[switchAt + 2];
            try { using var p = System.Diagnostics.Process.GetProcessById(previous); p.WaitForExit(20000); }
            catch (ArgumentException) { }
        }

        var paths = AppPaths.Resolve(AppContext.BaseDirectory);
        try
        {
            paths.EnsureCreated();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show($"無法建立資料資料夾：{paths.Root}\n{e.Message}", AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
        Log.Initialize(paths.Logs);
        Log.Info($"Mode {paths.Mode}, data {paths.Root}");

        if (args.Contains("--install"))
        {
            try { Installation.InstallFromCurrent(paths); return 0; }
            catch (AppException e) { MessageBox.Show(e.Message, AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Warning); return 1; }
        }

        // One launcher per data root: a second start brings the running window forward instead.
        var key = "Aphelion-" + AppInfo.AppId + "-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(paths.Root.ToUpperInvariant())))[..16];
        using var mutex = new Mutex(true, @"Local\" + key, out var first);
        using var activate = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\" + key + "-activate");
        if (!first)
        {
            activate.Set();
            return 0;
        }

        Application.ThreadException += (_, e) => Log.Error("Unhandled UI exception", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Error("Unhandled exception", e.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { Log.Warn("Unobserved task exception", e.Exception); e.SetObserved(); };

        using var services = new AppServices(paths) { MovedFrom = movedFrom, UpdatedFrom = updatedFrom };
        services.Housekeeping();
        _ = Task.Run(() => Updater.CleanupAsync(paths));
        if (updatedFrom is not null) Log.Info($"Updated from {updatedFrom} to {AppInfo.Version}");
        if (movedFrom is not null) _ = Task.Run(() => DataMigration.CleanupSource(movedFrom));
        using var form = new MainForm(services);

        var listener = new Thread(() =>
        {
            while (activate.WaitOne())
            {
                if (form.IsDisposed) return;
                try { form.BeginInvoke(() => ((Bridge.IHostWindow)form).BringToFront()); }
                catch (InvalidOperationException) { return; }
            }
        }) { IsBackground = true, Name = "single-instance" };
        listener.Start();

        Application.Run(form);
        Log.Info("Exit");
        Log.Shutdown();
        return 0;
    }
}
