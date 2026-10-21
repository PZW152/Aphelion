using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using Aphelion.Core.Accounts;
using Aphelion.Core.Install;
using Aphelion.Core.Instances;
using Aphelion.Core.Java;
using Aphelion.Core.Logging;
using Aphelion.Core.Minecraft;
using Aphelion.Core.Net;
using Aphelion.Core.Settings;
using Aphelion.Core.Storage;

namespace Aphelion.Core.Launch;

/// <summary>The six stages shown to the user, in order.</summary>
public static class LaunchStages
{
    public const string Verify = "launch.verify";
    public const string Java = "launch.java";
    public const string Auth = "launch.auth";
    public const string Natives = "launch.natives";
    public const string Arguments = "launch.arguments";
    public const string Ignition = "launch.ignition";
    public static readonly string[] All = [Verify, Java, Auth, Natives, Arguments, Ignition];
}

public sealed class GameLauncher(
    AppPaths paths,
    Func<LauncherSettings> settings,
    AccountService accounts,
    JavaManager java,
    MojangMeta meta,
    Func<Downloader> downloaderFactory)
{
    private readonly ConcurrentDictionary<string, GameSession> _sessions = new();

    public event Action<GameSession>? SessionStarted;
    public event Action<GameSession, GameExit>? SessionEnded;

    public IReadOnlyCollection<GameSession> Sessions => _sessions.Values.ToList();

    public GameSession? Find(string sessionId) => _sessions.TryGetValue(sessionId, out var s) ? s : null;

    public async Task<GameSession> LaunchAsync(Instance instance, Account account, IProgress<StageProgress>? progress, CancellationToken ct)
    {
        if (_sessions.Values.Any(s => s.InstanceId == instance.Id && s.RootPath == instance.RootPath && s.State != SessionState.Exited))
            throw AppException.Of("launch.alreadyRunning", $"{instance.Name} is already running", ("name", instance.Name));
        if (instance.Error is not null)
            throw AppException.Of("launch.brokenInstance", $"{instance.Name} cannot be read: {instance.Error}", ("name", instance.Name), ("error", instance.Error));

        var cfg = settings();
        var o = instance.Meta.Overrides;
        var root = new GameRoot(instance.RootPath);
        var platform = RuntimePlatform.Current;

        // 1. Files.
        progress?.Report(new StageProgress(LaunchStages.Verify));
        var version = VersionResolver.Resolve(root, instance.Id);
        var items = await GameFiles.CollectAsync(root, version, platform, meta, ct).ConfigureAwait(false);
        await downloaderFactory().RunAsync(items, progress.ForDownloads(LaunchStages.Verify), ct, VerifyMode.Size).ConfigureAwait(false);
        var unrecoverable = GameFiles.MissingLocalOnly(root, version, platform);
        if (unrecoverable.Count > 0)
            throw AppException.Of("launch.missingGenerated", $"{Path.GetFileName(unrecoverable[0])} is missing; reinstall the mod loader", ("file", Path.GetFileName(unrecoverable[0])));

        var gameDir = instance.GameDirectory;
        Directory.CreateDirectory(gameDir);
        if (instance.Loader.Kind is not (LoaderKind.Vanilla or LoaderKind.Unknown))
        {
            // Old Forge writes config/splash.properties and scans mods/ without creating either folder.
            Directory.CreateDirectory(Path.Combine(gameDir, "config"));
            Directory.CreateDirectory(Path.Combine(gameDir, "mods"));
        }
        string? legacyAssets = null;
        if (version.Json.AssetIndex is { } assetRef)
        {
            var index = await GameFiles.LoadAssetIndexAsync(root, assetRef, meta, ct).ConfigureAwait(false);
            var assetsDir = GameFiles.PrepareLegacyAssets(root, assetRef, index, gameDir);
            if (!string.Equals(assetsDir, root.Assets, StringComparison.OrdinalIgnoreCase)) legacyAssets = assetsDir;
            else if (index.MapToResources) legacyAssets = Path.Combine(gameDir, "resources");
        }

        // 2. Java.
        progress?.Report(new StageProgress(LaunchStages.Java));
        var javaInstall = await PickJavaAsync(version.Json, o.JavaPath ?? cfg.Game.JavaPath, progress, ct).ConfigureAwait(false);

        // 3. Account.
        progress?.Report(new StageProgress(LaunchStages.Auth));
        var identity = await accounts.PrepareLaunchAsync(account, ct).ConfigureAwait(false);
        string? injector = null;
        if (identity.YggdrasilServer is not null)
            injector = await AuthlibInjector.EnsureAsync(Path.Combine(paths.Runtimes, "authlib-injector"), ct).ConfigureAwait(false);

        // 4. Natives.
        progress?.Report(new StageProgress(LaunchStages.Natives));
        var nativesDir = Path.Combine(paths.Temp, $"natives-{instance.Id.GetHashCode(StringComparison.Ordinal):x8}-{Guid.NewGuid().ToString("N")[..8]}");
        var nativeCount = ExtractNatives(root, version, platform, nativesDir);

        // 5. Arguments.
        progress?.Report(new StageProgress(LaunchStages.Arguments));
        var memory = o.Memory ?? cfg.Game.Memory;
        var modCount = CountMods(gameDir);
        var heap = memory.Mode == MemoryMode.Fixed
            ? memory.FixedMb
            : SystemMemory.AutoHeapMb(SystemMemory.Read(), modCount, instance.Loader.Kind != LoaderKind.Vanilla);
        var resolution = o.Resolution ?? cfg.Game.Resolution;
        var logConfig = version.Json.Logging?.Client?.File is { } lf ? Path.Combine(root.LogConfigs, lf.Id) : null;
        var context = new LaunchContext
        {
            Root = root,
            Version = version,
            GameDirectory = gameDir,
            NativesDirectory = nativesDir,
            AssetsRoot = root.Assets,
            LegacyAssetsDirectory = legacyAssets,
            Identity = identity,
            HeapMb = heap,
            JavaMajor = javaInstall.Major,
            Width = resolution.Width,
            Height = resolution.Height,
            Fullscreen = resolution.Fullscreen,
            JoinServer = string.IsNullOrWhiteSpace(o.AutoJoinServer) ? null : o.AutoJoinServer,
            AuthlibInjectorJar = injector,
            LogConfigPath = logConfig is not null && File.Exists(logConfig) ? logConfig : null,
            ExtraJvmArguments = LaunchArguments.ParseUserArguments(o.JvmArguments ?? cfg.Game.JvmArguments),
            ExtraGameArguments = LaunchArguments.ParseUserArguments(o.GameArguments ?? cfg.Game.GameArguments),
        };
        var command = LaunchArguments.Build(context);
        WriteDefaultOptions(gameDir, cfg.Language);
        Log.Info($"Launching {instance.Id}: java {javaInstall.Version} ({javaInstall.Executable}), heap {heap} MB, {command.Classpath.Count} classpath entries, {nativeCount} natives, {modCount} mods");
        Log.Debug(LaunchArguments.Describe(javaInstall.Executable, command, identity));

        // 6. Ignition.
        progress?.Report(new StageProgress(LaunchStages.Ignition));
        var psi = new ProcessStartInfo(javaInstall.Executable)
        {
            WorkingDirectory = gameDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        AddArguments(psi, command, javaInstall.Major, nativesDir);
        // A global JAVA_TOOL_OPTIONS / _JAVA_OPTIONS silently overrides our memory and GC settings.
        psi.Environment.Remove("JAVA_TOOL_OPTIONS");
        psi.Environment.Remove("_JAVA_OPTIONS");

        Process process;
        try
        {
            process = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null");
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            TryDeleteDirectory(nativesDir);
            throw AppException.Of("launch.startFailed", $"Java could not be started: {e.Message}", ("error", e.Message));
        }

        var session = new GameSession(process, instance.Id, instance.Name, instance.RootPath, gameDir, (s, exit) => OnExit(s, exit, root, nativesDir));
        _sessions[session.Id] = session;
        // Subscribers attach before output is read, so a JVM that dies instantly cannot race the start event.
        SessionStarted?.Invoke(session);
        session.Start();
        return session;
    }

    private void OnExit(GameSession session, GameExit exit, GameRoot root, string nativesDir)
    {
        TryDeleteDirectory(nativesDir);
        try
        {
            InstanceStore.UpdateMeta(root, session.InstanceId, m =>
            {
                m.LastPlayed = DateTimeOffset.Now;
                m.PlaySeconds += (long)exit.Duration.TotalSeconds;
            });
        }
        catch (Exception e) when (e is IOException or AppException or UnauthorizedAccessException)
        {
            Log.Warn("Could not record play time", e);
        }
        SessionEnded?.Invoke(session, exit);
        _ = Task.Delay(TimeSpan.FromMinutes(30)).ContinueWith(_ => { if (_sessions.TryRemove(session.Id, out var s)) s.Dispose(); }, TaskScheduler.Default);
    }

    private async Task<JavaInstall> PickJavaAsync(VersionJson version, string? configured, IProgress<StageProgress>? progress, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var home = JavaProbe.HomeOf(configured) ?? throw AppException.Of("java.configuredMissing", $"The selected Java ({configured}) no longer exists", ("path", configured));
            return JavaProbe.Inspect(home, JavaSource.UserAdded) ?? throw AppException.Of("java.configuredBroken", "The selected Java cannot be identified", ("path", configured));
        }
        return await java.EnsureAsync(version, progress, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Windows limits a command line to 32,767 characters. Long class paths go into a Java argument file
    /// (supported since Java 9); Java 8 has no such option, so its arguments are passed directly.
    /// </summary>
    private static void AddArguments(ProcessStartInfo psi, LaunchCommand command, int javaMajor, string tempDir)
    {
        var all = command.All.ToList();
        var length = all.Sum(a => a.Length + 3);
        if (length > 30_000 && javaMajor >= 9)
        {
            var file = Path.Combine(tempDir, "launch.args");
            Directory.CreateDirectory(tempDir);
            var jvmText = string.Join('\n', command.JvmArguments.Select(QuoteForArgFile));
            File.WriteAllText(file, jvmText, new UTF8Encoding(false));
            psi.ArgumentList.Add("@" + file);
            psi.ArgumentList.Add(command.MainClass);
            foreach (var a in command.GameArguments) psi.ArgumentList.Add(a);
            return;
        }
        foreach (var a in all) psi.ArgumentList.Add(a);
    }

    private static string QuoteForArgFile(string arg) => "\"" + arg.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    public static int ExtractNatives(GameRoot root, ResolvedVersion version, RuntimePlatform platform, string target)
    {
        Directory.CreateDirectory(target);
        var count = 0;
        foreach (var lib in LibraryResolver.ResolveAll(version.Json, root, platform))
        {
            if (lib.Native is not { } native || !File.Exists(native.Path)) continue;
            using var zip = ZipFile.OpenRead(native.Path);
            foreach (var entry in zip.Entries)
            {
                if (entry.FullName.EndsWith('/') || entry.FullName.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase)) continue;
                if (lib.NativeExcludes.Any(x => entry.FullName.StartsWith(x, StringComparison.OrdinalIgnoreCase))) continue;
                string destination;
                try { destination = SafePath.Combine(target, entry.FullName); }
                catch (InvalidDataException) { continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                entry.ExtractToFile(destination, overwrite: true);
                count++;
            }
        }
        return count;
    }

    private static int CountMods(string gameDir)
    {
        var mods = Path.Combine(gameDir, "mods");
        if (!Directory.Exists(mods)) return 0;
        try { return Directory.EnumerateFiles(mods, "*.jar").Count(); }
        catch (IOException) { return 0; }
    }

    /// <summary>A brand-new game directory starts in the launcher's language instead of English.</summary>
    private static void WriteDefaultOptions(string gameDir, string language)
    {
        var options = Path.Combine(gameDir, "options.txt");
        if (File.Exists(options)) return;
        var culture = language == "auto" ? System.Globalization.CultureInfo.CurrentUICulture.Name : language;
        var lang = culture switch
        {
            var c when c.StartsWith("zh-Hant", StringComparison.OrdinalIgnoreCase) || c is "zh-TW" or "zh-HK" or "zh-MO" => "zh_tw",
            var c when c.StartsWith("zh", StringComparison.OrdinalIgnoreCase) => "zh_cn",
            _ => null,
        };
        if (lang is null) return;
        try { File.WriteAllText(options, $"lang:{lang}\n"); }
        catch (IOException e) { Log.Warn("Could not write default options.txt", e); }
    }

    /// <summary>Removes per-launch folders left behind by sessions that ended while the launcher was closed.</summary>
    public static void CleanupTemp(string tempDirectory)
    {
        if (!Directory.Exists(tempDirectory)) return;
        foreach (var dir in Directory.EnumerateDirectories(tempDirectory, "natives-*"))
            TryDeleteDirectory(dir);
    }

    private static void TryDeleteDirectory(string dir)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(300);
            }
        }
        Log.Warn($"Temporary folder {dir} is still in use; it will be removed on next start");
    }
}
