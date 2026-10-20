using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Aphelion.Core.Content;

public sealed record WorldInfo(string Folder, string Name, DateTimeOffset? LastPlayed, string? GameMode, bool Hardcore, string? IconDataUrl, string Path);

/// <summary>Lists singleplayer worlds, reading the few fields shown in the UI from level.dat (gzip NBT).</summary>
public static class Saves
{
    public static List<WorldInfo> List(string gameDirectory)
    {
        var saves = System.IO.Path.Combine(gameDirectory, "saves");
        if (!Directory.Exists(saves)) return [];
        var result = new List<WorldInfo>();
        foreach (var dir in Directory.EnumerateDirectories(saves))
        {
            var level = System.IO.Path.Combine(dir, "level.dat");
            if (!File.Exists(level)) continue;
            var folder = System.IO.Path.GetFileName(dir);
            Dictionary<string, object> data;
            try { data = ReadLevelData(level); }
            catch (Exception e) when (e is IOException or InvalidDataException or EndOfStreamException or ArgumentException) { data = []; }

            string? icon = null;
            var iconPath = System.IO.Path.Combine(dir, "icon.png");
            if (File.Exists(iconPath) && new FileInfo(iconPath).Length < 256 * 1024)
                icon = "data:image/png;base64," + Convert.ToBase64String(File.ReadAllBytes(iconPath));

            DateTimeOffset? lastPlayed = data.TryGetValue("LastPlayed", out var lp) && lp is long ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : File.GetLastWriteTimeUtc(level);
            var mode = data.TryGetValue("GameType", out var gt) && gt is int g ? g switch { 0 => "survival", 1 => "creative", 2 => "adventure", 3 => "spectator", _ => null } : null;
            var hardcore = data.TryGetValue("hardcore", out var hc) && hc is sbyte b && b != 0;
            var name = data.TryGetValue("LevelName", out var ln) && ln is string s && s.Length > 0 ? s : folder;
            result.Add(new WorldInfo(folder, name, lastPlayed, mode, hardcore, icon, dir));
        }
        return result.OrderByDescending(w => w.LastPlayed).ToList();
    }

    /// <summary>Reads the scalar children of the root "Data" compound. Nested compounds and lists are skipped.</summary>
    public static Dictionary<string, object> ReadLevelData(string path)
    {
        using var file = File.OpenRead(path);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var buffered = new BufferedStream(gzip);
        var r = new NbtReader(buffered);
        if (r.Byte() != 10) throw new InvalidDataException("level.dat root is not a compound");
        r.String();
        var result = new Dictionary<string, object>();
        while (true)
        {
            var type = r.Byte();
            if (type == 0) break;
            var name = r.String();
            if (type == 10 && name == "Data")
            {
                while (true)
                {
                    var t = r.Byte();
                    if (t == 0) break;
                    var n = r.String();
                    var value = r.Payload(t, depth: 0);
                    if (value is not null) result[n] = value;
                }
                break;
            }
            r.Payload(type, depth: 0);
        }
        return result;
    }

    private sealed class NbtReader(Stream s)
    {
        private readonly byte[] _buf = new byte[8];

        public byte Byte()
        {
            var b = s.ReadByte();
            if (b < 0) throw new EndOfStreamException();
            return (byte)b;
        }

        private Span<byte> Read(int n)
        {
            s.ReadExactly(_buf, 0, n);
            return _buf.AsSpan(0, n);
        }

        private short Short() => BinaryPrimitives.ReadInt16BigEndian(Read(2));
        private int Int() => BinaryPrimitives.ReadInt32BigEndian(Read(4));
        private long Long() => BinaryPrimitives.ReadInt64BigEndian(Read(8));

        public string String()
        {
            var len = (ushort)Short();
            var bytes = new byte[len];
            s.ReadExactly(bytes);
            return Encoding.UTF8.GetString(bytes);
        }

        private void Skip(long n)
        {
            var tmp = new byte[4096];
            while (n > 0)
            {
                var chunk = (int)Math.Min(n, tmp.Length);
                s.ReadExactly(tmp, 0, chunk);
                n -= chunk;
            }
        }

        /// <summary>Returns scalar values; nested structures are consumed and return null.</summary>
        public object? Payload(byte type, int depth)
        {
            if (depth > 64) throw new InvalidDataException("NBT nesting too deep");
            switch (type)
            {
                case 1: return (sbyte)Byte();
                case 2: return Short();
                case 3: return Int();
                case 4: return Long();
                case 5: return BinaryPrimitives.ReadSingleBigEndian(Read(4));
                case 6: return BinaryPrimitives.ReadDoubleBigEndian(Read(8));
                case 7: Skip(CheckedLength(Int())); return null;
                case 8: return String();
                case 9:
                {
                    var itemType = Byte();
                    var count = CheckedLength(Int());
                    for (var i = 0; i < count; i++) Payload(itemType, depth + 1);
                    return null;
                }
                case 10:
                    while (true)
                    {
                        var t = Byte();
                        if (t == 0) return null;
                        String();
                        Payload(t, depth + 1);
                    }
                case 11: Skip(CheckedLength(Int()) * 4L); return null;
                case 12: Skip(CheckedLength(Int()) * 8L); return null;
                default: throw new InvalidDataException($"Unknown NBT tag {type}");
            }
        }

        private static int CheckedLength(int n) => n is >= 0 and <= 64 * 1024 * 1024 ? n : throw new InvalidDataException("Bad NBT length");
    }
}
