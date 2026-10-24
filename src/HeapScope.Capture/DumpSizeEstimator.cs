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

using System.Runtime.InteropServices;

namespace HeapScope.Capture;

/// <summary>
/// Estimates the size of a full dump (sprint 1.1 review). A full dump holds every committed, readable page of the
/// process: private memory, loaded images and mapped files. Walking the address space with <c>VirtualQueryEx</c> sums
/// exactly those pages; when the walk is not allowed, the estimate falls back to private bytes plus a fixed part for
/// images and mapped files (measured on the samples: 250 to 660 MB outside the GC heap).
/// </summary>
public static partial class DumpSizeEstimator
{
    /// <summary>Headers, module lists, thread contexts and other streams.</summary>
    public const long StreamBytes = 16L * 1024 * 1024;

    /// <summary>Fallback: images and mapped files outside private memory.</summary>
    public const long FallbackImageBytes = 384L * 1024 * 1024;

    /// <summary>Fallback headroom over private bytes.</summary>
    public const double FallbackFactor = 1.1;

    private const uint ProcessQueryInformation = 0x0400;
    private const uint MemCommit = 0x1000;
    private const uint PageNoAccess = 0x01;
    private const uint PageGuard = 0x100;

    /// <summary>The fallback estimate from private bytes alone; errs high.</summary>
    public static long FromPrivateBytes(long? privateBytes) =>
        (long)((privateBytes ?? 0) * FallbackFactor) + FallbackImageBytes + StreamBytes;

    /// <summary>The estimate for a running process: the committed readable pages, or the fallback.</summary>
    public static long Estimate(DotNetProcess process)
    {
        ArgumentNullException.ThrowIfNull(process);
        return CommittedReadableBytes(process.ProcessId) is { } committed
            ? committed + StreamBytes
            : FromPrivateBytes(process.PrivateBytes);
    }

    /// <summary>Sum of committed pages that are neither no-access nor guard pages; null when the walk is denied.</summary>
    public static long? CommittedReadableBytes(int processId)
    {
        var handle = OpenProcess(ProcessQueryInformation, false, (uint)processId);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            long total = 0;
            var info = default(MemoryBasicInformation);
            var size = (nuint)Marshal.SizeOf<MemoryBasicInformation>();
            ulong address = 0;
            while (VirtualQueryEx(handle, (nuint)address, out info, size) != 0)
            {
                if (info.State == MemCommit && (info.Protect & PageNoAccess) == 0 && (info.Protect & PageGuard) == 0)
                {
                    total += (long)info.RegionSize;
                }

                var next = (ulong)info.BaseAddress + (ulong)info.RegionSize;
                if (next <= address)
                {
                    break;
                }

                address = next;
            }

            return total;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryBasicInformation
    {
        public nuint BaseAddress;
        public nuint AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public nuint RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nuint VirtualQueryEx(IntPtr process, nuint address, out MemoryBasicInformation buffer, nuint length);
}
