namespace Aphelion.Core.Minecraft;

/// <summary>A concrete file belonging to a library. <see cref="Url"/> is null when the file is produced locally (Forge processors).</summary>
public sealed record LibraryFile(string Path, string? Url, string? Sha1, long Size);

public sealed record ResolvedLibrary(Library Source, MavenCoordinate? Coordinate, LibraryFile? Artifact, LibraryFile? Native, IReadOnlyList<string> NativeExcludes);

public static class LibraryResolver
{
    public const string MojangLibraries = "https://libraries.minecraft.net/";

    public static IEnumerable<ResolvedLibrary> ResolveAll(VersionJson version, GameRoot root, RuntimePlatform platform)
    {
        foreach (var lib in version.Libraries)
        {
            var resolved = Resolve(lib, root, platform);
            if (resolved is not null) yield return resolved;
        }
    }

    public static ResolvedLibrary? Resolve(Library lib, GameRoot root, RuntimePlatform platform)
    {
        if (!Rules.Allows(lib.Rules, platform)) return null;
        if (lib.Clientreq == false) return null;
        MavenCoordinate? coordinate = MavenCoordinate.TryParse(lib.Name, out var c) ? c : null;

        LibraryFile? native = null;
        if (lib.Natives is not null)
        {
            if (!lib.Natives.TryGetValue(platform.OsName, out var classifierTemplate)) return lib.Downloads?.Artifact is null ? null : Plain(lib, coordinate, root);
            var classifier = classifierTemplate.Replace("${arch}", platform.ArchBits, StringComparison.Ordinal);
            if (lib.Downloads?.Classifiers is { } classifiers && classifiers.TryGetValue(classifier, out var info))
                native = FromInfo(info, coordinate?.WithClassifier(classifier), root, lib.Url);
            else if (coordinate is { } coord)
                native = FromMaven(coord.WithClassifier(classifier), lib.Url, null, 0, root);

            var artifact = lib.Downloads?.Artifact is { } a ? FromInfo(a, coordinate, root, lib.Url) : null;
            return new ResolvedLibrary(lib, coordinate, artifact, native, lib.Extract?.Exclude ?? []);
        }

        return Plain(lib, coordinate, root);
    }

    private static ResolvedLibrary? Plain(Library lib, MavenCoordinate? coordinate, GameRoot root)
    {
        LibraryFile? artifact;
        if (lib.Downloads?.Artifact is { } info) artifact = FromInfo(info, coordinate, root, lib.Url);
        else if (coordinate is { } coord) artifact = FromMaven(coord, lib.Url, lib.Sha1, lib.Size ?? 0, root);
        else return null;
        return new ResolvedLibrary(lib, coordinate, artifact, null, []);
    }

    private static LibraryFile? FromInfo(DownloadInfo info, MavenCoordinate? coordinate, GameRoot root, string? repo)
    {
        var relative = !string.IsNullOrEmpty(info.Path) ? info.Path : coordinate?.RelativePath;
        if (relative is null) return null;
        var url = info.Url;
        if (url is null && repo is not null) url = AppendPath(repo, relative);
        // An explicitly empty URL means "generated during installation" — never downloaded.
        return new LibraryFile(root.LibraryPath(relative), string.IsNullOrEmpty(url) ? null : url, info.Sha1, info.Size);
    }

    private static LibraryFile FromMaven(MavenCoordinate coordinate, string? repo, string? sha1, long size, GameRoot root)
    {
        var relative = coordinate.RelativePath;
        return new LibraryFile(root.LibraryPath(relative), AppendPath(repo ?? MojangLibraries, relative), sha1, size);
    }

    public static string AppendPath(string repo, string relative) => NormalizeRepository(repo).TrimEnd('/') + "/" + relative.TrimStart('/');

    /// <summary>Old metadata points at retired plain-HTTP repositories; rewrite them to their current HTTPS homes.</summary>
    public static string NormalizeRepository(string repo)
    {
        var r = repo.Trim();
        foreach (var legacy in new[] { "http://files.minecraftforge.net/maven", "https://files.minecraftforge.net/maven", "http://maven.minecraftforge.net" })
        {
            if (r.StartsWith(legacy, StringComparison.OrdinalIgnoreCase)) return "https://maven.minecraftforge.net" + r[legacy.Length..];
        }
        if (r.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) return "https://" + r["http://".Length..];
        return r;
    }
}
