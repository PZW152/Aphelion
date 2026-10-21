using System.Diagnostics;
using System.Globalization;
using Aphelion.Core;
using Aphelion.Core.Logging;
using Aphelion.Core.Storage;

namespace Aphelion.App.Platform;

/// <summary>
/// Switching between standard and portable mode: where the program and its data end up, whether that is possible,
/// and the steps around <see cref="DataMigration"/> (copying the program, the marker file, restarting).
/// </summary>
internal static class ModeSwitch
{
    /// <param name="ProgramFolder">Folder of the executable that will run afterwards.</param>
    /// <param name="CopiesProgram">Portable copy in another folder: the program is copied there first.</param>
    public sealed record Target(string To, string ProgramFolder, string DataRoot, bool CopiesProgram);

    public static Target Resolve(AppPaths paths, string to, string? folder)
    {
        if (paths.Mode == RunMode.Override) throw AppException.Of("mode.override", "The data folder is set by APHELION_HOME.");
        var exeDir = paths.ExecutableDirectory;
        switch (to)
        {
            case "portable" when paths.Mode == RunMode.Portable:
            case "standard" when paths.Mode == RunMode.Standard:
                throw AppException.Of("mode.same", "Already in this mode.");
            case "standard":
                return new Target(to, exeDir, AppPaths.StandardRoot(), CopiesProgram: false);
            case "portable":
                // An installed copy must stay in Programs (uninstall removes that folder), so it needs another place.
                var programFolder = folder is null ? exeDir : ProgramFolderIn(folder);
                return new Target(to, programFolder, Path.Combine(programFolder, AppPaths.PortableDataFolderName),
                    CopiesProgram: !string.Equals(Path.GetFullPath(programFolder).TrimEnd('\\'), exeDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
            default:
                throw AppException.Of("mode.unknown", $"Unknown mode {to}");
        }
    }

    /// <summary>A chosen folder that is empty is used as is; otherwise the copy goes into an "Aphelion" folder inside it.</summary>
    private static string ProgramFolderIn(string folder)
    {
        var full = Path.GetFullPath(folder);
        return Directory.Exists(full) && !Directory.EnumerateFileSystemEntries(full).Any() ? full : Path.Combine(full, AppInfo.Name);
    }

    /// <summary>Reasons beyond the data itself that make the switch impossible, or null.</summary>
    public static string? Blocker(Target t)
    {
        if (t.To == "portable" && !t.CopiesProgram && Installation.IsInstalledCopy()) return "installed";
        if (t.CopiesProgram && Directory.Exists(t.ProgramFolder) && Directory.EnumerateFileSystemEntries(t.ProgramFolder).Any()) return "folderNotEmpty";
        // The marker is written (portable) or removed (standard) beside the program afterwards.
        if (!t.CopiesProgram && !CanWrite(t.ProgramFolder)) return "readOnly";
        return null;
    }

    /// <summary>Runs before any data moves, so a failure here changes nothing.</summary>
    public static void Prepare(Target t)
    {
        if (!t.CopiesProgram) return;
        Directory.CreateDirectory(t.ProgramFolder);
        foreach (var f in Installation.ProgramFiles())
            File.Copy(f, Path.Combine(t.ProgramFolder, Path.GetFileName(f)));
    }

    /// <summary>After the data moved: mark (or unmark) the program folder, start the program there, and exit.</summary>
    public static void Finish(Target t, AppPaths from)
    {
        var marker = Path.Combine(t.ProgramFolder, AppPaths.PortableMarkerName);
        if (t.To == "portable")
            File.WriteAllText(marker, "Aphelion keeps all of its data in the Data folder next to this file while it exists.\r\n");
        else
            File.Delete(Path.Combine(from.ExecutableDirectory, AppPaths.PortableMarkerName));

        var exe = Path.Combine(t.ProgramFolder, Path.GetFileName(Environment.ProcessPath!));
        var psi = new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = t.ProgramFolder };
        psi.ArgumentList.Add("--after-switch");
        psi.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        psi.ArgumentList.Add(from.Root);
        Process.Start(psi);
        Log.Info($"Switched to {t.To} mode; data now in {t.DataRoot}");
    }

    private static bool CanWrite(string folder)
    {
        try
        {
            var probe = Path.Combine(folder, ".aphelion-write-test-" + Guid.NewGuid().ToString("N")[..8]);
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }
}
