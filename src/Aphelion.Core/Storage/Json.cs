using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Aphelion.Core.Storage;

/// <summary>Shared serializer settings. Files we own are camelCase; Mojang/loader JSON is read leniently.</summary>
public static class Json
{
    /// <summary>For files Aphelion writes (settings, metadata) and for the UI bridge.</summary>
    public static readonly JsonSerializerOptions App = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>For external JSON (version manifests, APIs): case-insensitive, tolerant.</summary>
    public static readonly JsonSerializerOptions External = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    /// <summary>Version JSON written into game directories keeps the Mojang layout (camelCase, indented).</summary>
    public static readonly JsonSerializerOptions VersionFile = new(External)
    {
        WriteIndented = true,
    };
}
