namespace Aphelion.Core.Storage;

public enum RunMode
{
    /// <summary>Data lives under %LOCALAPPDATA%\Aphelion (default).</summary>
    Standard,

    /// <summary>A marker file next to the executable keeps all data in a sibling "Data" folder.</summary>
    Portable,

    /// <summary>Data root chosen through the APHELION_HOME environment variable (development and tests).</summary>
    Override,
}

/// <summary>
/// Every file Aphelion owns lives under exactly one root, split by lifecycle:
/// <code>
/// root/
///   config/    settings.json, accounts.json          user data — kept by a normal uninstall
///   minecraft/ default game directory                 user data — kept by a normal uninstall
///   runtime/   Java runtimes Aphelion downloaded      reproducible
///   cache/     HTTP/metadata cache, WebView profile   disposable at any time
///   temp/      per-launch natives, installer scratch  cleaned at startup
///   logs/      launcher logs (rotated)                disposable
/// </code>
/// </summary>
public sealed class AppPaths
{
    public const string PortableMarkerName = "Aphelion.portable";
    public const string PortableDataFolderName = "Data";
    public const string HomeEnvironmentVariable = "APHELION_HOME";

    /// <summary>Development only: stands in for %LOCALAPPDATA%\Aphelion, so mode switching can be exercised without real data.</summary>
    public const string StandardRootVariable = "APHELION_STANDARD_ROOT";

    public RunMode Mode { get; }
    public string Root { get; }
    public string ExecutableDirectory { get; }

    public string Config => Path.Combine(Root, "config");
    public string SettingsFile => Path.Combine(Config, "settings.json");
    public string AccountsFile => Path.Combine(Config, "accounts.json");
    public string DefaultGameRoot => Path.Combine(Root, "minecraft");
    public string Runtimes => Path.Combine(Root, "runtime");
    public string Cache => Path.Combine(Root, "cache");
    public string WebViewData => Path.Combine(Cache, "webview");
    public string Temp => Path.Combine(Root, "temp");
    public string Logs => Path.Combine(Root, "logs");

    private AppPaths(RunMode mode, string root, string exeDir)
    {
        Mode = mode;
        Root = Path.GetFullPath(root);
        ExecutableDirectory = exeDir;
    }

    /// <summary>Resolution order: APHELION_HOME, portable marker beside the executable, standard location.</summary>
    public static AppPaths Resolve(string executableDirectory, Func<string, string?>? env = null)
    {
        env ??= Environment.GetEnvironmentVariable;
        var exeDir = Path.GetFullPath(executableDirectory);

        var overrideRoot = env(HomeEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(overrideRoot))
            return new AppPaths(RunMode.Override, overrideRoot, exeDir);

        if (File.Exists(Path.Combine(exeDir, PortableMarkerName)))
            return new AppPaths(RunMode.Portable, Path.Combine(exeDir, PortableDataFolderName), exeDir);

        return new AppPaths(RunMode.Standard, StandardRoot(), exeDir);
    }

    public static string StandardRoot() =>
        Environment.GetEnvironmentVariable(StandardRootVariable) is { Length: > 0 } dev
            ? Path.GetFullPath(dev)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create), AppInfo.Name);

    public static AppPaths ForRoot(string root, RunMode mode = RunMode.Override) => new(mode, root, AppContext.BaseDirectory);

    public void EnsureCreated()
    {
        foreach (var dir in new[] { Root, Config, Runtimes, Cache, Temp, Logs })
            Directory.CreateDirectory(dir);
    }

    /// <summary>Whether <paramref name="path"/> is inside this data root (used before any recursive delete).</summary>
    public bool Owns(string path) => IsUnder(path, Root);

    public static bool IsUnder(string path, string directory)
    {
        var p = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var d = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return p.StartsWith(d, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Paths inside the data root are stored relative to it, so a portable folder keeps working after it is moved
/// or its drive letter changes; everything else stays absolute.
/// </summary>
public static class DataPath
{
    public static string Contract(string root, string path) =>
        Path.IsPathFullyQualified(path) && AppPaths.IsUnder(path, root) ? Path.GetRelativePath(root, path) : path;

    public static string Expand(string root, string path) =>
        string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(root, path));
}