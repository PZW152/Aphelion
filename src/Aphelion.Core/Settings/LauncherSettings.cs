using System.Text.Json.Nodes;
using Aphelion.Core.Storage;

namespace Aphelion.Core.Settings;

public enum DownloadSourcePreference { Auto, Official, Mirror }
public enum MemoryMode { Auto, Fixed }
public enum AfterLaunchAction { Keep, Minimize, Close }

public sealed class GameRootEntry
{
    public string Path { get; set; } = "";
    public string Name { get; set; } = "";
}

public sealed class MemorySettings
{
    public MemoryMode Mode { get; set; } = MemoryMode.Auto;
    public int FixedMb { get; set; } = 4096;
}

public sealed class ResolutionSettings
{
    public int Width { get; set; } = 1280;
    public int Height { get; set; } = 720;
    public bool Fullscreen { get; set; }
}

/// <summary>Launch defaults; an instance may override any of these.</summary>
public sealed class GameDefaults
{
    public MemorySettings Memory { get; set; } = new();
    public ResolutionSettings Resolution { get; set; } = new();
    public string JvmArguments { get; set; } = "";
    public string GameArguments { get; set; } = "";
    /// <summary>Absolute path to java(w).exe; null selects automatically per version.</summary>
    public string? JavaPath { get; set; }
    public AfterLaunchAction AfterLaunch { get; set; } = AfterLaunchAction.Keep;
    public bool AnalyzeCrashes { get; set; } = true;
}

public sealed class DownloadSettings
{
    public DownloadSourcePreference Source { get; set; } = DownloadSourcePreference.Auto;
    public int Concurrency { get; set; } = 24;
}

public sealed class LauncherSettings
{
    public const int CurrentSchema = 1;

    public int Schema { get; set; } = CurrentSchema;

    /// <summary>"auto", "zh-Hant", "zh-Hans" or "en".</summary>
    public string Language { get; set; } = "auto";

    /// <summary>"console" (dark), "manual" (light) or "system".</summary>
    public string Theme { get; set; } = "console";

    public string Accent { get; set; } = "#FF5B14";

    /// <summary>"system" (follow the Windows animation setting), "full" or "reduced".</summary>
    public string Motion { get; set; } = "system";
    public DownloadSettings Download { get; set; } = new();
    public GameDefaults Game { get; set; } = new();
    public List<GameRootEntry> GameRoots { get; set; } = [];
    public string? ActiveGameRoot { get; set; }
    public string? SelectedInstance { get; set; }
    public List<string> ExtraJavaPaths { get; set; } = [];

    /// <summary>Azure application (client) ID used for Microsoft sign-in. Empty disables Microsoft accounts.</summary>
    public string MicrosoftClientId { get; set; } = "";

    /// <summary>Repairs values a hand-edited or older file may carry, so the rest of the app can trust them.</summary>
    public void Normalize()
    {
        Download.Concurrency = Math.Clamp(Download.Concurrency, 1, 64);
        Game.Memory.FixedMb = Math.Clamp(Game.Memory.FixedMb, 512, 65536);
        Game.Resolution.Width = Math.Clamp(Game.Resolution.Width, 320, 16384);
        Game.Resolution.Height = Math.Clamp(Game.Resolution.Height, 240, 16384);
        if (Language is not ("auto" or "zh-Hant" or "zh-Hans" or "en")) Language = "auto";
        if (Theme is not ("console" or "manual" or "system")) Theme = "console";
        if (Motion is not ("system" or "full" or "reduced")) Motion = "system";
        if (!System.Text.RegularExpressions.Regex.IsMatch(Accent ?? "", "^#[0-9A-Fa-f]{6}$")) Accent = "#FF5B14";
        GameRoots = GameRoots.Where(r => !string.IsNullOrWhiteSpace(r.Path))
            .GroupBy(r => System.IO.Path.GetFullPath(r.Path), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First()).ToList();
        ExtraJavaPaths = ExtraJavaPaths.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        MicrosoftClientId = (MicrosoftClientId ?? "").Trim();
    }
}

/// <summary>Loads, migrates and saves <see cref="LauncherSettings"/>. Thread-safe; writes are atomic.</summary>
public sealed class SettingsStore
{
    private readonly string _path;
    private readonly Lock _gate = new();
    private LauncherSettings _current;

    public event Action<LauncherSettings>? Changed;

    public SettingsStore(string path)
    {
        _path = path;
        _current = Load(path);
    }

    public LauncherSettings Current
    {
        get { lock (_gate) return Clone(_current); }
    }

    public LauncherSettings Update(Action<LauncherSettings> mutate)
    {
        LauncherSettings snapshot;
        lock (_gate)
        {
            var next = Clone(_current);
            mutate(next);
            next.Normalize();
            AtomicFile.WriteJson(_path, next);
            _current = next;
            snapshot = Clone(next);
        }
        Changed?.Invoke(snapshot);
        return snapshot;
    }

    internal static LauncherSettings Load(string path)
    {
        if (!File.Exists(path) && !File.Exists(path + ".bak")) return Fresh();
        var node = AtomicFile.ReadJson<JsonObject>(path);
        if (node is null) return Fresh();

        var schema = node["schema"]?.GetValue<int>() ?? 0;
        if (schema > LauncherSettings.CurrentSchema)
        {
            // Written by a newer Aphelion: read what we understand, never write back over it silently.
            Logging.Log.Warn($"settings.json schema {schema} is newer than supported {LauncherSettings.CurrentSchema}; unknown fields are ignored.");
        }
        else if (schema < LauncherSettings.CurrentSchema)
        {
            var backup = $"{path}.v{schema}.bak";
            try { File.Copy(path, backup, overwrite: true); } catch (IOException) { }
            Migrate(node, schema);
        }

        var settings = node.Deserialize<LauncherSettings>(Json.App) ?? Fresh();
        settings.Normalize();
        return settings;
    }

    /// <summary>Sequential, individually testable schema upgrades. Each step moves exactly one version forward.</summary>
    internal static void Migrate(JsonObject node, int from)
    {
        for (var v = from; v < LauncherSettings.CurrentSchema; v++)
        {
            switch (v)
            {
                case 0:
                    // Pre-release files had no schema field; their shape already matches schema 1.
                    break;
            }
        }
        node["schema"] = LauncherSettings.CurrentSchema;
    }

    private static LauncherSettings Fresh()
    {
        var s = new LauncherSettings();
        s.Normalize();
        return s;
    }

    private static LauncherSettings Clone(LauncherSettings s) =>
        System.Text.Json.JsonSerializer.Deserialize<LauncherSettings>(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(s, Json.App), Json.App)!;
}

file static class JsonNodeExtensions
{
    public static T? Deserialize<T>(this JsonObject node, System.Text.Json.JsonSerializerOptions options) =>
        System.Text.Json.JsonSerializer.Deserialize<T>(node.ToJsonString(), options);
}
