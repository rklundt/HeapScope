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

namespace HeapScope.Application.Monitoring;

/// <summary>An event of a recorded timeline: a snapshot taken, or a collection forced (the CSV's event column).</summary>
public sealed record TimelineEvent(DateTimeOffset At, string Text);

/// <summary>A recorded monitoring timeline read back (sprint 3.4, a past run reopened): its samples and events.</summary>
public sealed record Timeline(IReadOnlyList<HeapSample> Samples, IReadOnlyList<TimelineEvent> Events, int SkippedLines);

/// <summary>Reads the counters CSV a monitoring run writes (its header is fixed since 1.7).</summary>
public static class TimelineFile
{
    public static async Task<Timeline> ReadAsync(string path, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var samples = new List<HeapSample>();
        var events = new List<TimelineEvent>();
        var skipped = 0;
        using var reader = new StreamReader(path);
        _ = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            var cells = Split(line);
            if (cells.Count < 14 || !DateTimeOffset.TryParse(cells[0], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at))
            {
                skipped++;
                continue;
            }

            if (cells[13].Length > 0)
            {
                events.Add(new TimelineEvent(at, cells[13]));
                continue;
            }

            long L(int i) => long.TryParse(cells[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
            var gcPercent = double.TryParse(cells[11], NumberStyles.Float, CultureInfo.InvariantCulture, out var p) ? p : 0;
            samples.Add(new HeapSample(at, L(1), L(2), L(3), L(4), L(5), L(6), L(7), L(8), L(9), L(10), gcPercent, L(12)));
        }

        return new Timeline(samples, events, skipped);
    }

    /// <summary>CSV cells: commas outside quotes, doubled quotes inside.</summary>
    private static List<string> Split(string line)
    {
        var cells = new List<string>();
        var cell = new System.Text.StringBuilder();
        var quoted = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < line.Length && line[i + 1] == '"')
                {
                    cell.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    cell.Append(c);
                }
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (c == ',')
            {
                cells.Add(cell.ToString());
                cell.Clear();
            }
            else
            {
                cell.Append(c);
            }
        }

        cells.Add(cell.ToString());
        return cells;
    }
}
