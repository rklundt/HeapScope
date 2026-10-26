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

namespace HeapScope.Capture;

/// <summary>
/// Elevation off Windows (sprint 2.3.5): whether this process runs as root, from .NET's own cross-platform check. Another
/// process's state is not looked up yet (Linux and macOS capture is a later sprint), so it is <see cref="ElevationState.Unknown"/>.
/// </summary>
public sealed class UnixElevationProbe : IElevationProbe
{
    public static UnixElevationProbe Instance { get; } = new();

    public bool IsCurrentProcessElevated => Environment.IsPrivilegedProcess;

    public ElevationState GetState(int processId) => ElevationState.Unknown;
}

/// <summary>Capture on the system HeapScope runs on (sprint 2.3.5).</summary>
public static class CapturePlatform
{
    /// <summary>The elevation probe of this system.</summary>
    public static IElevationProbe ElevationProbe() =>
        OperatingSystem.IsWindows() ? WindowsElevationProbe.Instance : UnixElevationProbe.Instance;

    /// <summary>
    /// Refuses capture where it is not built yet: listing processes and writing dumps call Windows APIs. Opening and
    /// analyzing snapshots works everywhere.
    /// </summary>
    /// <exception cref="PlatformNotSupportedException">Not running on Windows.</exception>
    public static void EnsureSupported() => EnsureSupported(OperatingSystem.IsWindows());

    /// <summary><see cref="EnsureSupported()"/> for a given system (tests).</summary>
    public static void EnsureSupported(bool isWindows)
    {
        if (!isWindows)
        {
            throw new PlatformNotSupportedException(
                "Capturing from HeapScope runs on Windows for now. On Linux or macOS, take a dump with "
                + "'dotnet-dump collect --process-id <pid> --type Full' and open it with HeapScope.");
        }
    }
}
