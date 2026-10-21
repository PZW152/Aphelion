using Aphelion.Core.Logging;
using Aphelion.Core.Settings;

namespace Aphelion.Core.Storage;

/// <summary>One folder a mode switch can carry over. <see cref="Conflict"/>: the target already has one, so it stays put.</summary>
public sealed record MigrationItem(string Key, long Bytes, bool Required, bool Conflict);

public sealed record MigrationPlan(string SourceRoot, string TargetRoot, IReadOnlyList<MigrationItem> Items, bool SameVolume, string? Blocker);

public sealed record MigrationProgress(string Item, long Done, long Total);

/// <summary>
/// Moves Aphelion's user data from one data root to another, for switching between standard and portable mode.
/// <list type="bullet">
/// <item>Settings and accounts always move; the built-in game folder and downloaded Java runtimes move when asked.</item>
/// <item>Cache, temp and logs are not moved: they are disposable, the next start recreates them and
///   <see cref="CleanupSource"/> removes the old ones.</item>
/// <item>Nothing is lost on failure. On one volume each folder is renamed and renames are undone if a later one fails;
///   across volumes everything is copied first and the source is deleted only after every copy succeeded.</item>
/// <item>Paths in the settings that pointed into a moved folder are rewritten; paths into folders left behind keep
///   pointing at the old location, so an unmoved game folder stays in the list.</item>
/// </list>
/// </summary>
public static class DataMigration
{
    public const string Config = "config";
    public const string Games = "minecraft";
    public const string Runtime = "runtime";

    /// <summary>Loaded by the running process (it cannot be moved) and unpacked again by the next one.</summary>
    private const string LoaderFolder = "webview2-loader";

    private static readonly EnumerationOptions Walk = new()
    {
        RecurseSubdirectories = true, IgnoreInaccessible = false, AttributesToSkip = FileAttributes.ReparsePoint,
    };

