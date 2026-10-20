using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Aphelion.Core.Content;

public sealed record ContentMetadata(string? Id, string? Name, string? Version, string? Description, string? Authors, string? Loader, byte[]? Icon);

/// <summary>Reads display metadata from mod jars (Fabric, Quilt, Forge, NeoForge, legacy mcmod.info) and resource packs.</summary>
public static partial class ContentMetadataReader
{
    public const int MaxIconBytes = 96 * 1024;

    public static ContentMetadata? Read(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            return ReadFabric(zip, "fabric.mod.json", "fabric")
                   ?? ReadQuilt(zip)
                   ?? ReadToml(zip, "META-INF/neoforge.mods.toml", "neoforge")
                   ?? ReadToml(zip, "META-INF/mods.toml", "forge")
                   ?? ReadMcmodInfo(zip)
                   ?? ReadPack(zip);
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static ContentMetadata? ReadFabric(ZipArchive zip, string file, string loader)
    {
        var entry = zip.GetEntry(file);
        if (entry is null) return null;
        using var doc = ParseLenient(entry);
        var r = doc.RootElement;
        var icon = r.TryGetProperty("icon", out var i) ? i.ValueKind switch
        {
            JsonValueKind.String => i.GetString(),
            JsonValueKind.Object => i.EnumerateObject().OrderBy(p => int.TryParse(p.Name, out var n) ? Math.Abs(n - 64) : 999).Select(p => p.Value.GetString()).FirstOrDefault(),
            _ => null,
        } : null;
        return new ContentMetadata(Str(r, "id"), Str(r, "name"), Str(r, "version"), Str(r, "description"), People(r, "authors"), loader, ReadIcon(zip, icon));
    }

    private static ContentMetadata? ReadQuilt(ZipArchive zip)
    {
        var entry = zip.GetEntry("quilt.mod.json");
        if (entry is null) return null;
        using var doc = ParseLenient(entry);
        if (!doc.RootElement.TryGetProperty("quilt_loader", out var ql)) return null;
        var md = ql.TryGetProperty("metadata", out var m) ? m : default;
        string? authors = null;
        if (md.ValueKind == JsonValueKind.Object && md.TryGetProperty("contributors", out var c) && c.ValueKind == JsonValueKind.Object)
            authors = string.Join(", ", c.EnumerateObject().Select(p => p.Name));
        var icon = md.ValueKind == JsonValueKind.Object && md.TryGetProperty("icon", out var ic) && ic.ValueKind == JsonValueKind.String ? ic.GetString() : null;
        return new ContentMetadata(Str(ql, "id"), md.ValueKind == JsonValueKind.Object ? Str(md, "name") : null, Str(ql, "version"),
            md.ValueKind == JsonValueKind.Object ? Str(md, "description") : null, authors, "quilt", ReadIcon(zip, icon));
    }

    private static ContentMetadata? ReadToml(ZipArchive zip, string file, string loader)
    {
        var entry = zip.GetEntry(file);
        if (entry is null) return null;
        string text;
        using (var reader = new StreamReader(entry.Open())) text = reader.ReadToEnd();
        // Only the first [[mods]] table describes the jar's primary mod.
        var modsAt = text.IndexOf("[[mods]]", StringComparison.Ordinal);
        var section = modsAt >= 0 ? text[modsAt..] : text;
        var next = section.IndexOf("[[", 8, StringComparison.Ordinal);
        if (next > 0) section = section[..next];
        string? Value(string src, string key)
        {
            var multi = Regex.Match(src, $@"^\s*{key}\s*=\s*'''(.*?)'''", RegexOptions.Multiline | RegexOptions.Singleline);
            if (multi.Success) return multi.Groups[1].Value.Trim();
            var m = Regex.Match(src, $@"^\s*{key}\s*=\s*""((?:[^""\\]|\\.)*)""", RegexOptions.Multiline);
            return m.Success ? Regex.Unescape(m.Groups[1].Value) : null;
        }
        var version = Value(section, "version");
        if (version is null or "${file.jarVersion}") version = ManifestVersion(zip) ?? version;
        var logo = Value(section, "logoFile") ?? Value(text, "logoFile");
        return new ContentMetadata(Value(section, "modId"), Value(section, "displayName"), version, Value(section, "description"),
            Value(section, "authors") ?? Value(text, "authors"), loader, ReadIcon(zip, logo));
    }

    private static ContentMetadata? ReadMcmodInfo(ZipArchive zip)
    {
        var entry = zip.GetEntry("mcmod.info");
        if (entry is null) return null;
        using var doc = ParseLenient(entry);
        var root = doc.RootElement;
        var mod = root.ValueKind == JsonValueKind.Array ? (root.GetArrayLength() > 0 ? root[0] : default)
            : root.TryGetProperty("modList", out var list) && list.GetArrayLength() > 0 ? list[0] : default;
        if (mod.ValueKind != JsonValueKind.Object) return null;
        return new ContentMetadata(Str(mod, "modid"), Str(mod, "name"), Str(mod, "version"), Str(mod, "description"),
            People(mod, "authorList") ?? People(mod, "authors"), "forge", ReadIcon(zip, Str(mod, "logoFile")));
    }

    private static ContentMetadata? ReadPack(ZipArchive zip)
    {
        var entry = zip.GetEntry("pack.mcmeta");
        if (entry is null) return null;
        using var doc = ParseLenient(entry);
        string? description = null;
        if (doc.RootElement.TryGetProperty("pack", out var pack) && pack.TryGetProperty("description", out var d))
            description = d.ValueKind == JsonValueKind.String ? d.GetString() : FlattenText(d);
        return new ContentMetadata(null, null, null, StripFormatting(description), null, null, ReadIcon(zip, "pack.png"));
    }

    private static string? FlattenText(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.String => e.GetString(),
        JsonValueKind.Array => string.Concat(e.EnumerateArray().Select(FlattenText)),
        JsonValueKind.Object => (e.TryGetProperty("text", out var t) ? t.GetString() : "") + (e.TryGetProperty("extra", out var x) ? FlattenText(x) : ""),
        _ => null,
    };

    public static string? StripFormatting(string? text) => text is null ? null : FormattingPattern().Replace(text, "");

    private static string? ManifestVersion(ZipArchive zip)
    {
        var mf = zip.GetEntry("META-INF/MANIFEST.MF");
        if (mf is null) return null;
        using var reader = new StreamReader(mf.Open());
        while (reader.ReadLine() is { } line)
            if (line.StartsWith("Implementation-Version:", StringComparison.OrdinalIgnoreCase)) return line[23..].Trim();
        return null;
    }

    private static byte[]? ReadIcon(ZipArchive zip, string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var entry = zip.GetEntry(path.TrimStart('/', '.').TrimStart('/'));
        if (entry is null || entry.Length is 0 or > MaxIconBytes) return null;
        using var s = entry.Open();
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        var bytes = ms.ToArray();
        return bytes.Length > 8 && bytes[0] == 0x89 && bytes[1] == 0x50 ? bytes : null; // PNG only
    }

    private static JsonDocument ParseLenient(ZipArchiveEntry entry)
    {
        using var s = entry.Open();
        using var reader = new StreamReader(s);
        var text = reader.ReadToEnd();
        // Several mods ship JSON with raw newlines inside strings; replace them so the document still parses.
        text = RawNewlineInString().Replace(text, m => m.Value.Replace("\n", " ", StringComparison.Ordinal).Replace("\r", "", StringComparison.Ordinal));
        return JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string? People(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Array => string.Join(", ", v.EnumerateArray().Select(p => p.ValueKind == JsonValueKind.String ? p.GetString() : Str(p, "name")).Where(s => !string.IsNullOrEmpty(s))),
            _ => null,
        };
    }

    [GeneratedRegex("§.")]
    private static partial Regex FormattingPattern();

    [GeneratedRegex("\"(?:[^\"\\\\]|\\\\.)*\"", RegexOptions.Singleline)]
    private static partial Regex RawNewlineInString();
}
