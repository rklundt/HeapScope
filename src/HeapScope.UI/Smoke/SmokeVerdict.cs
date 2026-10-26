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

namespace HeapScope.UI.Smoke;

/// <summary>
/// A smoke run's exit code (ARCHITECTURE.md 8), apart from the run itself so the gate's rules are unit tested (sprint
/// 2.3.7 review M11).
/// </summary>
internal static class SmokeVerdict
{
    /// <summary>
    /// A phase's code: it failed (<see cref="SmokeRunner.SmokeJobFailed"/>), or the longest operation's upper bound passed the
    /// 100 ms budget or the lag passed twice the budget (<see cref="SmokeRunner.SmokeLagExceeded"/>), or 0. The operation is
    /// checked hard; lag, which also holds OS scheduling noise, at the 2x gate (sprint 0.3 decision 10).
    /// </summary>
    public static int Phase(bool completed, TimeSpan operation, TimeSpan lag) =>
        !completed ? SmokeRunner.SmokeJobFailed
        : operation > DispatcherWatchdog.Budget || lag > SmokeRunner.SmokeLagLimit ? SmokeRunner.SmokeLagExceeded
        : 0;

    /// <summary>
    /// The run's code from its phases' result: a whole-list grid walk fails a passing run (exit 6); headless runs (CI) do
    /// not gate lag, since a shared runner drawing in software measures the machine (decision 11 of 2.3.6); a passing run
    /// whose first window took longer than <see cref="SmokeRunner.FirstWindowLimit"/> fails with 5 (not checked headless).
    /// </summary>
    public static int Final(int result, long enumerations, bool headless, TimeSpan? firstWindow)
    {
        if (result == 0 && enumerations > 0)
        {
            return SmokeRunner.SmokeGridEnumerated;
        }

        if (headless && result == SmokeRunner.SmokeLagExceeded)
        {
            return 0;
        }

        return result == 0 && !headless && firstWindow is { } first && first > SmokeRunner.FirstWindowLimit
            ? SmokeRunner.SmokeColdStartExceeded
            : result;
    }
}
