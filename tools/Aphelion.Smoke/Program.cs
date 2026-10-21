// Usage: dotnet run --project tools/Aphelion.Smoke -- <dataRoot> <scenario...>
// Scenarios: vanilla:<mc>  fabric:<mc>  quilt:<mc>  forge:<mc>  neoforge:<mc>   (each installs, then launches until the window appears)
using System.Diagnostics;
using Aphelion.Core.Accounts;
using Aphelion.Core.Install;
using Aphelion.Core.Instances;
using Aphelion.Core.Java;
using Aphelion.Core.Launch;
using Aphelion.Core.Logging;
using Aphelion.Core.Minecraft;
using Aphelion.Core.Net;
using Aphelion.Core.Settings;
using Aphelion.Core.Storage;

if (args.Length < 2)
{
    Console.WriteLine("usage: <dataRoot> <kind:mc>...");
    return 2;
}

var paths = AppPaths.ForRoot(args[0]);
paths.EnsureCreated();
Log.Initialize(paths.Logs);
Log.Written += (_, line) => { if (line.Contains("[WARN") || line.Contains("[ERROR")) Console.WriteLine("  log: " + line); };

var settings = new SettingsStore(paths.SettingsFile);
var meta = new MojangMeta(paths.Cache);
// No local reuse here: the smoke test measures downloads from the official servers themselves.
Downloader NewDownloader() => new(32);
using var java = new JavaManager(paths.Runtimes, meta, NewDownloader, () => []);
var accounts = new AccountService(new AccountStore(paths.AccountsFile, new PlainSecretProtector()), () => "");
var account = accounts.AddOffline("Smoke_Pilot");
var launcher = new GameLauncher(paths, () => settings.Current, accounts, java, meta, NewDownloader);
var root = new GameRoot(paths.DefaultGameRoot);
var catalog = new LoaderCatalog(meta);
var failures = 0;

foreach (var scenario in args.Skip(1))
{
    var (kindText, mc) = (scenario.Split(':')[0], scenario.Split(':')[1]);
    var kind = kindText switch
    {
        "fabric" => LoaderKind.Fabric, "quilt" => LoaderKind.Quilt, "forge" => LoaderKind.Forge, "neoforge" => LoaderKind.NeoForge, _ => LoaderKind.Vanilla,
    };
    var id = $"smoke-{kindText}-{mc}";
    Console.WriteLine($"\n=== {id} ===");
    var clock = Stopwatch.StartNew();
    try
    {
        if (!Directory.Exists(root.VersionDir(id)))
        {
            string? loaderVersion = null;
            if (kind != LoaderKind.Vanilla)
            {
                var list = await catalog.ListAsync(kind, mc, CancellationToken.None);
                loaderVersion = (list.FirstOrDefault(l => l.Label == "recommended") ?? list.FirstOrDefault(l => l.Stable) ?? list.First()).Version;
                Console.WriteLine($"  loader {kind} {loaderVersion} ({list.Count} available)");
            }
            var last = "";
            var progress = new InlineProgress<StageProgress>(p =>
            {
                var line = $"  {p.Stage} {(p.Download is { } d ? $"{d.FilesDone}/{d.FilesTotal} files {d.BytesDone / 1048576}/{d.BytesTotal / 1048576} MB" : p.Detail)}";
                if (line[..Math.Min(line.Length, 40)] != last[..Math.Min(last.Length, 40)]) Console.WriteLine(line);
                last = line;
            });
            var installer = new InstanceInstaller(root, meta, NewDownloader(), java.ResolveExecutableAsync, paths.Temp);
            await installer.CreateAsync(new CreateInstanceRequest(id, mc, kind, loaderVersion), progress, CancellationToken.None);
            Console.WriteLine($"  installed in {clock.Elapsed:mm\\:ss}");
        }

        var instance = InstanceStore.Load(root, id);
        Console.WriteLine($"  instance: {instance.GameVersion} {instance.Loader.Kind} {instance.Loader.Version} isolated={instance.Isolated}");
        var session = await launcher.LaunchAsync(instance, account, new InlineProgress<StageProgress>(p => Console.WriteLine($"  > {p.Stage}")), CancellationToken.None);
        var ready = new TaskCompletionSource<bool>();
        session.StateChanged += s => { if (s.State == SessionState.Running) ready.TrySetResult(true); if (s.State == SessionState.Exited) ready.TrySetResult(false); };
        var lines = 0;
        session.LineReceived += (_, l) => { if (Interlocked.Increment(ref lines) <= 6 || l.Level >= GameLogLevel.Error) Console.WriteLine($"  [{l.Level}] {Trim(l.Message)}"); };
        var finished = await Task.WhenAny(ready.Task, Task.Delay(TimeSpan.FromMinutes(4)));
        if (finished == ready.Task && ready.Task.Result)
        {
            // Give the game a moment past the window appearing to prove it keeps running.
            await Task.Delay(TimeSpan.FromSeconds(12));
            var alive = session.State == SessionState.Running;
            Console.WriteLine($"  RUNNING={alive} pid={session.ProcessId} mem={session.MemoryBytes / 1048576} MB lines={lines} t={clock.Elapsed:mm\\:ss}");
            session.Kill();
            var exit = await session.Completion;
            Console.WriteLine($"  stopped (code {exit.ExitCode}, killed={exit.KilledByUser})");
            if (!alive) failures++;
        }
        else
        {
            var exit = session.State == SessionState.Exited ? await session.Completion : null;
            Console.WriteLine($"  FAILED: no window. exit={exit?.ExitCode} findings={string.Join(", ", exit?.Crash?.Findings.Select(f => f.Code + " " + string.Join(";", f.Args.Values)) ?? [])}");
            foreach (var l in session.Snapshot().TakeLast(25)) Console.WriteLine("    | " + Trim(l.Message));
            session.Kill();
            failures++;
        }
    }
    catch (Exception e)
    {
        Console.WriteLine($"  FAILED: {e}");
        failures++;
    }
}

Log.Shutdown();
Console.WriteLine($"\nfailures: {failures}");
return failures == 0 ? 0 : 1;

static string Trim(string s) => s.Length > 160 ? s[..160] + "…" : s.Replace('\n', ' ');
