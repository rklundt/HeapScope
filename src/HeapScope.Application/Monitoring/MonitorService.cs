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
/// Starts monitoring runs (sprint 1.7): counters every interval, snapshot rules turned into captures in the current
/// session (<see cref="SnapshotSource.Timed"/>, labeled with the rule), and a timeline file in the session folder.
/// </summary>
public sealed class MonitorService(ICaptureService capture, ICounterSource? counters = null, ILogger<MonitorService>? logger = null, AllocationRecorder? allocations = null, TimeProvider? timeProvider = null)
{
    private readonly ICaptureService _capture = capture ?? throw new ArgumentNullException(nameof(capture));
    private readonly ICounterSource _counters = counters ?? new EventPipeCounterSource();
    private readonly ILogger _logger = logger ?? NullLogger<MonitorService>.Instance;
    private readonly AllocationRecorder _allocations = allocations ?? ((pid, path, ct) => new TraceService().RecordAsync(pid, path, TraceKind.Allocations, TimeSpan.FromDays(20), ct));
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    /// <summary>
    /// Starts monitoring <paramref name="processId"/>; the run ends when stopped or when the process exits.
    /// <paramref name="attach"/> subscribes to the run before the first sample can arrive.
    /// </summary>
    public MonitorRun Start(int processId, MonitorOptions options, Action<MonitorRun>? attach = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        var run = new MonitorRun(processId, options, _capture, _logger, _allocations, _time);

        // What the run will do, so a later automatic capture can be traced to its rule (sprint 2.3.7 review M4).
        _logger.LogInformation(
            "Monitoring pid {Pid} started (run {RunId}): every {IntervalSeconds:N1} s, rules {Rules}, at most {Max} snapshots, capture {Kind}, timeline {Timeline}, allocations {Allocations}",
            processId,
            run.Id,
            options.Interval.TotalSeconds,
            options.Rules.Rules.Count == 0 ? "none" : string.Join("; ", options.Rules.Rules.Select(Describe)),
            options.Rules.MaxSnapshots?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unlimited",
            options.CaptureKind,
            options.WriteTimeline,
            options.RecordAllocations);
        attach?.Invoke(run);
        run.Begin(_counters);
        return run;
    }

    private static string Describe(SnapshotRule rule) => rule switch
    {
        AboveRule above => $"{rule.Label} (rearm {above.Rearm})",
        GrowthRule growth => $"{rule.Label} (rearm {growth.Rearm})",
        _ => rule.Label,
    };
}
