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

using HeapScope.UI.Services;
using HeapScope.UI.Smoke;

namespace HeapScope.UI.Tests;

/// <summary>
/// The instrument behind every responsiveness gate (sprint 2.3.7 review M11): the operation bound and the exit codes,
/// tested without a running app.
/// </summary>
public sealed class SmokeGateTests
{
    [Fact]
    public void A_probe_posted_inside_an_operation_bounds_it_from_both_sides()
    {
        // An operation runs from 1,000 to 1,120; the previous probe ran at 990; a probe posted at 1,030 runs at 1,120.
        var bound = new OperationBound();
        bound.OnProbeRan(posted: 980, ran: 990);
        var wait = bound.OnProbeRan(posted: 1_030, ran: 1_120);

        wait.ShouldBe(90);
        bound.MaxWait.ShouldBe(90, "the wait misses the start of the operation: a lower bound");
        bound.MaxBound.ShouldBe(130, "from the previous probe's run: an upper bound of the 120-tick operation");
    }

    [Fact]
    public void A_reset_forgets_probes_posted_before_it()
    {
        var bound = new OperationBound();
        bound.OnProbeRan(posted: 0, ran: 500);
        bound.Reset(now: 600);
        bound.OnProbeRan(posted: 550, ran: 700);
        bound.MaxWait.ShouldBe(0, "posted before the reset");

        bound.OnProbeRan(posted: 710, ran: 720);
        (bound.MaxWait, bound.MaxBound).ShouldBe((10L, 20L));
    }

    [Theory]
    [InlineData(false, 10, 10, SmokeRunner.SmokeJobFailed)]
    [InlineData(true, 99, 199, 0)]
    [InlineData(true, 101, 10, SmokeRunner.SmokeLagExceeded)]
    [InlineData(true, 10, 201, SmokeRunner.SmokeLagExceeded)]
    public void A_phase_fails_on_its_job_the_operation_budget_or_twice_the_lag_budget(bool completed, int operationMs, int lagMs, int expected) =>
        SmokeVerdict.Phase(completed, TimeSpan.FromMilliseconds(operationMs), TimeSpan.FromMilliseconds(lagMs)).ShouldBe(expected);

    [Theory]
    [InlineData(0, 0, false, 5, 0)]
    [InlineData(0, 1, false, 5, SmokeRunner.SmokeGridEnumerated)]
    [InlineData(SmokeRunner.SmokeLagExceeded, 0, true, 5, 0)]
    [InlineData(SmokeRunner.SmokeLagExceeded, 0, false, 5, SmokeRunner.SmokeLagExceeded)]
    [InlineData(0, 0, false, 31, SmokeRunner.SmokeColdStartExceeded)]
    [InlineData(0, 0, true, 31, 0)]
    [InlineData(SmokeRunner.SmokeJobFailed, 1, false, 31, SmokeRunner.SmokeJobFailed)]
    public void The_run_maps_grid_walks_headless_lag_and_a_slow_first_window(int result, long enumerations, bool headless, int firstWindowSeconds, int expected) =>
        SmokeVerdict.Final(result, enumerations, headless, TimeSpan.FromSeconds(firstWindowSeconds)).ShouldBe(expected);
}
