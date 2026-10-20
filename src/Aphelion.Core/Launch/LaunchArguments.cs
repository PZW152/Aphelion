using System.Text.RegularExpressions;
using Aphelion.Core.Accounts;
using Aphelion.Core.Minecraft;

namespace Aphelion.Core.Launch;

/// <summary>Everything needed to build a command line. Pure data, so argument building is unit-testable.</summary>
public sealed record LaunchContext
{
    public required GameRoot Root { get; init; }
    public required ResolvedVersion Version { get; init; }
    public required string GameDirectory { get; init; }
    public required string NativesDirectory { get; init; }
    public required string AssetsRoot { get; init; }
    public string? LegacyAssetsDirectory { get; init; }
    public required LaunchIdentity Identity { get; init; }
    public required int HeapMb { get; init; }
    public required int JavaMajor { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public bool Fullscreen { get; init; }
    public string? JoinServer { get; init; }
    public string? AuthlibInjectorJar { get; init; }
    public string? LogConfigPath { get; init; }
    public IReadOnlyList<string> ExtraJvmArguments { get; init; } = [];
    public IReadOnlyList<string> ExtraGameArguments { get; init; } = [];
    public RuntimePlatform Platform { get; init; } = RuntimePlatform.Current;
    public string LauncherName { get; init; } = AppInfo.Name;
    public string LauncherVersion { get; init; } = AppInfo.Version;
}

public sealed record LaunchCommand(IReadOnlyList<string> JvmArguments, string MainClass, IReadOnlyList<string> GameArguments, IReadOnlyList<string> Classpath)
{
    public IEnumerable<string> All => JvmArguments.Append(MainClass).Concat(GameArguments);
}

public static partial class LaunchArguments
{
    /// <summary>Default collector flags (the ones the official launcher uses).</summary>
    public static readonly string[] DefaultGcFlags =
    [
        "-XX:+UnlockExperimentalVMOptions", "-XX:+UseG1GC", "-XX:G1NewSizePercent=20", "-XX:G1ReservePercent=20",
        "-XX:MaxGCPauseMillis=50", "-XX:G1HeapRegionSize=32M",
    ];

    public static LaunchCommand Build(LaunchContext ctx)
    {
        var json = ctx.Version.Json;
        var mainClass = json.MainClass ?? throw AppException.Of("launch.noMainClass", "The version has no main class");
        var classpath = BuildClasspath(ctx);
        var (features, quickPlay) = Features(ctx, json);
        var values = Placeholders(ctx, classpath, quickPlay);

        var jvm = new List<string> { $"-Xmx{ctx.HeapMb}m" };
        jvm.Add($"-Xms{Math.Min(ctx.HeapMb, 1024)}m");
        if (!ctx.ExtraJvmArguments.Any(a => a.StartsWith("-XX:+Use", StringComparison.Ordinal) && a.EndsWith("GC", StringComparison.Ordinal)))
            jvm.AddRange(DefaultGcFlags);
        jvm.Add("-Dlog4j2.formatMsgNoLookups=true");
        jvm.Add("-Dfml.ignoreInvalidMinecraftCertificates=true");
        jvm.Add("-Dfml.ignorePatchDiscrepancies=true");
        // Make the game's console output UTF-8 regardless of the Windows code page, so logs are decoded correctly.
        if (ctx.JavaMajor >= 18) { jvm.Add("-Dstdout.encoding=UTF-8"); jvm.Add("-Dstderr.encoding=UTF-8"); }
        else { jvm.Add("-Dsun.stdout.encoding=UTF-8"); jvm.Add("-Dsun.stderr.encoding=UTF-8"); }

        if (json.Arguments?.Jvm is { Count: > 0 } jvmArgs)
            jvm.AddRange(Expand(jvmArgs, ctx.Platform, features, values));
        else
        {
            jvm.Add(Substitute("-Djava.library.path=${natives_directory}", values));
            jvm.Add("-cp");
            jvm.Add(values["classpath"]);
        }

        if (ctx.LogConfigPath is not null && json.Logging?.Client is { Argument.Length: > 0 } logging)
            jvm.Add(logging.Argument.Replace("${path}", ctx.LogConfigPath, StringComparison.Ordinal));

        if (ctx.AuthlibInjectorJar is not null && ctx.Identity.YggdrasilServer is not null)
        {
            jvm.Add($"-javaagent:{ctx.AuthlibInjectorJar}={ctx.Identity.YggdrasilServer}");
            if (ctx.Identity.YggdrasilMetadataBase64 is not null)
                jvm.Add($"-Dauthlibinjector.yggdrasil.prefetched={ctx.Identity.YggdrasilMetadataBase64}");
        }
        jvm.AddRange(ctx.ExtraJvmArguments);

        var game = new List<string>();
        if (json.Arguments?.Game is { Count: > 0 } gameArgs)
            game.AddRange(Expand(gameArgs, ctx.Platform, features, values));
        if (!string.IsNullOrWhiteSpace(json.MinecraftArguments))
            game.AddRange(SplitArguments(json.MinecraftArguments).Select(a => Substitute(a, values)));
        game = DropEmptyOptions(game);

        var modern = json.Arguments?.Game is { Count: > 0 };
        if (!modern && ctx.Width is { } w && ctx.Height is { } h && !ctx.Fullscreen)
            game.AddRange(["--width", w.ToString(System.Globalization.CultureInfo.InvariantCulture), "--height", h.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        if (ctx.Fullscreen && !game.Contains("--fullscreen")) game.Add("--fullscreen");
        if (ctx.JoinServer is { } server && !quickPlay)
        {
            var (host, port) = SplitServer(server);
            game.AddRange(["--server", host, "--port", port]);
        }
        game.AddRange(ctx.ExtraGameArguments);

        return new LaunchCommand(jvm, mainClass, game, classpath);
    }

    public static List<string> BuildClasspath(LaunchContext ctx)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cp = new List<string>();
        foreach (var lib in LibraryResolver.ResolveAll(ctx.Version.Json, ctx.Root, ctx.Platform))
        {
            if (lib.Artifact is { } a && seen.Add(a.Path)) cp.Add(a.Path);
        }
        var jar = ctx.Root.VersionJarPath(ctx.Version.ClientJarId);
        if (seen.Add(jar)) cp.Add(jar);
        return cp;
    }

    private static (Dictionary<string, bool> Features, bool QuickPlay) Features(LaunchContext ctx, VersionJson json)
    {
        var features = new Dictionary<string, bool>
        {
            ["is_demo_user"] = false,
            ["has_custom_resolution"] = ctx.Width is not null && ctx.Height is not null && !ctx.Fullscreen,
        };
        var quickPlay = false;
        if (ctx.JoinServer is not null && json.Arguments?.Game is { } g
            && g.Any(e => e.Rules?.Any(r => r.Features?.ContainsKey("is_quick_play_multiplayer") == true) == true))
        {
            features["is_quick_play_multiplayer"] = true;
            quickPlay = true;
        }
        return (features, quickPlay);
    }

    private static Dictionary<string, string> Placeholders(LaunchContext ctx, List<string> classpath, bool quickPlay)
    {
        var id = ctx.Identity;
        var json = ctx.Version.Json;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["auth_player_name"] = id.Name,
            ["auth_uuid"] = id.Uuid,
            ["auth_access_token"] = id.AccessToken,
            ["auth_session"] = $"token:{id.AccessToken}:{id.Uuid}",
            ["auth_xuid"] = id.Xuid,
            ["clientid"] = "",
            ["user_type"] = id.UserType,
            ["user_properties"] = "{}",
            ["version_name"] = ctx.Version.Id,
            ["version_type"] = json.Type ?? "release",
            ["game_directory"] = ctx.GameDirectory,
            ["assets_root"] = ctx.AssetsRoot,
            ["game_assets"] = ctx.LegacyAssetsDirectory ?? ctx.AssetsRoot,
            ["assets_index_name"] = json.AssetIndex?.Id ?? json.Assets ?? "legacy",
            ["natives_directory"] = ctx.NativesDirectory,
            ["library_directory"] = ctx.Root.Libraries,
            ["classpath"] = string.Join(ctx.Platform.ClasspathSeparator, classpath),
            ["classpath_separator"] = ctx.Platform.ClasspathSeparator,
            ["primary_jar"] = ctx.Root.VersionJarPath(ctx.Version.ClientJarId),
            ["launcher_name"] = ctx.LauncherName,
            ["launcher_version"] = ctx.LauncherVersion,
            ["resolution_width"] = (ctx.Width ?? 854).ToString(inv),
            ["resolution_height"] = (ctx.Height ?? 480).ToString(inv),
            ["quickPlayMultiplayer"] = quickPlay ? ctx.JoinServer! : "",
            ["quickPlayPath"] = "",
        };
    }