    public static MigrationPlan Plan(string sourceRoot, string targetRoot)
    {
        var source = Path.GetFullPath(sourceRoot);
        var target = Path.GetFullPath(targetRoot);
        var sameVolume = string.Equals(Path.GetPathRoot(source), Path.GetPathRoot(target), StringComparison.OrdinalIgnoreCase);

        string? blocker = null;
        if (string.Equals(source.TrimEnd('\\'), target.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) blocker = "same";
        else if (AppPaths.IsUnder(target, source) || AppPaths.IsUnder(source, target)) blocker = "nested";
        else if (File.Exists(Path.Combine(target, Config, "settings.json")) || File.Exists(Path.Combine(target, Config, "accounts.json"))) blocker = "targetHasData";

        var items = new List<MigrationItem> { new(Config, Size(Path.Combine(source, Config)), Required: true, Conflict: false) };
        var games = Path.Combine(source, Games);
        if (HasEntries(games))
            items.Add(new(Games, Size(games), Required: false, Conflict: HasEntries(Path.Combine(target, Games))));
        var runtimes = RuntimeEntries(source).ToList();
        if (runtimes.Count > 0)
            items.Add(new(Runtime, runtimes.Sum(Size), Required: false, Conflict: RuntimeEntries(target).Any()));

        return new MigrationPlan(source, target, items, sameVolume, blocker);
    }

    /// <summary>Moves the required items plus those named in <paramref name="optional"/>, then rewrites the moved settings.</summary>
    public static void Execute(string sourceRoot, string targetRoot, IReadOnlyCollection<string> optional,
        IProgress<MigrationProgress>? progress = null, CancellationToken ct = default) =>
        Execute(sourceRoot, targetRoot, optional, progress, forceCopy: false, ct);

    /// <param name="forceCopy">Tests: take the cross-volume path on one volume.</param>
    internal static void Execute(string sourceRoot, string targetRoot, IReadOnlyCollection<string> optional,
        IProgress<MigrationProgress>? progress, bool forceCopy, CancellationToken ct)
    {
        var plan = Plan(sourceRoot, targetRoot);
        if (plan.Blocker is { } b) throw AppException.Of("mode." + b, $"Cannot move data to {plan.TargetRoot} ({b})", ("path", plan.TargetRoot));

        var chosen = plan.Items.Where(i => !i.Conflict && (i.Required || optional.Contains(i.Key))).ToList();
        // Each unit is one folder rename or copy: the config and game folders whole, runtimes one entry at a time.
        var units = new List<(string Item, string From, string To)>();
        foreach (var item in chosen)
        {
            if (item.Key == Runtime)
                units.AddRange(RuntimeEntries(plan.SourceRoot).Select(e => (item.Key, e, Path.Combine(plan.TargetRoot, Runtime, Path.GetFileName(e)))));
            else
                units.Add((item.Key, Path.Combine(plan.SourceRoot, item.Key), Path.Combine(plan.TargetRoot, item.Key)));
        }

        units.RemoveAll(u => !Directory.Exists(u.From));
        Directory.CreateDirectory(plan.TargetRoot);
        if (plan.SameVolume && !forceCopy) MoveByRename(units, progress, ct);
        else MoveByCopy(units, chosen.Sum(i => i.Bytes), progress, ct);

        var moved = chosen.Select(i => i.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var store = new SettingsStore(Path.Combine(plan.TargetRoot, Config, "settings.json"), plan.TargetRoot);
        store.Update(s => s.MapPaths(p => Remap(p, plan.SourceRoot, plan.TargetRoot, moved)));
        Log.Info($"Moved {string.Join(", ", moved)} from {plan.SourceRoot} to {plan.TargetRoot}");
    }

    /// <summary>Where a stored path should point after the move.</summary>
    internal static string Remap(string path, string source, string target, IReadOnlySet<string> moved)
    {
        if (!Path.IsPathFullyQualified(path)) return path;
        static string Top(string rel) => rel.Split(Path.DirectorySeparatorChar, 2)[0];
        if (AppPaths.IsUnder(path, source))
        {
            var rel = Path.GetRelativePath(source, path);
            return moved.Contains(Top(rel)) ? Path.Combine(target, rel) : path;
        }
        if (AppPaths.IsUnder(path, target))
        {
            // A relative entry, already expanded against the new root, whose folder was left behind.
            var rel = Path.GetRelativePath(target, path);
            return moved.Contains(Top(rel)) || rel == "." ? path : Path.Combine(source, rel);
        }
        return path;
    }

    /// <summary>
    /// After a switch, the old root keeps only what was deliberately left behind. Its disposable folders are removed
    /// (retrying while the previous process's browser still holds files), then empty folders. Does nothing if the
    /// old root still has settings, i.e. it was not actually moved away from.
    /// </summary>
    public static async Task CleanupSource(string oldRoot, CancellationToken ct = default)
    {
        var root = Path.GetFullPath(oldRoot);
        if (!Directory.Exists(root) || File.Exists(Path.Combine(root, Config, "settings.json"))) return;
        string[] disposable = [Path.Combine(root, "cache"), Path.Combine(root, "temp"), Path.Combine(root, "logs"), Path.Combine(root, Runtime, LoaderFolder)];
        for (var attempt = 0; attempt < 8; attempt++)
        {
            var left = 0;
            foreach (var dir in disposable.Where(Directory.Exists))
            {
                try { Directory.Delete(dir, recursive: true); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { left++; }
            }
            if (left == 0) break;
            await Task.Delay(1500, ct).ConfigureAwait(false);
        }
        foreach (var dir in new[] { Path.Combine(root, Runtime), Path.Combine(root, Games), Path.Combine(root, Config), root })
        {
            try { if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        Log.Info($"Cleaned up the previous data folder {root}");
    }

    private static void MoveByRename(List<(string Item, string From, string To)> units, IProgress<MigrationProgress>? progress, CancellationToken ct)
    {
        var done = new List<(string From, string To)>();
        try
        {
            foreach (var (item, from, to) in units)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(new(item, done.Count, units.Count));
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                if (Directory.Exists(to) && !Directory.EnumerateFileSystemEntries(to).Any()) Directory.Delete(to);
                Directory.Move(from, to);
                done.Add((from, to));
            }
            progress?.Report(new("", units.Count, units.Count));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            for (var i = done.Count - 1; i >= 0; i--)
            {
                try { Directory.Move(done[i].To, done[i].From); }
                catch (Exception undo) when (undo is IOException or UnauthorizedAccessException) { Log.Error($"Could not move {done[i].To} back", undo); }
            }
            throw e is OperationCanceledException ? e : AppException.Of("mode.inUse", e.Message, ("path", e.Message));
        }
    }

    private static void MoveByCopy(List<(string Item, string From, string To)> units, long total, IProgress<MigrationProgress>? progress, CancellationToken ct)
    {
        var created = new List<string>();
        long copied = 0;
        try
        {
            foreach (var (item, from, to) in units)
            {
                if (Directory.Exists(to) && Directory.EnumerateFileSystemEntries(to).Any())
                    throw AppException.Of("mode.targetHasData", $"{to} is not empty", ("path", to));
                Directory.CreateDirectory(to);
                created.Add(to);
                foreach (var dir in Directory.EnumerateDirectories(from, "*", Walk))
                    Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
                foreach (var file in Directory.EnumerateFiles(from, "*", Walk))
                {
                    ct.ThrowIfCancellationRequested();
                    File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)));
                    copied += new FileInfo(file).Length;
                    progress?.Report(new(item, copied, total));
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            foreach (var dir in created)
            {
                try { Directory.Delete(dir, recursive: true); }
                catch (Exception undo) when (undo is IOException or UnauthorizedAccessException) { Log.Warn($"Could not remove the partial copy {dir}", undo); }
            }
            throw e is OperationCanceledException ? e : e is IOException { HResult: unchecked((int)0x80070070) } // ERROR_DISK_FULL
                ? AppException.Of("mode.diskFull", e.Message)
                : AppException.Of("mode.copyFailed", e.Message, ("path", e.Message));
        }

        // Every copy is complete; only now is the source removed.
        foreach (var (_, from, _) in units)
        {
            try { Directory.Delete(from, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Warn($"Moved, but could not remove the original {from}", e); }
        }
    }

    private static IEnumerable<string> RuntimeEntries(string root)
    {
        var dir = Path.Combine(root, Runtime);
        if (!Directory.Exists(dir)) return [];
        return Directory.EnumerateDirectories(dir).Where(e => !string.Equals(Path.GetFileName(e), LoaderFolder, StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasEntries(string dir) => Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any();

    private static long Size(string path)
    {
        if (File.Exists(path)) return new FileInfo(path).Length;
        if (!Directory.Exists(path)) return 0;
        return new DirectoryInfo(path).EnumerateFiles("*", Walk).Sum(f => f.Length);
    }
}
