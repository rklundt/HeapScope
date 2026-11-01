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

using System.Globalization;
using HeapScope.Application.Sessions;
using HeapScope.Capture;
using HeapScope.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using HeapScope.Core;
using HeapScope.Application.Events;

namespace HeapScope.Application.Monitoring;

/// <summary>
/// How to monitor: the sample interval, the snapshot rules, whether to write the timeline file, and what rules capture
/// (sprint 1.8: full dumps, or gcdumps, which are smaller but force a full GC in the target that shows in its counters).
/// </summary>
/// <param name="RecordAllocations">Records an allocation trace for the whole run (sprint 3.4, decision 4).</param>
/// <param name="ProcessName">The target's name, for the run's record.</param>
/// <param name="RecordRun">
/// Whether the run uses a session at all (its record, its timeline, its trace): off for smoke runs, which leave nothing in
/// the session library (sprint 3.4 re-review).
/// </param>
/// <param name="TraceLimitBytes">
/// The allocation trace stops at this size (review H7 of 3.4): 1 GB by default, which the Allocations page reads well
/// inside its memory budget on a 16 GB laptop; the run goes on without it.
/// </param>
public sealed record MonitorOptions(TimeSpan Interval, MonitorRules Rules, bool WriteTimeline = true, SnapshotKindDto CaptureKind = SnapshotKindDto.FullDump, bool RecordAllocations = false, string? ProcessName = null, long TraceLimitBytes = MonitorOptions.DefaultTraceLimitBytes, bool RecordRun = true)
{
    /// <summary>The default allocation trace limit, 1 GB.</summary>
    public const long DefaultTraceLimitBytes = 1L << 30;

    public static MonitorOptions Default { get; } = new(TimeSpan.FromSeconds(1), MonitorRules.None);
}
