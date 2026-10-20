using System.Text.Json.Serialization;
using Aphelion.Core.Settings;

namespace Aphelion.Core.Instances;

public enum LoaderKind { Vanilla, Fabric, Quilt, Forge, NeoForge, LegacyForge, OptiFine, Unknown }

public sealed class LoaderInfo
{
    public LoaderKind Kind { get; set; } = LoaderKind.Vanilla;
    public string? Version { get; set; }
}

/// <summary>Per-instance overrides of <see cref="GameDefaults"/>. Null means "use the launcher default".</summary>
public sealed class InstanceOverrides
{
    public MemorySettings? Memory { get; set; }
    public ResolutionSettings? Resolution { get; set; }
    public string? JvmArguments { get; set; }
    public string? GameArguments { get; set; }
    public string? JavaPath { get; set; }
    /// <summary>Extra server to join directly after start ("host[:port]").</summary>
    public string? AutoJoinServer { get; set; }
}

/// <summary>Aphelion's metadata, stored as <c>versions/&lt;id&gt;/aphelion.instance.json</c> beside the version JSON.</summary>
public sealed class InstanceMeta
{
    public const int CurrentSchema = 1;
    public const string FileName = "aphelion.instance.json";

    public int Schema { get; set; } = CurrentSchema;
    public string? DisplayName { get; set; }
    /// <summary>Hex colour of the instance patch.</summary>
    public string? Color { get; set; }
    public DateTimeOffset? Created { get; set; }
    public DateTimeOffset? LastPlayed { get; set; }
    public long PlaySeconds { get; set; }
    public bool? Isolated { get; set; }
    public LoaderInfo? Loader { get; set; }
    public string? GameVersion { get; set; }
    public InstanceOverrides Overrides { get; set; } = new();
    /// <summary>Modrinth project/version ids for files Aphelion installed, keyed by relative path.</summary>
    public Dictionary<string, ContentSource> Content { get; set; } = [];
}

public sealed class ContentSource
{
    public string ProjectId { get; set; } = "";
    public string VersionId { get; set; } = "";
    public string? Sha1 { get; set; }
}

/// <summary>A launchable version folder as presented to the user.</summary>
public sealed class Instance
{
    public required string Id { get; init; }
    public required string RootPath { get; init; }
    public required string Name { get; init; }
    public required string Directory { get; init; }
    public string? GameVersion { get; init; }
    public LoaderInfo Loader { get; init; } = new();
    public bool Isolated { get; init; }
    public string GameDirectory => Isolated ? Directory : RootPath;
    public string? Color { get; init; }
    public DateTimeOffset? LastPlayed { get; init; }
    public DateTimeOffset? Created { get; init; }
    public long PlaySeconds { get; init; }
    public string? ReleaseType { get; init; }
    /// <summary>Set when the version JSON could not be read; the instance is listed but cannot launch.</summary>
    public string? Error { get; init; }
    public bool HasAphelionMeta { get; init; }

    [JsonIgnore]
    public InstanceMeta Meta { get; init; } = new();
}