    public static IEnumerable<string> Expand(IEnumerable<ArgumentEntry> entries, RuntimePlatform platform, IReadOnlyDictionary<string, bool> features,
        IReadOnlyDictionary<string, string> values)
    {
        foreach (var entry in entries)
        {
            if (!Rules.Allows(entry.Rules, platform, features)) continue;
            foreach (var v in entry.Values) yield return Substitute(v, values);
        }
    }

    public static string Substitute(string input, IReadOnlyDictionary<string, string> values) =>
        PlaceholderPattern().Replace(input, m => values.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);

    /// <summary>Drops "--option value" pairs whose value is empty or an unresolved placeholder.</summary>
    private static List<string> DropEmptyOptions(List<string> args)
    {
        var result = new List<string>(args.Count);
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal) && i + 1 < args.Count
                && (args[i + 1].Length == 0 || PlaceholderPattern().IsMatch(args[i + 1])))
            {
                i++;
                continue;
            }
            result.Add(args[i]);
        }
        return result;
    }

    public static IEnumerable<string> SplitArguments(string text) =>
        text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>Splits user-entered extra arguments, honouring double quotes.</summary>
    public static List<string> ParseUserArguments(string? text)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return result;
        var current = new System.Text.StringBuilder();
        var quoted = false;
        foreach (var ch in text)
        {
            if (ch == '"') { quoted = !quoted; continue; }
            if (char.IsWhiteSpace(ch) && !quoted)
            {
                if (current.Length > 0) { result.Add(current.ToString()); current.Clear(); }
                continue;
            }
            current.Append(ch);
        }
        if (current.Length > 0) result.Add(current.ToString());
        return result;
    }

    private static (string Host, string Port) SplitServer(string server)
    {
        var s = server.Trim();
        var colon = s.LastIndexOf(':');
        if (colon > 0 && !s.EndsWith(']') && int.TryParse(s[(colon + 1)..], out var port))
            return (s[..colon], port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return (s, "25565");
    }

    /// <summary>The command line for logs: access tokens replaced.</summary>
    public static string Describe(string java, LaunchCommand command, LaunchIdentity identity)
    {
        var parts = command.All.Select(a => identity.AccessToken.Length > 4 ? a.Replace(identity.AccessToken, "***", StringComparison.Ordinal) : a)
            .Select(a => a.Contains(' ', StringComparison.Ordinal) ? $"\"{a}\"" : a);
        return $"\"{java}\" {string.Join(' ', parts)}";
    }

    [GeneratedRegex(@"\$\{([A-Za-z_]+)\}")]
    private static partial Regex PlaceholderPattern();
}
