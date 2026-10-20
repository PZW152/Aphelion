namespace Aphelion.Core.Minecraft;

/// <summary>A standard ".minecraft" layout. Compatible with the official launcher and other launchers.</summary>
public sealed class GameRoot(string path)
{
    public string Path { get; } = System.IO.Path.GetFullPath(path);
    public string Versions => System.IO.Path.Combine(Path, "versions");
    public string Libraries => System.IO.Path.Combine(Path, "libraries");
    public string Assets => System.IO.Path.Combine(Path, "assets");
    public string AssetIndexes => System.IO.Path.Combine(Assets, "indexes");
    public string AssetObjects => System.IO.Path.Combine(Assets, "objects");
    public string LogConfigs => System.IO.Path.Combine(Assets, "log_configs");

    public string VersionDir(string id) => SafePath.Combine(Versions, id);
    public string VersionJsonPath(string id) => System.IO.Path.Combine(VersionDir(id), id + ".json");
    public string VersionJarPath(string id) => System.IO.Path.Combine(VersionDir(id), id + ".jar");
    public string LibraryPath(string relative) => SafePath.Combine(Libraries, relative);
    public string AssetObjectPath(string hash) => System.IO.Path.Combine(AssetObjects, hash[..2], hash);
    public string AssetIndexPath(string id) => SafePath.Combine(AssetIndexes, id + ".json");
}

/// <summary>Guards every path built from external data (metadata, archives, modpacks) against traversal.</summary>
public static class SafePath
{
    public static string Combine(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || System.IO.Path.IsPathRooted(relative) || relative.Contains(':'))
            throw new InvalidDataException($"Unsafe relative path '{relative}'");
        var rootFull = System.IO.Path.GetFullPath(root);
        var combined = System.IO.Path.GetFullPath(System.IO.Path.Combine(rootFull, relative.Replace('/', System.IO.Path.DirectorySeparatorChar)));
        if (!Storage.AppPaths.IsUnder(combined, rootFull) || string.Equals(combined.TrimEnd('\\', '/'), rootFull.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Path '{relative}' escapes '{root}'");
        return combined;
    }

    public static bool IsValidName(string name) =>
        !string.IsNullOrWhiteSpace(name) && name.Length <= 80 && name.Trim() == name && name != "." && name != ".."
        && name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) < 0 && !name.EndsWith('.')
        && !ReservedNames.Contains(name.Split('.')[0].ToUpperInvariant());

    private static readonly HashSet<string> ReservedNames =
        ["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"];
}
