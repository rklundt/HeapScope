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

/// <summary>Answers the elevation questions of ADR-014. Faked in tests.</summary>
public interface IElevationProbe
{
    /// <summary>True when this HeapScope process runs elevated (as administrator).</summary>
    bool IsCurrentProcessElevated { get; }

    /// <summary>The elevation state of another process.</summary>
    ElevationState GetState(int processId);
}

/// <summary>
/// Windows implementation: a process whose token cannot be opened for query (elevated, or another user's) or whose
/// token reports elevation needs an elevated HeapScope.
/// </summary>
public sealed partial class WindowsElevationProbe : IElevationProbe
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const int TokenElevation = 20;
    private const int ErrorAccessDenied = 5;

    public static WindowsElevationProbe Instance { get; } = new();

    /// <summary>The same rule as for targets: the current process token's elevation (TokenElevation).</summary>
    public bool IsCurrentProcessElevated { get; } = Query(Environment.ProcessId) == ElevationState.Elevated;

    public ElevationState GetState(int processId) => Query(processId);

    private static ElevationState Query(int processId)
    {
        var process = OpenProcess(ProcessQueryLimitedInformation, false, (uint)processId);
        if (process == IntPtr.Zero)
        {
            return Marshal.GetLastPInvokeError() == ErrorAccessDenied ? ElevationState.Elevated : ElevationState.Unknown;
        }

        try
        {
            if (!OpenProcessToken(process, TokenQuery, out var token))
            {
                return Marshal.GetLastPInvokeError() == ErrorAccessDenied ? ElevationState.Elevated : ElevationState.Unknown;
            }

            try
            {
                return GetTokenInformation(token, TokenElevation, out var elevated, sizeof(int), out _)
                    ? (elevated != 0 ? ElevationState.Elevated : ElevationState.NotElevated)
                    : ElevationState.Unknown;
            }
            finally
            {
                CloseHandle(token);
            }
        }
        finally
        {
            CloseHandle(process);
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetTokenInformation(IntPtr token, int informationClass, out int information, int length, out int returnLength);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);
}

/// <summary>A dump needs an elevated HeapScope (ADR-014).</summary>
public sealed class ElevationRequiredException : Exception
{
    public ElevationRequiredException()
    {
    }

    public ElevationRequiredException(string message)
        : base(message)
    {
    }

    public ElevationRequiredException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public static ElevationRequiredException For(int processId, string? name, Exception? inner = null)
    {
        var message = $"{name ?? "The process"} (pid {processId}) runs elevated or as another user, so HeapScope cannot dump it. " +
            "Run HeapScope as administrator (right-click, Run as administrator) and capture again.";
        return inner is null ? new ElevationRequiredException(message) : new ElevationRequiredException(message, inner);
    }
}
