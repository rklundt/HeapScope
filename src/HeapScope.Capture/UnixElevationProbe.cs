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
