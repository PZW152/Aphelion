using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text.Json;
using Aphelion.Core;
using Aphelion.Core.Logging;
using Aphelion.Core.Storage;
using Microsoft.Win32;

namespace Aphelion.App.Platform;

/// <summary>Everything a per-user installation created, so uninstalling never guesses.</summary>
internal sealed class InstallManifest
{
    public int Schema { get; set; } = 1;
    public string AppId { get; set; } = AppInfo.AppId;
    public string Version { get; set; } = "";
    public string InstallDirectory { get; set; } = "";
    public List<string> Files { get; set; } = [];
    public List<string> Shortcuts { get; set; } = [];
    public string RegistryKey { get; set; } = "";
    public DateTimeOffset Installed { get; set; }
}

/// <summary>
/// Per-user self-installation (no administrator rights, no installer framework):
/// program files in %LOCALAPPDATA%\Programs\Aphelion, a Start menu shortcut, and an entry in
/// Settings › Apps whose uninstall command is this executable with --uninstall.
/// Running --install from a newer build upgrades the existing installation in place.
/// </summary>
internal static class Installation
{
    public const string UninstallKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Aphelion_" + AppInfo.AppId;

    public static string InstallDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppInfo.Name);

    public static string ManifestPath => Path.Combine(InstallDirectory, "install-manifest.json");

    private static string StartMenuShortcut =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppInfo.Name + ".lnk");

    public static bool IsInstalledCopy() =>
        Environment.ProcessPath is { } exe && AppPaths.IsUnder(exe, InstallDirectory);

    public static string? InstalledVersion()
    {
        using var key = Registry.CurrentUser.OpenSubKey(UninstallKeyPath);
        return key?.GetValue("DisplayVersion") as string;
    }

    /// <summary>The files that make up this build: the executable and any sibling runtime files of a publish folder.</summary>
    private static List<string> ProgramFiles()
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("No process path");
        var dir = Path.GetDirectoryName(exe)!;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Path.GetFileName(exe) };
        foreach (var f in Directory.EnumerateFiles(dir))
        {
            var name = Path.GetFileName(f);
            var ext = Path.GetExtension(f).ToLowerInvariant();
            if (ext is ".dll" or ".pdb" || name.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase))
                names.Add(name);
        }
        return names.Select(n => Path.Combine(dir, n)).ToList();
    }

    public static void InstallFromCurrent(AppPaths paths)
    {
        if (IsInstalledCopy()) throw AppException.Of("install.already", "This copy is already the installed one.");
        if (paths.Mode == RunMode.Portable)
            throw AppException.Of("install.portable", "Portable copies keep their data beside the executable; copy the folder instead, or remove the marker file first.");

        var target = InstallDirectory;
        var staging = target + ".new";
        if (Directory.Exists(staging)) Directory.Delete(staging, true);
        Directory.CreateDirectory(staging);
        var files = ProgramFiles();
        foreach (var f in files) File.Copy(f, Path.Combine(staging, Path.GetFileName(f)), overwrite: true);

        // Swap in: the old program folder is kept until the new one is in place.
        var old = target + ".old";
        if (Directory.Exists(old)) Directory.Delete(old, true);
        if (Directory.Exists(target))
        {
            try { Directory.Move(target, old); }
            catch (IOException) { throw AppException.Of("install.inUse", "Close the installed Aphelion first, then try again."); }
        }
        Directory.Move(staging, target);
        if (Directory.Exists(old)) { try { Directory.Delete(old, true); } catch (IOException) { } }

        var exe = Path.Combine(target, Path.GetFileName(Environment.ProcessPath!));
        CreateShortcut(StartMenuShortcut, exe);
        using (var key = Registry.CurrentUser.CreateSubKey(UninstallKeyPath))
        {
            key.SetValue("DisplayName", AppInfo.Name);
            key.SetValue("DisplayVersion", AppInfo.Version);
            key.SetValue("Publisher", AppInfo.Name);
            key.SetValue("DisplayIcon", exe);
            key.SetValue("InstallLocation", target);
            key.SetValue("UninstallString", $"\"{exe}\" --uninstall");
            key.SetValue("QuietUninstallString", $"\"{exe}\" --uninstall --quiet");
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            key.SetValue("EstimatedSize", (int)(files.Sum(f => new FileInfo(f).Length) / 1024), RegistryValueKind.DWord);
            key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture));
        }
        var manifest = new InstallManifest
        {
            Version = AppInfo.Version,
            InstallDirectory = target,
            Files = files.Select(f => Path.GetFileName(f)).ToList(),
            Shortcuts = [StartMenuShortcut],
            RegistryKey = UninstallKeyPath,
            Installed = DateTimeOffset.Now,
        };
        AtomicFile.WriteJson(ManifestPath, manifest, keepBackup: false);
        Log.Info($"Installed {AppInfo.Version} to {target}");

        // Shell execute: the installed copy must not inherit this process's console handles.
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = target });
        Application.Exit();
    }

    /// <summary>
    /// The installed executable cannot delete itself while running, so a copy in %TEMP% performs the uninstall
    /// once this process has exited.
    /// </summary>
    public static void LaunchUninstaller(bool quiet = false, bool purge = false)
    {
        var exe = Environment.ProcessPath!;
        var tempDir = Path.Combine(Path.GetTempPath(), "Aphelion-uninstall-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        foreach (var f in ProgramFiles()) File.Copy(f, Path.Combine(tempDir, Path.GetFileName(f)));
        var copy = Path.Combine(tempDir, Path.GetFileName(exe));
        var psi = new ProcessStartInfo(copy) { UseShellExecute = false, WorkingDirectory = tempDir };
        psi.ArgumentList.Add("--uninstall-run");
        psi.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (quiet) psi.ArgumentList.Add("--quiet");
        if (purge) psi.ArgumentList.Add("--purge");
        Process.Start(psi);
        Application.Exit();
    }

    /// <summary>Runs from the temporary copy. Returns the process exit code.</summary>
    /// <param name="purgeQuietly">With --quiet: also delete Aphelion's own data (never the built-in game folder).</param>
    public static int RunUninstall(int parentPid, bool quiet, bool purgeQuietly = false)
    {
        try { Process.GetProcessById(parentPid).WaitForExit(15000); } catch (ArgumentException) { }

        var standardData = AppPaths.StandardRoot();
        var purge = quiet && purgeQuietly;
        var purgeGames = false;
        if (!quiet)
        {
            var keep = new TaskDialogButton("解除安裝並保留資料");
            var all = new TaskDialogButton("完整清除");
            var cancel = TaskDialogButton.Cancel;
            var games = new TaskDialogVerificationCheckBox("同時刪除內建遊戲目錄（包含存檔、模組與截圖）");
            var page = new TaskDialogPage
            {
                Caption = "解除安裝 Aphelion",
                Heading = "要保留你的資料嗎？",
                Text = "保留資料：只移除程式本身，帳戶、設定與遊戲都留著，之後重新安裝即可接續使用。\n\n" +
                       $"完整清除：另外刪除 {standardData} 內 Aphelion 建立的設定、帳戶、快取、日誌與 Java 執行環境。" +
                       "你另外加入的遊戲目錄永遠不會被刪除。",
                Buttons = { keep, all, cancel },
                Verification = games,
                Icon = TaskDialogIcon.Warning,
                DefaultButton = keep,
            };
            var result = TaskDialog.ShowDialog(page);
            if (result == cancel) return 1;
            purge = result == all;
            purgeGames = purge && games.Checked;
        }

        var report = new List<string>();
        var manifest = AtomicFile.ReadJson<InstallManifest>(ManifestPath);
        var installDir = manifest?.InstallDirectory ?? InstallDirectory;
        var files = manifest?.Files ?? [Path.GetFileName(Environment.ProcessPath!)];
        foreach (var name in files.Append("install-manifest.json"))
        {
            var p = Path.Combine(installDir, name);
            try { if (File.Exists(p)) File.Delete(p); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { report.Add($"{p}: {e.Message}"); }
        }
        TryRemoveEmptyDirectory(installDir, report);
        foreach (var s in manifest?.Shortcuts ?? [StartMenuShortcut])
        {
            try { if (File.Exists(s)) File.Delete(s); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { report.Add($"{s}: {e.Message}"); }
        }
        Registry.CurrentUser.DeleteSubKeyTree(manifest?.RegistryKey ?? UninstallKeyPath, throwOnMissingSubKey: false);

        if (purge)
        {
            var data = AppPaths.ForRoot(standardData, RunMode.Standard);
            foreach (var dir in new[] { data.Config, data.Cache, data.Temp, data.Logs, data.Runtimes })
                TryDeleteTree(dir, report);
            if (purgeGames) TryDeleteTree(data.DefaultGameRoot, report);
            TryRemoveEmptyDirectory(data.Root, report);
        }

        if (!quiet)
        {
            var text = report.Count == 0
                ? (purge ? "Aphelion 與它建立的資料已全部移除。" : "Aphelion 已移除，你的資料保留在原處。")
                : "大部分項目已移除，但以下項目無法刪除（可能正在使用中）：\n\n" + string.Join("\n", report.Take(12));
            TaskDialog.ShowDialog(new TaskDialogPage { Caption = "解除安裝 Aphelion", Heading = "完成", Text = text, Icon = report.Count == 0 ? TaskDialogIcon.ShieldSuccessGreenBar : TaskDialogIcon.Warning });
        }

        ScheduleSelfDelete();
        return report.Count == 0 ? 0 : 2;
    }

    private static void TryDeleteTree(string dir, List<string> report)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { report.Add($"{dir}: {e.Message}"); }
    }

    private static void TryRemoveEmptyDirectory(string dir, List<string> report)
    {
        try
        {
            if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
            else if (Directory.Exists(dir)) report.Add($"{dir}: 資料夾內仍有不屬於 Aphelion 的檔案，已保留");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { report.Add($"{dir}: {e.Message}"); }
    }

    /// <summary>Removes the temporary uninstaller copy after it exits.</summary>
    private static void ScheduleSelfDelete()
    {
        var dir = Path.GetDirectoryName(Environment.ProcessPath!)!;
        if (!Path.GetFileName(dir).StartsWith("Aphelion-uninstall-", StringComparison.Ordinal)) return;
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c ping 127.0.0.1 -n 3 > nul & rmdir /s /q \"{dir}\"")
        {
            CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden,
            // Run outside the folder being deleted; a process cannot remove its own working directory.
            WorkingDirectory = Path.GetTempPath(),
        });
    }

    private static void CreateShortcut(string path, string target)
    {
        var link = (IShellLinkW)new ShellLink();
        link.SetPath(target);
        link.SetWorkingDirectory(Path.GetDirectoryName(target)!);
        link.SetIconLocation(target, 0);
        link.SetDescription("Minecraft launcher");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        ((IPersistFile)link).Save(path, true);
        Marshal.FinalReleaseComObject(link);
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink;

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder file, int cch, IntPtr fd, uint flags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder name, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder dir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder args, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string args);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int showCmd);
        void SetShowCmd(int showCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder iconPath, int cch, out int iconIndex);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(IntPtr hwnd, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }
}
