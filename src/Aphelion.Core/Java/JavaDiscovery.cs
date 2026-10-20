using Microsoft.Win32;

namespace Aphelion.Core.Java;

/// <summary>Finds Java installations without modifying anything. Duplicates (same home) are collapsed.</summary>
public static class JavaDiscovery
{
    public static List<JavaInstall> Scan(string managedRuntimeDirectory, IEnumerable<string> userPaths)
    {
        var candidates = new List<(string Home, JavaSource Source)>();
        void Add(string? path, JavaSource source)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            var home = JavaProbe.HomeOf(path);
            if (home is not null) candidates.Add((home, source));
        }

        foreach (var p in userPaths) Add(p, JavaSource.UserAdded);
        foreach (var home in ManagedHomes(managedRuntimeDirectory)) Add(home, JavaSource.Managed);
        Add(Environment.GetEnvironmentVariable("JAVA_HOME"), JavaSource.JavaHome);
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (File.Exists(Path.Combine(dir, OperatingSystem.IsWindows() ? "java.exe" : "java"))) Add(dir, JavaSource.Path);
        }
        if (OperatingSystem.IsWindows())
        {
            foreach (var home in RegistryHomes()) Add(home, JavaSource.Registry);
            foreach (var home in KnownLocationHomes()) Add(home, JavaSource.KnownLocation);
            foreach (var home in OfficialLauncherHomes()) Add(home, JavaSource.OfficialLauncher);
        }

        var result = new List<JavaInstall>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (home, source) in candidates)
        {
            string key;
            try { key = Path.GetFullPath(home).TrimEnd('\\', '/'); }
            catch (Exception e) when (e is ArgumentException or IOException) { continue; }
            if (!seen.Add(key)) continue;
            var install = JavaProbe.Inspect(home, source);
            if (install is not null) result.Add(install);
        }
        return result.OrderByDescending(j => j.Major).ThenBy(j => j.Source).ToList();
    }

    /// <summary>Homes of runtimes Aphelion downloaded (runtime/&lt;component&gt;).</summary>
    public static IEnumerable<string> ManagedHomes(string runtimeDirectory)
    {
        if (!Directory.Exists(runtimeDirectory)) yield break;
        foreach (var dir in Directory.EnumerateDirectories(runtimeDirectory))
        {
            var name = Path.GetFileName(dir);
            if (name.StartsWith('.') || name.EndsWith(".old", StringComparison.Ordinal)) continue;
            yield return dir;
        }
    }

    private static IEnumerable<string> RegistryHomes()
    {
        if (!OperatingSystem.IsWindows()) yield break;
        string[] roots =
        [
            @"SOFTWARE\JavaSoft\Java Runtime Environment", @"SOFTWARE\JavaSoft\Java Development Kit", @"SOFTWARE\JavaSoft\JDK", @"SOFTWARE\JavaSoft\JRE",
            @"SOFTWARE\Eclipse Adoptium\JDK", @"SOFTWARE\Eclipse Adoptium\JRE", @"SOFTWARE\Eclipse Foundation\JDK", @"SOFTWARE\Microsoft\JDK",
            @"SOFTWARE\Azul Systems\Zulu", @"SOFTWARE\BellSoft\Liberica", @"SOFTWARE\Amazon Corretto",
        ];
        var homes = new List<string>();
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                foreach (var root in roots)
                {
                    using var key = hklm.OpenSubKey(root);
                    if (key is null) continue;
                    foreach (var sub in key.GetSubKeyNames()) CollectHomes(key, sub, homes, depth: 0);
                }
            }
            catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
        }
        foreach (var h in homes) yield return h;
    }

    private static void CollectHomes(RegistryKey parent, string name, List<string> homes, int depth)
    {
        if (!OperatingSystem.IsWindows() || depth > 3) return;
        using var key = parent.OpenSubKey(name);
        if (key is null) return;
        foreach (var valueName in new[] { "JavaHome", "InstallationPath", "Path" })
        {
            if (key.GetValue(valueName) is string s && s.Length > 0) homes.Add(s);
        }
        foreach (var sub in key.GetSubKeyNames()) CollectHomes(key, sub, homes, depth + 1);
    }

    private static IEnumerable<string> KnownLocationHomes()
    {
        var bases = new List<string>();
        foreach (var pf in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
        {
            if (string.IsNullOrEmpty(pf)) continue;
            foreach (var vendor in new[] { "Java", "Eclipse Adoptium", "Eclipse Foundation", "Zulu", "Microsoft", "BellSoft", "Amazon Corretto", "Semeru", "OpenJDK", "AdoptOpenJDK", "Liberica" })
                bases.Add(Path.Combine(pf, vendor));
            bases.Add(Path.Combine(pf, "Android", "Android Studio", "jbr"));
        }
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        bases.Add(Path.Combine(local, "Programs"));
        bases.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".jdks"));

        foreach (var b in bases)
        {
            if (!Directory.Exists(b)) continue;
            if (JavaProbe.FindExecutable(b) is not null) { yield return b; continue; }
            IEnumerable<string> subs;
            try { subs = Directory.EnumerateDirectories(b).ToList(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            foreach (var sub in subs)
            {
                if (JavaProbe.FindExecutable(sub) is not null) yield return sub;
            }
        }
    }

    /// <summary>Runtimes installed by the official Minecraft Launcher (both the MSI and Microsoft Store builds).</summary>
    private static IEnumerable<string> OfficialLauncherHomes()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var roots = new[]
        {
            Path.Combine(local, "Packages", "Microsoft.4297127D64EC6_8wekyb3d8bbwe", "LocalCache", "Local", "runtime"),
            Path.Combine(roaming, ".minecraft", "runtime"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Minecraft Launcher", "runtime"),
        };
        foreach (var root in roots)
        {
            if (!Directory.Exists(root)) continue;
            List<string> components;
            try { components = Directory.EnumerateDirectories(root).ToList(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            foreach (var component in components)
            {
                // runtime/<component>/<platform>/<component>/bin/javaw.exe
                IEnumerable<string> platforms;
                try { platforms = Directory.EnumerateDirectories(component).ToList(); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
                foreach (var platform in platforms)
                {
                    var home = Path.Combine(platform, Path.GetFileName(component));
                    if (JavaProbe.FindExecutable(home) is not null) yield return home;
                }
            }
        }
    }
}
