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

/// <summary>Whether a process can be dumped from this HeapScope process (ADR-014).</summary>
public enum ElevationState
{
    /// <summary>The process runs unelevated as the current user: it can be dumped.</summary>
    NotElevated = 0,

    /// <summary>The process is elevated, or belongs to another user: only an elevated HeapScope can dump it.</summary>
    Elevated = 1,

    /// <summary>The state could not be determined (the process exited, or the query failed).</summary>
    Unknown = 2,
}

/// <summary>A running .NET process that exposes a diagnostics port. Details that cannot be read are null.</summary>
/// <param name="ProcessId">Process id.</param>
/// <param name="Name">Process name (executable without extension).</param>
/// <param name="CommandLine">Full command line, when readable.</param>
/// <param name="RuntimeVersion">File version of the loaded runtime (coreclr.dll), when readable.</param>
/// <param name="Elevation">Whether this HeapScope process can dump it.</param>
/// <param name="WorkingSetBytes">Working set, when readable.</param>
/// <param name="PrivateBytes">Private (committed) bytes, when readable; the dump size estimate starts from it.</param>
/// <param name="StartTime">Process start time, when readable.</param>
/// <param name="RuntimeDirectory">Folder of the loaded coreclr.dll (where the matching DAC lives), when readable.</param>
public sealed record DotNetProcess(
    int ProcessId,
    string Name,
    string? CommandLine,
    string? RuntimeVersion,
    ElevationState Elevation,
    long? WorkingSetBytes,
    long? PrivateBytes,
    DateTimeOffset? StartTime,
    string? RuntimeDirectory = null);
