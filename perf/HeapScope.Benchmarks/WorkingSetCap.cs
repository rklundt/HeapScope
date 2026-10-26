// HeapScope
// Copyright (C) 2026 Ray Klundt
// https://github.com/rklundt/HeapScope
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU Affero General Public License as published
// by the Free Software Foundation, version 3 only, with additional terms
// under Section 7 of that license.
//
// Additional terms, including attribution requirements, apply.
// See the ADDITIONAL-TERMS.md file distributed with this program.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU Affero General Public License for more details.
//
// You should have received a copy of the GNU Affero General Public License
// along with this program (see the LICENSE file). If not, see
// <https://www.gnu.org/licenses/>.
//
// Commercial licenses are available. See COMMERCIAL.md.

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace HeapScope.Benchmarks;

/// <summary>
/// A hard cap on this process's working set, to see what a computation does when the machine cannot keep all its pages
/// in memory (the 2.3 review M14, a stand-in for a 16 GB machine under load). Pages over the cap are trimmed to the
/// standby list, so on a machine with free memory they come back as soft faults, not disk reads: the cap shows how much
/// a computation pages, and an upper bound on what it costs needs a machine that really is short of memory.
/// </summary>
internal static partial class WorkingSetCap
{
    private const uint HardMaximum = 0x4; // QUOTA_LIMITS_HARDWS_MAX_ENABLE
    private const uint SoftMinimum = 0x2; // QUOTA_LIMITS_HARDWS_MIN_DISABLE

    /// <summary>Caps the working set at <paramref name="megabytes"/>; throws when Windows refuses.</summary>
    public static void Apply(int megabytes)
    {
        using var process = Process.GetCurrentProcess();
        var maximum = (nuint)megabytes * 1024 * 1024;
        if (!SetProcessWorkingSetSizeEx(process.Handle, 16 * 1024 * 1024, maximum, HardMaximum | SoftMinimum))
        {
            throw new InvalidOperationException($"The working set could not be capped at {megabytes} MB (error {Marshal.GetLastPInvokeError()}).");
        }
    }

    /// <summary>Page faults of this process so far (soft and hard) and its peak working set.</summary>
    public static (uint PageFaults, long PeakWorkingSet) Counters()
    {
        using var process = Process.GetCurrentProcess();
        var counters = new ProcessMemoryCounters { Size = (uint)Marshal.SizeOf<ProcessMemoryCounters>() };
        return K32GetProcessMemoryInfo(process.Handle, ref counters, counters.Size)
            ? (counters.PageFaultCount, (long)counters.PeakWorkingSetSize)
            : (0, process.PeakWorkingSet64);
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetProcessWorkingSetSizeEx(IntPtr process, nuint minimum, nuint maximum, uint flags);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool K32GetProcessMemoryInfo(IntPtr process, ref ProcessMemoryCounters counters, uint size);

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessMemoryCounters
    {
        public uint Size;
        public uint PageFaultCount;
        public nuint PeakWorkingSetSize;
        public nuint WorkingSetSize;
        public nuint QuotaPeakPagedPoolUsage;
        public nuint QuotaPagedPoolUsage;
        public nuint QuotaPeakNonPagedPoolUsage;
        public nuint QuotaNonPagedPoolUsage;
        public nuint PagefileUsage;
        public nuint PeakPagefileUsage;
    }
}
