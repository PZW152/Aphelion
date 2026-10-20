using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aphelion.Core.Minecraft;

// Data model of the Mojang version JSON format (also produced by Fabric, Quilt, Forge and NeoForge).
// Unknown properties are preserved through ExtensionData so files written back stay faithful.

public sealed class VersionManifest
{
    public ManifestLatest Latest { get; set; } = new();
    public List<ManifestEntry> Versions { get; set; } = [];
}

public sealed class ManifestLatest
{
    public string Release { get; set; } = "";
    public string Snapshot { get; set; } = "";
}

public sealed class ManifestEntry
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "";
    public string Url { get; set; } = "";
    public DateTimeOffset ReleaseTime { get; set; }
    public string? Sha1 { get; set; }
}

public sealed class VersionJson
{
    public string Id { get; set; } = "";
    public string? InheritsFrom { get; set; }
    public string? Type { get; set; }
    public string? MainClass { get; set; }
    public string? MinecraftArguments { get; set; }
    public VersionArguments? Arguments { get; set; }
    public List<Library> Libraries { get; set; } = [];
    public AssetIndexRef? AssetIndex { get; set; }
    public string? Assets { get; set; }
    public VersionDownloads? Downloads { get; set; }
    public JavaVersionRef? JavaVersion { get; set; }
    public LoggingInfo? Logging { get; set; }
    public string? Jar { get; set; }
    // Kept as text: loaders publish timestamps in formats System.Text.Json rejects (e.g. "+0000"), and they are informational only.
    public string? ReleaseTime { get; set; }
    public string? Time { get; set; }
    public int? ComplianceLevel { get; set; }
    public int? MinimumLauncherVersion { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class VersionArguments
{
    public List<ArgumentEntry> Game { get; set; } = [];
    public List<ArgumentEntry> Jvm { get; set; } = [];
}

/// <summary>Either a plain string or <c>{"rules": [...], "value": "x" | ["x", ...]}</c>.</summary>
[JsonConverter(typeof(ArgumentEntryConverter))]
public sealed class ArgumentEntry
{
    public List<string> Values { get; set; } = [];
    public List<Rule>? Rules { get; set; }

    public static ArgumentEntry Of(string value) => new() { Values = [value] };
}

public sealed class Library
{
    public string Name { get; set; } = "";
    public LibraryDownloads? Downloads { get; set; }
    /// <summary>Maven repository base (Fabric/Forge style libraries without a downloads block).</summary>
    public string? Url { get; set; }
    public string? Sha1 { get; set; }
    public long? Size { get; set; }
    public Dictionary<string, string>? Natives { get; set; }
    public ExtractRules? Extract { get; set; }
    public List<Rule>? Rules { get; set; }

    /// <summary>Legacy Forge flag; when explicitly false the library is server-only.</summary>
    public bool? Clientreq { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class LibraryDownloads
{
    public DownloadInfo? Artifact { get; set; }
    public Dictionary<string, DownloadInfo>? Classifiers { get; set; }
}

public sealed class DownloadInfo
{
    public string? Path { get; set; }
    public string? Sha1 { get; set; }
    public long Size { get; set; }
    public string? Url { get; set; }
}

public sealed class ExtractRules
{
    public List<string> Exclude { get; set; } = [];
}

public sealed class Rule
{
    public string Action { get; set; } = "allow";
    public OsRule? Os { get; set; }
    public Dictionary<string, bool>? Features { get; set; }
}

public sealed class OsRule
{
    public string? Name { get; set; }
    public string? Version { get; set; }
    public string? Arch { get; set; }
}

public sealed class AssetIndexRef
{
    public string Id { get; set; } = "";
    public string? Sha1 { get; set; }
    public long Size { get; set; }
    public long TotalSize { get; set; }
    public string Url { get; set; } = "";
}

public sealed class VersionDownloads
{
    public DownloadInfo? Client { get; set; }
    public DownloadInfo? Server { get; set; }

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Extra { get; set; }
}

public sealed class JavaVersionRef
{
    public string? Component { get; set; }
    public int MajorVersion { get; set; }
}

public sealed class LoggingInfo
{
    public LoggingSide? Client { get; set; }
}

public sealed class LoggingSide
{
    public string Argument { get; set; } = "";
    public LoggingFile? File { get; set; }
    public string? Type { get; set; }
}

public sealed class LoggingFile
{
    public string Id { get; set; } = "";
    public string? Sha1 { get; set; }
    public long Size { get; set; }
    public string Url { get; set; } = "";
}

public sealed class AssetIndexFile
{
    public Dictionary<string, AssetObject> Objects { get; set; } = [];
    /// <summary>Pre-1.6 indexes: copy objects to assets/virtual/legacy by name.</summary>
    public bool Virtual { get; set; }
    /// <summary>1.6-1.7 indexes: copy objects into &lt;gameDir&gt;/resources.</summary>
    [JsonPropertyName("map_to_resources")]
    public bool MapToResources { get; set; }
}

public sealed class AssetObject
{
    public string Hash { get; set; } = "";
    public long Size { get; set; }
}

internal sealed class ArgumentEntryConverter : JsonConverter<ArgumentEntry>
{
    public override ArgumentEntry Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
            return ArgumentEntry.Of(reader.GetString()!);

        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;
        var entry = new ArgumentEntry();
        if (root.TryGetProperty("rules", out var rules))
            entry.Rules = rules.Deserialize<List<Rule>>(options);
        if (root.TryGetProperty("value", out var value))
        {
            if (value.ValueKind == JsonValueKind.String) entry.Values.Add(value.GetString()!);
            else if (value.ValueKind == JsonValueKind.Array)
                entry.Values.AddRange(value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!));
        }
        return entry;
    }

    public override void Write(Utf8JsonWriter writer, ArgumentEntry value, JsonSerializerOptions options)
    {
        if (value.Rules is null && value.Values.Count == 1)
        {
            writer.WriteStringValue(value.Values[0]);
            return;
        }
        writer.WriteStartObject();
        if (value.Rules is not null)
        {
            writer.WritePropertyName("rules");
            JsonSerializer.Serialize(writer, value.Rules, options);
        }
        writer.WritePropertyName("value");
        if (value.Values.Count == 1) writer.WriteStringValue(value.Values[0]);
        else JsonSerializer.Serialize(writer, value.Values, options);
        writer.WriteEndObject();
    }
}
