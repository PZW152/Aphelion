using System.Text;
using System.Text.Json;

namespace Aphelion.Core.Storage;

/// <summary>
/// Crash-safe file writes: content goes to a sibling temp file, is flushed to disk, then replaces the
/// target in one rename. The previous version is kept as <c>.bak</c> so a corrupted file can be recovered.
/// </summary>
public static class AtomicFile
{
    public static void WriteAllText(string path, string content, bool keepBackup = true) =>
        WriteAllBytes(path, new UTF8Encoding(false).GetBytes(content), keepBackup);

    public static void WriteAllBytes(string path, ReadOnlySpan<byte> content, bool keepBackup = true)
    {
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        var temp = full + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            using (var fs = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                fs.Write(content);
                fs.Flush(flushToDisk: true);
            }

            if (File.Exists(full))
            {
                // File.Replace is atomic on NTFS and keeps the old file as the backup.
                File.Replace(temp, full, keepBackup ? full + ".bak" : null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temp, full);
            }
        }
        finally
        {
            TryDelete(temp);
        }
    }

    public static void WriteJson<T>(string path, T value, JsonSerializerOptions? options = null, bool keepBackup = true) =>
        WriteAllText(path, JsonSerializer.Serialize(value, options ?? Json.App), keepBackup);

    /// <summary>
    /// Reads JSON, falling back to the <c>.bak</c> copy when the main file is missing or unreadable.
    /// Returns <c>null</c> when neither exists. A corrupt main file is preserved as <c>.corrupt-*</c> for diagnosis.
    /// </summary>
    public static T? ReadJson<T>(string path, JsonSerializerOptions? options = null) where T : class
    {
        options ??= Json.App;
        if (TryRead(path, options, out T? value)) return value;

        var backup = path + ".bak";
        if (File.Exists(path))
        {
            var quarantine = $"{path}.corrupt-{DateTime.UtcNow.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture)}";
            try { File.Copy(path, quarantine, overwrite: true); } catch (IOException) { }
            Logging.Log.Warn($"Unreadable file {path}; preserved as {Path.GetFileName(quarantine)}.");
        }
        return TryRead(backup, options, out value) ? value : null;
    }

    private static bool TryRead<T>(string path, JsonSerializerOptions options, out T? value) where T : class
    {
        value = null;
        if (!File.Exists(path)) return false;
        try
        {
            using var fs = File.OpenRead(path);
            value = JsonSerializer.Deserialize<T>(fs, options);
            return value is not null;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }

    public static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
