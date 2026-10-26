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
using Microsoft.Diagnostics.NETCore.Client;

namespace HeapScope.Capture;

/// <summary>Lists running .NET processes (ARCHITECTURE.md 2, sprint 1.1). Faked in front end tests.</summary>
public interface IProcessDiscovery
{
    IReadOnlyList<DotNetProcess> List(CancellationToken cancellationToken = default);

    /// <summary>One process, or null when it is not a running .NET process with a diagnostics port.</summary>
    DotNetProcess? Find(int processId);
}

/// <summary>
/// Discovers .NET processes through their diagnostics ports (<c>DiagnosticsClient.GetPublishedProcesses</c>) and reads
/// what it can about each one. A detail that cannot be read is left empty; it never fails the list.
/// </summary>
public sealed partial class ProcessDiscovery(IElevationProbe? elevation = null) : IProcessDiscovery
{
    /// <summary>HeapScope's own executables live under this folder (the worker in its worker subfolder).</summary>
    private static readonly string s_ownFolder = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);

    private readonly IElevationProbe _elevation = elevation ?? WindowsElevationProbe.Instance;

    public IReadOnlyList<DotNetProcess> List(CancellationToken cancellationToken = default)
    {
        CapturePlatform.EnsureSupported();
        var result = new List<DotNetProcess>();
        foreach (var processId in DiagnosticsClient.GetPublishedProcesses())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsOwn(processId) && Describe(processId) is { } process)
            {
                result.Add(process);
            }
        }

        result.Sort(static (a, b) =>
        {
            var byName = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            return byName != 0 ? byName : a.ProcessId.CompareTo(b.ProcessId);
        });
        return result;
    }

    public DotNetProcess? Find(int processId)
    {
        CapturePlatform.EnsureSupported();
        return FindOnThisSystem(processId);
    }

    private DotNetProcess? FindOnThisSystem(int processId) =>
        !IsOwn(processId) && DiagnosticsClient.GetPublishedProcesses().Contains(processId) ? Describe(processId) : null;

    /// <summary>
    /// HeapScope itself (this process, and the UI, CLI and worker started from this installation) is never listed or
    /// captured: a dump of the worker would contain the snapshots it holds. Matched by pid and executable path, not by
    /// name, so a user's own app called HeapScope is still listed.
    /// </summary>
    private static bool IsOwn(int processId) =>
        processId == Environment.ProcessId
        || (ReadImagePath(processId) is { } image && image.StartsWith(s_ownFolder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));

    private DotNetProcess? Describe(int processId)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            return null; // Exited since the list was taken.
        }

        using (process)
        {
            var elevation = _elevation.GetState(processId);
            var runtime = elevation == ElevationState.NotElevated || _elevation.IsCurrentProcessElevated ? ReadRuntime(process) : null;
            return new DotNetProcess(
                processId,
                Try(() => process.ProcessName) ?? $"pid {processId}",
                ReadCommandLine(processId),
                runtime?.Version,
                elevation,
                TryValue(() => process.WorkingSet64),
                TryValue(() => process.PrivateMemorySize64),
                TryValue(() => new DateTimeOffset(process.StartTime)),
                runtime?.Directory);
        }
    }

    /// <summary>
    /// The file version and folder of the process's coreclr.dll (the runtime ClrMD will need a DAC for). Single-file
    /// apps link the runtime into their executable and have no coreclr.dll: their version stays unknown.
    /// </summary>
    private static RuntimeInfo? ReadRuntime(Process process) => Try(() =>
    {
        RuntimeInfo? found = null;
        foreach (ProcessModule module in process.Modules)
        {
            using (module)
            {
                if (found is null && string.Equals(module.ModuleName, "coreclr.dll", StringComparison.OrdinalIgnoreCase))
                {
                    var version = module.FileVersionInfo;
                    found = new RuntimeInfo(
                        $"{version.FileMajorPart}.{version.FileMinorPart}.{version.FileBuildPart}.{version.FilePrivatePart}",
                        Path.GetDirectoryName(module.FileName));
                }
            }
        }

        return found;
    });

    /// <summary>The executable path through QueryFullProcessImageName (limited query rights).</summary>
    private static string? ReadImagePath(int processId)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, (uint)processId);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var buffer = new char[32768];
            var length = buffer.Length;
            return QueryFullProcessImageName(handle, 0, buffer, ref length) ? new string(buffer, 0, length) : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private sealed record RuntimeInfo(string Version, string? Directory);

    /// <summary>The command line through NtQueryInformationProcess (works with limited query rights).</summary>
    private static string? ReadCommandLine(int processId)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, (uint)processId);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var length = 4096;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var buffer = Marshal.AllocHGlobal(length);
                try
                {
                    var status = NtQueryInformationProcess(handle, ProcessCommandLineInformation, buffer, length, out var needed);
                    if (status == StatusInfoLengthMismatch && needed > length)
                    {
                        length = needed;
                        continue;
                    }

                    if (status != 0)
                    {
                        return null;
                    }

                    // UNICODE_STRING { ushort Length; ushort MaximumLength; IntPtr Buffer } followed by the characters.
                    var bytes = Marshal.ReadInt16(buffer) & 0xFFFF;
                    var chars = Marshal.ReadIntPtr(buffer, IntPtr.Size);
                    return bytes == 0 ? null : Marshal.PtrToStringUni(chars, bytes / 2);
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }

            return null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static T? Try<T>(Func<T?> read)
        where T : class
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return null;
        }
    }

    private static T? TryValue<T>(Func<T> read)
        where T : struct
    {
        try
        {
            return read();
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            return null;
        }
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int ProcessCommandLineInformation = 60;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryFullProcessImageName(IntPtr process, uint flags, [Out] char[] name, ref int size);

    [LibraryImport("ntdll.dll")]
    private static partial int NtQueryInformationProcess(IntPtr process, int informationClass, IntPtr information, int length, out int returnLength);
}
