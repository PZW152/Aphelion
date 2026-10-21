using System.Diagnostics;
using System.Globalization;
using Aphelion.Core;
using Aphelion.Core.Logging;
using Aphelion.Core.Storage;

namespace Aphelion.App.Platform;

/// <summary>
/// Applying a downloaded, verified update. A running executable cannot replace itself, so the new one does it:
/// <list type="number">
/// <item>The running launcher starts the new executable with <c>--apply-update</c> and exits.</item>
/// <item>The new executable waits for it to exit, then upgrades in place: an installed copy through the normal
///   per-user install (program folder swapped as a whole, registration refreshed); a loose or portable copy by renaming
///   the old executable to <c>.old</c> and copying itself into its place — renamed back if anything fails.</item>
/// <item>The upgraded launcher starts with <c>--after-update</c>, removes the <c>.old</c> file and the download folder.</item>
/// </list>
/// Data, settings and the portable marker are never touched.
/// </summary>
internal static class Updater
{
    public const string DownloadPrefix = "update-";

    /// <summary>Starts <paramref name="newExe"/> to replace the running executable. The caller exits afterwards.</summary>
    public static void HandOver(string newExe)
    {
        var psi = new ProcessStartInfo(newExe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(newExe)! };
        psi.ArgumentList.Add("--apply-update");
        psi.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        psi.ArgumentList.Add(Environment.ProcessPath!);
        psi.ArgumentList.Add(AppInfo.Version);
        Process.Start(psi);
        Log.Info($"Handing over to {newExe}");
    }

    /// <summary>Runs inside the new executable. Returns the process exit code.</summary>
    public static int Run(int previousPid, string target, string fromVersion)
    {
        try { using var p = Process.GetProcessById(previousPid); p.WaitForExit(30000); }
        catch (ArgumentException) { }

        if (AppPaths.IsUnder(target, Installation.InstallDirectory))
        {
            try
            {
                Installation.InstallProgram(["--after-update", fromVersion]);
                return 0;
            }
            catch (Exception e) when (e is AppException or IOException or UnauthorizedAccessException)
            {
                Fail(e.Message, target);
                return 1;
            }
        }

        var backup = target + ".old";
        try
        {
            Retry(() =>
            {
                if (File.Exists(backup)) File.Delete(backup);
                File.Move(target, backup);
            });
            File.Copy(Environment.ProcessPath!, target);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Put the previous version back so the launcher still starts.
            try { if (!File.Exists(target) && File.Exists(backup)) File.Move(backup, target); }
            catch (Exception undo) when (undo is IOException or UnauthorizedAccessException) { }
            Fail(e.Message, target);
            return 1;
        }

        Start(target, ["--after-update", fromVersion]);
        return 0;
    }

    /// <summary>At every start: leftovers of an earlier update (the replaced executable, downloaded files) are removed.</summary>
    public static async Task CleanupAsync(AppPaths paths)
    {
        var leftovers = Directory.Exists(paths.Temp) ? Directory.EnumerateDirectories(paths.Temp, DownloadPrefix + "*").ToList() : [];
        var old = Environment.ProcessPath + ".old";
        for (var attempt = 0; attempt < 10 && (leftovers.Count > 0 || File.Exists(old)); attempt++)
        {
            // The executable that applied the update may still be closing.
            if (attempt > 0) await Task.Delay(1000).ConfigureAwait(false);
            try { if (File.Exists(old)) File.Delete(old); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            leftovers.RemoveAll(dir =>
            {
                try { Directory.Delete(dir, recursive: true); return true; }
                catch (DirectoryNotFoundException) { return true; }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
            });
        }
    }

    private static void Retry(Action action)
    {
        for (var attempt = 0; ; attempt++)
        {
            try { action(); return; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException && attempt < 20) { Thread.Sleep(500); }
        }
    }

    private static void Start(string exe, string[] args)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe)! };
        foreach (var a in args) psi.ArgumentList.Add(a);
        Process.Start(psi);
    }

    private static void Fail(string reason, string target)
    {
        TaskDialog.ShowDialog(new TaskDialogPage
        {
            Caption = AppInfo.Name, Heading = "更新沒有完成", Icon = TaskDialogIcon.Warning,
            Text = $"原本的版本保持不變，會照常開啟。\n\n原因：{reason}",
        });
        if (File.Exists(target)) Start(target, []);
    }
}
