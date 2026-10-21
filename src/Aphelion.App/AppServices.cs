using Aphelion.App.Platform;
using Aphelion.Core.Accounts;
using Aphelion.Core.Content;
using Aphelion.Core.Install;
using Aphelion.Core.Instances;
using Aphelion.Core.Java;
using Aphelion.Core.Launch;
using Aphelion.Core.Logging;
using Aphelion.Core.Minecraft;
using Aphelion.Core.Net;
using Aphelion.Core.Settings;
using Aphelion.Core.Storage;
using Aphelion.Core.Tasks;

namespace Aphelion.App;

/// <summary>Composition root: creates every long-lived service once and wires them together.</summary>
internal sealed class AppServices : IDisposable
{
    public AppPaths Paths { get; }
    public SettingsStore Settings { get; }
    public DownloadSources Sources { get; }
    public MojangMeta Meta { get; }
    public JavaManager Java { get; }
    public AccountService Accounts { get; }
    public TaskHub Tasks { get; } = new();
    public GameLauncher Launcher { get; }
    public ModrinthClient Modrinth { get; } = new();
    public ContentService Content { get; }
    public ModpackService Modpacks { get; }
    public LoaderCatalog Loaders { get; }

    /// <summary>Set on the first start after a mode switch: the data root the data was moved away from.</summary>
    public string? MovedFrom { get; init; }

    public AppServices(AppPaths paths)
    {
        Paths = paths;
        paths.EnsureCreated();
        Settings = new SettingsStore(paths.SettingsFile, paths.Root);
        EnsureDefaultGameRoot();

        Sources = new DownloadSources(Settings.Current.Download.Source);
        Settings.Changed += s => Sources.Preference = s.Download.Source;

        Meta = new MojangMeta(Sources, paths.Cache);
        Java = new JavaManager(paths.Runtimes, Meta, CreateDownloader, () => Settings.Current.ExtraJavaPaths);
        Accounts = new AccountService(new AccountStore(paths.AccountsFile, new DpapiProtector()), () => Settings.Current.MicrosoftClientId);
        Launcher = new GameLauncher(paths, () => Settings.Current, Accounts, Java, Meta, CreateDownloader, Sources);
        Content = new ContentService(Modrinth, CreateDownloader, Shell.Recycle);
        Modpacks = new ModpackService(Modrinth, CreateDownloader, paths.Temp);
        Loaders = new LoaderCatalog(Meta);
    }

    public Downloader CreateDownloader() => new(Sources, Settings.Current.Download.Concurrency);

    public GameRoot ActiveRoot()
    {
        var s = Settings.Current;
        var path = s.ActiveGameRoot is { } a && s.GameRoots.Any(r => string.Equals(r.Path, a, StringComparison.OrdinalIgnoreCase))
            ? a : s.GameRoots.First().Path;
        return new GameRoot(path);
    }

    public InstanceInstaller CreateInstaller(GameRoot root) =>
        new(root, Meta, CreateDownloader(), Java.ResolveExecutableAsync, Paths.Temp);

    /// <summary>The internal game directory is always registered, so a fresh install works without any setup.</summary>
    private void EnsureDefaultGameRoot()
    {
        var current = Settings.Current;
        if (current.GameRoots.Count > 0 && current.GameRoots.All(r => Directory.Exists(r.Path) || IsDefault(r.Path))) return;
        Settings.Update(s =>
        {
            if (!s.GameRoots.Any(r => IsDefault(r.Path)))
                s.GameRoots.Insert(0, new GameRootEntry { Path = Paths.DefaultGameRoot, Name = "Aphelion" });
            s.ActiveGameRoot ??= Paths.DefaultGameRoot;
        });
        Directory.CreateDirectory(Paths.DefaultGameRoot);
    }

    public bool IsDefault(string path) => string.Equals(Path.GetFullPath(path).TrimEnd('\\'), Path.GetFullPath(Paths.DefaultGameRoot).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    /// <summary>Startup housekeeping: leftovers of interrupted work are removed, never user data.</summary>
    public void Housekeeping()
    {
        try
        {
            GameLauncher.CleanupTemp(Paths.Temp);
            Java.CleanupStaging();
            foreach (var r in Settings.Current.GameRoots.Where(r => Directory.Exists(r.Path)))
                InstanceInstaller.CleanupStaging(new GameRoot(r.Path));
            foreach (var f in Directory.EnumerateFiles(Paths.Temp, "modpack-*.mrpack"))
                AtomicFile.TryDelete(f);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Warn("Startup cleanup was incomplete", e);
        }
    }

    public void Dispose() => Java.Dispose();
}
