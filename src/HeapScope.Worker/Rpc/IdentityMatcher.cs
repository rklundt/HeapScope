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
using HeapScope.Analysis;
using HeapScope.Contracts;
using HeapScope.Core;
using HeapScope.Worker.Snapshots;

namespace HeapScope.Worker.Rpc;

/// <summary>
/// Matching two snapshots' objects by address (sprint 3.3, ADR-032), and why two snapshots cannot be matched, for a
/// diff's new and dead objects and for compared sets.
/// </summary>
internal sealed class IdentityMatcher(SnapshotStore snapshots)
{
    /// <summary>Why two snapshots' objects cannot be matched by address (decision 1), or null when they can.</summary>
    public static string? WhyNoIdentity(ISnapshot baseline, ISnapshot current)
    {
        const string still = " Comparing by type still works.";
        if ((SnapshotKinds.Describe(baseline.Info.Kind).WhyNoObjectIdentity ?? SnapshotKinds.Describe(current.Info.Kind).WhyNoObjectIdentity) is { } noIdentity)
        {
            return noIdentity + still;
        }

        if (!baseline.Capabilities.HasFlag(SnapshotCapabilities.CompleteGraph) || !current.Capabilities.HasFlag(SnapshotCapabilities.CompleteGraph))
        {
            return "New and dead objects need complete dumps: a dump with missing heap segments would count the objects it lost as dead or new." + still;
        }

        var (before, after) = (baseline.Info, current.Info);
        if (before.ProcessId is null || after.ProcessId is null)
        {
            return "New and dead objects need both dumps to say which process they are of, and one of them does not." + still;
        }

        if (before.ProcessId != after.ProcessId || !string.Equals(before.ProcessName, after.ProcessName, StringComparison.OrdinalIgnoreCase))
        {
            return string.Create(CultureInfo.InvariantCulture, $"New and dead objects are matched by address, which only means something within one run of a process: these dumps are of {before.ProcessName} {before.ProcessId} and {after.ProcessName} {after.ProcessId}.{still}");
        }

        if (before.CapturedAtUtc is { } t0 && after.CapturedAtUtc is { } t1 && t1 < t0)
        {
            return "New and dead objects need the earlier dump as the baseline: the baseline was captured after the current one. Swap them in Compare (in the CLI, give the earlier dump first; for a dead set, open it in the earlier dump)." + still;
        }

        return null;
    }

    /// <summary>
    /// Why a match says the dumps are not of one run of the process (the 3.3 review): frozen objects never move or die
    /// in one run, so most of them must be at their addresses. A process id reused by another run (a container's pid 1)
    /// fails here. Null when the match looks like one run.
    /// </summary>
    public static string? WhyNotOneRun(ObjectIdentity identity) =>
        identity.BaselineFrozen >= 20 && identity.FrozenSurvivors * 2 < identity.BaselineFrozen
            ? string.Create(CultureInfo.InvariantCulture, $"These dumps do not look like one run of the process: only {identity.FrozenSurvivors:N0} of the baseline's {identity.BaselineFrozen:N0} frozen objects, which never move, are at their addresses (the process id was probably reused). Comparing by type still works.")
            : null;

    /// <summary>Matches two snapshots' objects inside a reservation of a bit per object of each.</summary>
    public async Task<ObjectIdentity> MatchAsync(Guid reserveIn, ISnapshot baseline, ISnapshot current, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        const string phase = "Matching objects by address";
        progress?.Report(new JobProgress(phase, 0, null));
        return await Task.Run(
            () =>
            {
                using var reservation = snapshots.Reserve(reserveIn, ObjectIdentity.WorkingBytes(baseline.Graph, current.Graph), phase)
                    ?? throw SnapshotAccess.NotLoaded(reserveIn);
                return ObjectIdentity.Match(baseline.Graph, current.Graph, cancellationToken);
            },
            cancellationToken).ConfigureAwait(false);
    }
}
