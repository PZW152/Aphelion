using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Aphelion.Core.Java;

public enum JavaSource { Managed, Registry, KnownLocation, Path, JavaHome, OfficialLauncher, UserAdded }

public sealed record JavaInstall(string Executable, string Home, int Major, string Version, string Arch, string? Vendor, JavaSource Source)
{
    public bool Is64Bit => Arch is "x64" or "arm64";
}

public static partial class JavaProbe
{
    /// <summary>Executable used to launch the game: javaw.exe on Windows (no console window), java elsewhere.</summary>
    public static string? FindExecutable(string home)
    {
        foreach (var name in OperatingSystem.IsWindows() ? new[] { "javaw.exe", "java.exe" } : ["java"])
        {
            var p = Path.Combine(home, "bin", name);
            if (File.Exists(p) && (!OperatingSystem.IsWindows() || IsWindowsExecutable(p))) return p;
        }
        return null;
    }

    /// <summary>
    /// Whether the file is a Windows PE image. Starting a damaged or truncated java.exe does not simply fail:
    /// Windows shows a modal "unsupported 16-bit application" box that blocks the caller, so such files are never run.
    /// </summary>
    public static bool IsWindowsExecutable(string path)
    {
        try
        {
            using var f = File.OpenRead(path);
            Span<byte> dos = stackalloc byte[64];
            if (f.ReadAtLeast(dos, dos.Length, throwOnEndOfStream: false) < dos.Length || dos[0] != 'M' || dos[1] != 'Z') return false;
            var pe = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(dos[0x3C..]);
            if (pe < 64 || pe > f.Length - 4) return false;
            f.Position = pe;
            Span<byte> sig = stackalloc byte[4];
            return f.ReadAtLeast(sig, 4, throwOnEndOfStream: false) == 4 && sig[0] == 'P' && sig[1] == 'E' && sig[2] == 0 && sig[3] == 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Normalises a path that may point at a home, its bin folder, or java(w).exe itself.</summary>
    public static string? HomeOf(string path)
    {
        try
        {
            var full = Path.GetFullPath(path.Trim().Trim('"'));
            if (File.Exists(full)) full = Path.GetDirectoryName(full)!;
            if (string.Equals(Path.GetFileName(full), "bin", StringComparison.OrdinalIgnoreCase)) full = Path.GetDirectoryName(full)!;
            return FindExecutable(full) is null ? null : full;
        }
        catch (Exception e) when (e is ArgumentException or IOException or NotSupportedException or System.Security.SecurityException)
        {
            return null;
        }
    }

    public static JavaInstall? Inspect(string home, JavaSource source)
    {
        var exe = FindExecutable(home);
        if (exe is null) return null;
        var release = Path.Combine(home, "release");
        string? version = null, arch = null, vendor = null;
        if (File.Exists(release))
        {
            try
            {
                foreach (var line in File.ReadLines(release))
                {
                    var eq = line.IndexOf('=', StringComparison.Ordinal);
                    if (eq <= 0) continue;
                    var key = line[..eq].Trim();
                    var value = line[(eq + 1)..].Trim().Trim('"');
                    switch (key)
                    {
                        case "JAVA_VERSION": version = value; break;
                        case "OS_ARCH": arch = value; break;
                        case "IMPLEMENTOR": vendor = value; break;
                    }
                }
            }
            catch (IOException) { }
        }

        if (version is null || arch is null)
        {
            var probed = RunProbe(exe);
            version ??= probed.Version;
            arch ??= probed.Arch;
            vendor ??= probed.Vendor;
        }
        if (version is null) return null;
        var major = MajorOf(version);
        if (major <= 0) return null;
        return new JavaInstall(exe, home, major, version, NormalizeArch(arch), vendor, source);
    }

    /// <summary>"1.8.0_392" → 8, "17.0.9" → 17, "21" → 21.</summary>
    public static int MajorOf(string version)
    {
        var m = VersionPattern().Match(version);
        if (!m.Success) return 0;
        var first = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        if (first == 1 && m.Groups[2].Success) return int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        return first;
    }

    public static string NormalizeArch(string? arch) => (arch ?? "").ToLowerInvariant() switch
    {
        "amd64" or "x86_64" or "x64" => "x64",
        "aarch64" or "arm64" => "arm64",
        "x86" or "i386" or "i586" or "i686" => "x86",
        "" => Environment.Is64BitOperatingSystem ? "x64" : "x86",
        var other => other,
    };

    private static (string? Version, string? Arch, string? Vendor) RunProbe(string exe)
    {
        // java.exe (not javaw) prints the settings to stderr.
        var console = Path.Combine(Path.GetDirectoryName(exe)!, OperatingSystem.IsWindows() ? "java.exe" : "java");
        if (!File.Exists(console) || (OperatingSystem.IsWindows() && !IsWindowsExecutable(console))) console = exe;
        try
        {
            var psi = new ProcessStartInfo(console, "-XshowSettings:properties -version")
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return default;
            var stderrTask = p.StandardError.ReadToEndAsync();
            var stdoutTask = p.StandardOutput.ReadToEndAsync();
            if (!p.WaitForExit(6000))
            {
                try { p.Kill(); } catch (InvalidOperationException) { }
                return default;
            }
            var text = stderrTask.Result + stdoutTask.Result;
            string? Prop(string name)
            {
                var m = Regex.Match(text, $@"^\s*{Regex.Escape(name)}\s*=\s*(.+)$", RegexOptions.Multiline);
                return m.Success ? m.Groups[1].Value.Trim() : null;
            }
            return (Prop("java.version"), Prop("os.arch"), Prop("java.vendor"));
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return default;
        }
    }

    [GeneratedRegex(@"^(\d+)(?:\.(\d+))?")]
    private static partial Regex VersionPattern();
}
