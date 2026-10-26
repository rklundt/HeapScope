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

using System.IO.Hashing;
using System.Runtime.InteropServices;
using HeapScope.Core;
using HeapScope.Core.Graph;

namespace HeapScope.Analysis.Inspections;

/// <summary>
/// The BCL generic collections the collection inspections understand, by their field layout in .NET's
/// System.Private.CoreLib: <c>List&lt;T&gt;</c> (<c>_size</c>, <c>_items</c>), <c>Dictionary&lt;K,V&gt;</c> and
/// <c>HashSet&lt;T&gt;</c> (<c>_count</c> minus <c>_freeCount</c>, <c>_entries</c> and <c>_buckets</c>),
/// <c>Queue&lt;T&gt;</c> and <c>Stack&lt;T&gt;</c> (<c>_size</c>, <c>_array</c>). Other collections are ignored rather
/// than guessed at.
/// </summary>
internal static class Collections
{
    private static readonly (string Prefix, string Count, string? Free, string Items, string? Buckets)[] s_layouts =
    [
        ("System.Collections.Generic.List<", "_size", null, "_items", null),
        ("System.Collections.Generic.Dictionary<", "_count", "_freeCount", "_entries", "_buckets"),
        ("System.Collections.Generic.HashSet<", "_count", "_freeCount", "_entries", "_buckets"),
        ("System.Collections.Generic.Queue<", "_size", null, "_array", null),
        ("System.Collections.Generic.Stack<", "_size", null, "_array", null),
    ];

    public static readonly InspectionColumn[] Columns =
    [
        new(InspectionColumns.Address, InspectionColumnKind.Address, IsKey: true),
        new(InspectionColumns.Type, InspectionColumnKind.Text),
        new(InspectionColumns.Count, InspectionColumnKind.Integer),
        new(InspectionColumns.Capacity, InspectionColumnKind.Integer),
        new(InspectionColumns.Wasted, InspectionColumnKind.Bytes),
        new(InspectionColumns.Bytes, InspectionColumnKind.Bytes),
    ];

    /// <summary>Every covered collection with its count, capacity and backing storage, read through the typed reader.</summary>
    public static IEnumerable<Shape> Shapes(InspectionContext context)
    {
        var graph = context.Graph;
        var reader = context.Reader ?? throw new InvalidOperationException("The collection inspections need a reader.");
        var layouts = new int[graph.Types.Count];
        for (var t = 0; t < layouts.Length; t++)
        {
            // Arrays of collections and nested types (Dictionary<,>+KeyCollection) share the prefix but not the layout.
            var name = graph.Types.GetName(t);
            layouts[t] = graph.Types.Kinds[t].HasFlag(TypeKind.Array) || name.Contains('+', StringComparison.Ordinal)
                ? -1
                : Array.FindIndex(s_layouts, l => name.StartsWith(l.Prefix, StringComparison.Ordinal));
        }

        for (var i = 0; i < graph.ObjectCount; i++)
        {
            if ((i & 0xFFF) == 0)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                context.Report("Reading collections", i, graph.ObjectCount);
            }

            var layout = layouts[graph.TypeIndices[i]];
            if (layout < 0)
            {
                continue;
            }

            var (_, countField, freeField, itemsField, bucketsField) = s_layouts[layout];
            var address = graph.Addresses[i];
            if (reader.ReadField(address, countField) is not { } count)
            {
                continue;
            }

            var used = count.Integer - (freeField is not null && reader.ReadField(address, freeField) is { } free ? free.Integer : 0);
            var items = Backing(graph, reader.ReadField(address, itemsField));
            var buckets = bucketsField is null ? default : Backing(graph, reader.ReadField(address, bucketsField));
            yield return new Shape(i, used, items.Length, items.ElementBytes, items.Bytes + buckets.Bytes);
        }
    }

    /// <summary>A backing array's length, element size and total size, from the graph.</summary>
    private static (long Length, long ElementBytes, long Bytes) Backing(HeapGraph graph, ObjectValue? value)
    {
        if (value is not { Kind: ObjectValueKind.Array } array || !graph.TryGetIndex(array.Address, out var index))
        {
            return default;
        }

        return (array.ElementCount, graph.Types.ComponentSizes[graph.TypeIndices[index]], (long)graph.GetSize(index));
    }

    public static RowTable<CollectionRow> Table(HeapGraph graph, CollectionRow[] rows) =>
        new(Columns, rows, 40, (row, cells) =>
        {
            cells[0] = InspectionRows.Hex(graph.Addresses[row.Object]);
            cells[1] = graph.Types.GetName(graph.TypeIndices[row.Object]);
            cells[2] = InspectionRows.Number(row.Count);
            cells[3] = InspectionRows.Number(row.Capacity);
            cells[4] = InspectionRows.Number(row.Wasted);
            cells[5] = InspectionRows.Number(row.Bytes);
        });

    /// <param name="BackingBytes">The backing arrays' sizes (entries and buckets for hashed collections).</param>
    public sealed record Shape(int Object, long Count, long Capacity, long ElementBytes, long BackingBytes);
}

