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

namespace HeapScope.UI.Services;

/// <summary>
/// The longest UI operation as the highest-priority probes see it (sprint 2.3.6 review M2; extracted for tests in 2.3.7,
/// review M11). A probe waits only for the operation already running: its wait is a lower bound of that operation. The
/// operation started after the previous probe ran (that probe ran first), so the time from the previous probe's run to
/// this one's is an upper bound. Times are <see cref="System.Diagnostics.Stopwatch"/> ticks; thread safe.
/// </summary>
internal sealed class OperationBound
{
    private long _resetAt;
    private long _lastRun;
    private long _maxWait;
    private long _maxBound;

    /// <summary>The longest probe wait since the last reset: a lower bound.</summary>
    public long MaxWait => Interlocked.Read(ref _maxWait);

    /// <summary>The longest time between two probe runs since the last reset: an upper bound.</summary>
    public long MaxBound => Interlocked.Read(ref _maxBound);

    /// <summary>Clears the maxima at <paramref name="now"/>; probes posted earlier do not count.</summary>
    public void Reset(long now)
    {
        Interlocked.Exchange(ref _resetAt, now);
        Interlocked.Exchange(ref _maxWait, 0);
        Interlocked.Exchange(ref _maxBound, 0);
    }

    /// <summary>Records a probe posted at <paramref name="posted"/> that ran at <paramref name="ran"/>; returns its wait.</summary>
    public long OnProbeRan(long posted, long ran)
    {
        var previousRun = Interlocked.Exchange(ref _lastRun, ran);
        var wait = ran - posted;
        if (posted >= Interlocked.Read(ref _resetAt))
        {
            RecordMax(ref _maxWait, wait);
            RecordMax(ref _maxBound, previousRun == 0 ? wait : ran - previousRun);
        }

        return wait;
    }

    private static void RecordMax(ref long maximum, long value)
    {
        var current = Interlocked.Read(ref maximum);
        while (value > current)
        {
            var seen = Interlocked.CompareExchange(ref maximum, value, current);
            if (seen == current)
            {
                return;
            }

            current = seen;
        }
    }
}
