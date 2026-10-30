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
using CommunityToolkit.Mvvm.ComponentModel;
using HeapScope.Application.Presentation;
using HeapScope.Application.Snapshots;
using HeapScope.Contracts;
using Microsoft.Extensions.Logging;

namespace HeapScope.UI.ViewModels;

/// <summary>A row of an allocation tree grid (sprint 2.5): a call tree or back-trace frame, filled from the worker's columns by name.</summary>
public sealed class AllocationRowItem(int index) : ObservableObject, IIndexedRow
{
    public int Index { get; } = index;

    public bool IsLoaded { get; private set; }

    public string Key { get; private set; } = string.Empty;

    public string ParentKey { get; private set; } = string.Empty;

    public int Depth { get; private set; }

    /// <summary>One of <see cref="AllocationRowKinds"/>.</summary>
    public string Kind { get; private set; } = string.Empty;

    /// <summary>The method, or what a "more" row stands for.</summary>
    public string Name { get; private set; } = string.Empty;

    /// <summary>Sampled bytes of the stacks through this row.</summary>
    public long Total { get; private set; }

    /// <summary>Sampled bytes allocated in the frame itself, or null where it has no meaning.</summary>
    public long? Own { get; private set; }

    public long Samples { get; private set; }

    public double Percent { get; private set; }

    public int Children { get; private set; }

    public bool IsExpanded { get; private set; }

    public bool CanExpand => Kind == AllocationRowKinds.More || Children > 0;

    public double Indent => Depth * DominatorRowItem.IndentPerLevel;

    public string Glyph => !CanExpand ? string.Empty : IsExpanded ? "▾" : "▸";

    public static VirtualRowList<AllocationRowItem> CreateList(ResultSetHandle? rows, ResultSetInfo info, Action<Exception>? onError = null, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(info);
        var at = info.Columns.Select((c, i) => (c.Name, i)).ToDictionary(c => c.Name, c => c.i, StringComparer.Ordinal);
        if (rows is not null)
        {
            foreach (var column in new[] { AllocationTreeColumns.Key, AllocationTreeColumns.Kind, AllocationTreeColumns.Total })
            {
                ResultColumns.Require(info.Columns, column);
            }
        }

        return new VirtualRowList<AllocationRowItem>(rows, info.TotalCount, i => new AllocationRowItem(i), (row, cells) => row.Fill(cells, at), onError, logger);
    }

    private void Fill(IReadOnlyList<string?> cells, Dictionary<string, int> at)
    {
        string? Cell(string column) => at.TryGetValue(column, out var i) && i < cells.Count ? cells[i] : null;
        long? Number(string column) => long.TryParse(Cell(column), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;
        Key = Cell(AllocationTreeColumns.Key) ?? string.Empty;
        ParentKey = Cell(AllocationTreeColumns.ParentKey) ?? string.Empty;
        Depth = (int)(Number(AllocationTreeColumns.Depth) ?? 0);
        Kind = Cell(AllocationTreeColumns.Kind) ?? string.Empty;
        Name = Cell(AllocationTreeColumns.Name) ?? string.Empty;
        Total = Number(AllocationTreeColumns.Total) ?? 0;
        Own = Number(AllocationTreeColumns.Own);
        Samples = Number(AllocationTreeColumns.Samples) ?? 0;
        Percent = double.TryParse(Cell(AllocationTreeColumns.Percent), NumberStyles.Float, CultureInfo.InvariantCulture, out var p) ? p : 0;
        Children = (int)(Number(AllocationTreeColumns.Children) ?? 0);
        IsExpanded = Cell(AllocationTreeColumns.Expanded) == "1";
        IsLoaded = true;
        OnPropertyChanged(string.Empty);
    }
}

/// <summary>One of the top call stacks (sprint 2.5): its rank, sampled bytes and share, and its frames, the allocating method first.</summary>
public sealed record AllocationStackItem(int Rank, long Bytes, double Percent, long Samples, IReadOnlyList<string> Frames)
{
    /// <summary>The allocating method.</summary>
    public string Top => Frames.Count > 0 ? Frames[0] : string.Empty;

    /// <summary>The method that called it.</summary>
    public string Caller => Frames.Count > 1 ? Frames[1] : string.Empty;
}