internal sealed record CollectionRow(int Object, long Count, long Capacity, long Wasted, long Bytes);

/// <summary>Covered collections that hold nothing but have allocated storage.</summary>
internal sealed class EmptyCollectionsInspection : IInspection
{
    public string Name => "empty-collections";

    public string Title => "Empty collections";

    public string Cause => "Collections created with a capacity, or emptied, keep their storage although they hold nothing.";

    public string Fix => "Create collections lazily or without a capacity, share one empty instance, or call TrimExcess after clearing.";

    public SnapshotCapabilities Requires => SnapshotCapabilities.FieldValues;

    public InspectionResult Run(InspectionContext context)
    {
        var graph = context.Graph;
        var rows = Collections.Shapes(context)
            .Where(s => s.Count == 0 && s.Capacity > 0)
            .Select(s => new CollectionRow(s.Object, 0, s.Capacity, s.BackingBytes, (long)graph.GetSize(s.Object) + s.BackingBytes))
            .OrderByDescending(r => r.Wasted).ThenBy(r => r.Object)
            .ToArray();
        return new InspectionResult(rows.Length, rows.Sum(r => r.Bytes), rows.Sum(r => r.Wasted), null, Collections.Table(graph, rows));
    }
}

/// <summary>Covered collections whose capacity is at least twice their count plus 16.</summary>
internal sealed class OversizedCollectionsInspection : IInspection
{
    public const int Slack = 16;

    public string Name => "oversized-collections";

    public string Parameters => $"Capacity>=2*Count+{Slack}";

    public string Title => "Oversized collections";

    public string Cause => "Capacity far above the count: the unused slots of the backing array are allocated and never used.";

    public string Fix => "Pass a realistic capacity, or call TrimExcess once a collection stops growing.";

    public SnapshotCapabilities Requires => SnapshotCapabilities.FieldValues;

    public InspectionResult Run(InspectionContext context)
    {
        var graph = context.Graph;
        var rows = Collections.Shapes(context)
            .Where(s => s.Count > 0 && s.Capacity >= (2 * s.Count) + Slack)
            .Select(s => new CollectionRow(s.Object, s.Count, s.Capacity, (s.Capacity - s.Count) * s.ElementBytes, (long)graph.GetSize(s.Object) + s.BackingBytes))
            .OrderByDescending(r => r.Wasted).ThenBy(r => r.Object)
            .ToArray();
        return new InspectionResult(rows.Length, rows.Sum(r => r.Bytes), rows.Sum(r => r.Wasted), null, Collections.Table(graph, rows));
    }
}

