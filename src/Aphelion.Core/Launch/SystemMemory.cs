using System.Runtime.InteropServices;

namespace Aphelion.Core.Launch;

public readonly record struct MemoryStatus(long TotalMb, long AvailableMb);

public static partial class SystemMemory
{
    public static MemoryStatus Read()
    {
        if (OperatingSystem.IsWindows())
        {
            var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            if (GlobalMemoryStatusEx(ref status))
                return new MemoryStatus((long)(status.TotalPhys / 1048576), (long)(status.AvailPhys / 1048576));
        }
        var info = GC.GetGCMemoryInfo();
        var total = info.TotalAvailableMemoryBytes / 1048576;
        return new MemoryStatus(total, Math.Max(0, total - info.MemoryLoadBytes / 1048576));
    }

    /// <summary>
    /// Heap size for "automatic" memory: what the game typically needs for its mod count, capped by what the
    /// machine can spare right now (leaving room for the OS and the JVM's off-heap memory).
    /// </summary>
    public static int AutoHeapMb(MemoryStatus memory, int modCount, bool modded)
    {
        int wanted = !modded ? 2048 : modCount switch
        {
            > 200 => 8192,
            > 120 => 6144,
            > 40 => 4096,
            _ => 3072,
        };
        var spare = (long)Math.Min(memory.AvailableMb - 768, memory.TotalMb * 0.65);
        var cap = Math.Clamp(spare, 1024, 16384);
        var heap = (int)Math.Clamp(wanted, 1024, cap);
        return heap / 256 * 256;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
