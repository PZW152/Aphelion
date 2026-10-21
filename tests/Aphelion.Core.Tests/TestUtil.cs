using System.IO.Compression;
using System.Text;

namespace Aphelion.Core.Tests;

/// <summary>A unique temporary directory removed when the test finishes.</summary>
public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "aphelion-tests", Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public string File(string relative, string content)
    {
        var p = System.IO.Path.Combine(Path, relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p)!);
        System.IO.File.WriteAllText(p, content);
        return p;
    }

    /// <summary>The smallest file that passes as a Windows executable (DOS header pointing at a PE signature).</summary>
    public string Executable(string relative)
    {
        var p = System.IO.Path.Combine(Path, relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p)!);
        var bytes = new byte[72];
        bytes[0] = (byte)'M'; bytes[1] = (byte)'Z'; bytes[0x3C] = 64;
        bytes[64] = (byte)'P'; bytes[65] = (byte)'E';
        System.IO.File.WriteAllBytes(p, bytes);
        return p;
    }

    public string Zip(string relative, params (string Name, string Content)[] entries)
    {
        var p = System.IO.Path.Combine(Path, relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p)!);
        using var fs = System.IO.File.Create(p);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        foreach (var (name, content) in entries)
        {
            using var w = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
            w.Write(content);
        }
        return p;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