/// <summary>
/// 64-bit XxHash3 of strings' full UTF-16 text, streamed from the dump in pieces (sprint 1.6 review H2): no string is
/// built, so a group of large equal-size strings costs one pooled buffer, not their text. Equal hashes of strings of
/// equal size are treated as equal text (decision 3; a collision would merge two values, odds negligible).
/// </summary>
public static class StringHashes
{
    /// <summary>Hashes the strings at <paramref name="addresses"/>; <paramref name="valid"/> is false where one is not readable.</summary>
    public static ulong[] Compute(IObjectReader reader, IReadOnlyList<ulong> addresses, out bool[] valid, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(addresses);
        var hashes = new ulong[addresses.Count];
        var ok = new bool[addresses.Count];
        var hash = new XxHash3();
        reader.ReadStringPieces(addresses, (index, length, piece, last) =>
        {
            if (length < 0)
            {
                hash.Reset();
                return;
            }

            hash.Append(MemoryMarshal.AsBytes(piece));
            if (last)
            {
                hashes[index] = hash.GetCurrentHashAsUInt64();
                ok[index] = true;
                hash.Reset();
            }
        }, cancellationToken);
        valid = ok;
        return hashes;
    }
}

/// <summary>
/// Strings with the same text held more than once (decision 3 of 1.6): strings are grouped by size first (equal text
/// means equal size), then the text of each group of two or more is hashed (64-bit XxHash3 of the UTF-16 text, plus the
/// size, as the key), one size group at a time, so memory stays at the largest group. Each copy after the first is
/// wasted.
/// </summary>
internal sealed class DuplicateStringsInspection : IInspection
{
    /// <summary>Characters of a value shown in a row.</summary>
    public const int ShownLength = 100;

    private static readonly InspectionColumn[] s_columns =
    [
        new(InspectionColumns.Value, InspectionColumnKind.Text),
        new(InspectionColumns.Copies, InspectionColumnKind.Integer),
        new(InspectionColumns.Bytes, InspectionColumnKind.Bytes),
        new(InspectionColumns.Wasted, InspectionColumnKind.Bytes),
        new(InspectionColumns.Address, InspectionColumnKind.Address, IsKey: true),
        new(InspectionColumns.TypeName, InspectionColumnKind.Text),
        new(InspectionColumns.Module, InspectionColumnKind.Text),
        new(InspectionColumns.Scope, InspectionColumnKind.Text),
    ];

    public string Name => "duplicate-strings";

    // Version 2 (sprint 2.3.7): unreadable strings are counted and named in the note; the note says unreachable strings
    // are included.
    public int Version => 2;

    public string Parameters => "equal UTF-16 text by 64-bit XxHash3 within equal sizes; reachable and unreachable strings";

    public string Title => "Duplicate strings";

    public string Cause => "The same text is held in many separate string objects; every copy after the first is wasted.";

    public string Fix => "Reuse one instance: string.Intern or a lookup table for repeated values, or keep the data as enums or numbers.";

    public SnapshotCapabilities Requires => SnapshotCapabilities.StringContents;

    public InspectionResult Run(InspectionContext context)
    {
        var graph = context.Graph;
        var reader = context.Reader ?? throw new InvalidOperationException("Duplicate strings needs a reader.");

        // Strings ordered by size (counted first, exact arrays): each run of equal sizes may hold duplicates.
        var total = 0;
        for (var i = 0; i < graph.ObjectCount; i++)
        {
            if ((i & 0xFFFF) == 0)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
            }

            if (graph.Types.Kinds[graph.TypeIndices[i]].HasFlag(TypeKind.String))
            {
                total++;
            }
        }

        var sorted = new int[total];
        var sizes = new ulong[total];
        for (int i = 0, k = 0; i < graph.ObjectCount && k < total; i++)
        {
            if (graph.Types.Kinds[graph.TypeIndices[i]].HasFlag(TypeKind.String))
            {
                sorted[k] = i;
                sizes[k++] = graph.GetSize(i);
            }
        }

