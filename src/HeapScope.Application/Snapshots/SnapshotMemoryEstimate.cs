using System.Globalization;
using System.Runtime.InteropServices;

namespace HeapScope.Application.Snapshots;

/// <summary>Physical memory of this machine, read live (Windows: GlobalMemoryStatusEx).</summary>
public readonly record struct PhysicalMemory(long TotalBytes, long AvailableBytes)
{
    public static PhysicalMemory Current()
    {
        if (OperatingSystem.IsWindows() && NativeMethods.TryGetMemoryStatus(out var total, out var available))
        {
            return new PhysicalMemory((long)total, (long)available);
        }

        var info = GC.GetGCMemoryInfo();
        return new PhysicalMemory(info.TotalAvailableMemoryBytes, info.TotalAvailableMemoryBytes - info.MemoryLoadBytes);
    }

    private static partial class NativeMethods
    {
        public static bool TryGetMemoryStatus(out ulong totalPhysical, out ulong availablePhysical)
        {
            var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            if (GlobalMemoryStatusEx(ref status))
            {
                totalPhysical = status.TotalPhys;
                availablePhysical = status.AvailPhys;
                return true;
            }

            totalPhysical = availablePhysical = 0;
            return false;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

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
    }
}

/// <summary>
/// Estimates the worker's peak memory for a first load of a dump before asking the worker (ARCHITECTURE.md 10), so the
/// user is warned before the machine starts paging. Calibrated in sprint 1.2 on the sample dumps (memory-mapped dump
/// reading, ClrMD cache capped at 512 MB): Small 568 MB dump peaked at 227 MB, Medium 4.0 GB at 2.4 GB. The fixed
/// part leaves room for ClrMD's cache and denser heaps than the fixtures.
/// </summary>
public static class SnapshotMemoryEstimate
{
    /// <summary>Peak private bytes per byte of dump, with headroom for denser heaps than the fixtures.</summary>
    public const double BytesPerDumpByte = 0.55;

    /// <summary>Fixed part: ClrMD's capped cache, runtime structures and the worker itself.</summary>
    public const long FixedBytes = 768L * 1024 * 1024;

    public static long FirstLoadPeakBytes(long dumpBytes) =>
        dumpBytes <= 0 ? FixedBytes : (long)(dumpBytes * BytesPerDumpByte) + FixedBytes;

    /// <summary>
    /// A warning for the user when the estimated first-load peak exceeds the memory available now; null when it fits.
    /// A reopen from a valid index cache needs much less, so callers may skip the warning when a cache exists.
    /// </summary>
    public static string? Warn(long dumpBytes, PhysicalMemory memory)
    {
        var needed = FirstLoadPeakBytes(dumpBytes);
        if (needed <= memory.AvailableBytes)
        {
            return null;
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"Loading this dump needs about {Gb(needed)} of memory, but only {Gb(memory.AvailableBytes)} of {Gb(memory.TotalBytes)} is available now. Windows may start paging and the computer can become slow. Close other applications first, or continue anyway.");
    }

    private static string Gb(long bytes) => (bytes / (1024.0 * 1024 * 1024)).ToString("N1", CultureInfo.InvariantCulture) + " GB";
}
