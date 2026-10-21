using Aphelion.Core.Storage;

namespace Aphelion.Core.Net;

/// <summary>
/// Game folders on this PC often already hold the files a new install needs: assets and libraries are identical
/// across launchers and versions. For a file inside one game folder's <c>assets</c> or <c>libraries</c>, this names
/// the same relative path in every other known game folder; the downloader copies one only after its hash matches
/// the official metadata, so a donor can never introduce a different file.
/// </summary>
public sealed class LocalDonors(Func<IEnumerable<string>> roots)
{
    private static readonly string[] Shared = ["assets", "libraries"];

    /// <summary>The official launcher's folder, which most players already have.</summary>
    public static string OfficialLauncherRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft");

    public IEnumerable<string> For(DownloadItem item)
    {
        var all = roots().Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => Path.GetFullPath(r).TrimEnd('\\'))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var full = Path.GetFullPath(item.Path);
        var owner = all.FirstOrDefault(r => AppPaths.IsUnder(full, r));
        if (owner is null) yield break;
        var relative = Path.GetRelativePath(owner, full);
        if (!Shared.Any(s => relative.StartsWith(s + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))) yield break;
        foreach (var root in all)
        {
            if (!string.Equals(root, owner, StringComparison.OrdinalIgnoreCase)) yield return Path.Combine(root, relative);
        }
    }
}