        Array.Sort(sizes, sorted);
        var rows = new List<DuplicateRow>();
        var done = 0;
        var groups = 0;
        long hashed = 0;
        long unreadable = 0;
        for (var start = 0; start < sorted.Length;)
        {
            var end = start + 1;
            while (end < sorted.Length && sizes[end] == sizes[start])
            {
                end++;
            }

            if (end - start > 1)
            {
                groups++;
                hashed += end - start;
                unreadable += HashGroup(graph, reader, sorted.AsSpan(start, end - start).ToArray(), (long)sizes[start], rows, context.CancellationToken);
            }

            done += end - start;
            context.Report("Comparing strings", done, sorted.Length);
            start = end;
        }

        // Text for the rows: the first characters of one copy each.
        var shown = new string?[rows.Count];
        reader.ReadStrings([.. rows.Select(r => graph.Addresses[r.Representative])], ShownLength + 1, (i, text) => shown[i] = text, context.CancellationToken);
        var table = rows.Select((r, i) => r with { Text = Shorten(shown[i]) })
            .OrderByDescending(r => r.Wasted).ThenBy(r => r.Representative)
            .ToArray();
        // Every string in the dump counts, reachable or not: a full dump also holds garbage the next collection frees, the
        // likeliest reason a figure here is disputed (sprint 2.3.7 review H3); unreadable strings are left out, and said so.
        var note = unreadable > 0
            ? $"{unreadable:N0} strings could not be read from the dump and were left out. Unreachable strings are included."
            : "Unreachable strings (garbage the next collection would free) are included.";
        var basis = $"strings {total}, size groups hashed {groups}, strings hashed {hashed}, unreadable {unreadable}";
        return new InspectionResult(table.Length, table.Sum(r => r.Copies * r.Size), table.Sum(r => r.Wasted), note, new RowTable<DuplicateRow>(s_columns, table, 80 + (2 * ShownLength), (row, cells) =>
        {
            var type = graph.TypeIndices[row.Representative];
            cells[0] = row.Text;
            cells[1] = InspectionRows.Number(row.Copies);
            cells[2] = InspectionRows.Number(row.Size);
            cells[3] = InspectionRows.Number(row.Wasted);
            cells[4] = InspectionRows.Hex(graph.Addresses[row.Representative]);
            cells[5] = graph.Types.GetName(type);
            cells[6] = TypeGroups.ModuleKey(graph.Types.GetModule(type));
            cells[7] = InspectionScopes.SameValue;
        }), basis);
    }

    /// <summary>
    /// Hashes one size group and adds a row per hash shared by two or more strings (sorting the hashes, no table). Returns
    /// how many strings of the group could not be read.
    /// </summary>
    private static int HashGroup(HeapGraph graph, IObjectReader reader, int[] group, long size, List<DuplicateRow> rows, CancellationToken cancellationToken)
    {
        var addresses = new ulong[group.Length];
        for (var k = 0; k < group.Length; k++)
        {
            addresses[k] = graph.Addresses[group[k]];
        }

        var hashes = StringHashes.Compute(reader, addresses, out var valid, cancellationToken);
        var unreadable = 0;

        // Unreadable strings sort apart: they take no part in a run.
        var keys = new (bool Valid, ulong Hash)[group.Length];
        for (var k = 0; k < group.Length; k++)
        {
            keys[k] = (valid[k], hashes[k]);
            unreadable += valid[k] ? 0 : 1;
        }

        Array.Sort(keys, group);
        for (var start = 0; start < keys.Length;)
        {
            var end = start + 1;
            while (end < keys.Length && keys[end] == keys[start])
            {
                end++;
            }

            if (keys[start].Valid && end - start > 1)
            {
                var copies = end - start;
                rows.Add(new DuplicateRow(group[start], copies, size, (copies - 1) * size, null));
            }

            start = end;
        }

        return unreadable;
    }

    private static string? Shorten(string? text) =>
        text is not null && text.Length > ShownLength ? string.Concat(text.AsSpan(0, ShownLength), "...") : text;

    private sealed record DuplicateRow(int Representative, long Copies, long Size, long Wasted, string? Text);
}
