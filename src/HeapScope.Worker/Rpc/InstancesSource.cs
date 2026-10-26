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
using System.Text.RegularExpressions;
using HeapScope.Analysis;
using HeapScope.Analysis.Retention;
using HeapScope.Contracts;
using HeapScope.Core;
using HeapScope.Core.Graph;

namespace HeapScope.Worker.Rpc;

/// <summary>
/// The objects of a type (sprint 1.5): object indices only, formatted per page. String values are read from the dump
/// for the page asked for; a value filter reads and tests them one at a time, and only a sort by value keeps them
/// (shortened to <see cref="ValueLength"/> characters, at most <see cref="MaxValueSort"/>). The source holds a snapshot
/// lease (the result set's owner) because it reads the graph and the dump while paging.
/// </summary>
internal sealed class InstancesSource : IResultSource
{
    /// <summary>Characters of a string shown (and matched) as its value.</summary>
    public const int ValueLength = 100;

    private static readonly ResultColumn[] s_columns =
    [
        new(InstanceColumns.Address, ResultColumnKind.Address, IsKey: true),
        new(InstanceColumns.Type, ResultColumnKind.Text),
        new(InstanceColumns.Bytes, ResultColumnKind.Bytes),
        new(InstanceColumns.Generation, ResultColumnKind.Text),
        new(InstanceColumns.Value, ResultColumnKind.Text),
    ];

    private static readonly ResultColumn[] s_columnsWithRetained = [.. s_columns, new(InstanceColumns.Retained, ResultColumnKind.Bytes)];

    /// <summary>The largest sort key: 33 bits, so retained sizes up to 8 GB sort exactly and a position fits beside them.</summary>
    private const ulong MaxSortKey = (1UL << 33) - 1;

    private readonly HeapGraph _graph;
    private readonly IObjectReader? _reader;
    private readonly int[] _objects;
    private readonly string?[]? _values;
    private readonly Dominators? _dominators;

    private InstancesSource(HeapGraph graph, IObjectReader? reader, int[] objects, string?[]? values, long totalObjects, long totalBytes, int matchedTypes, Dominators? dominators, string? note = null)
    {
        Note = note;
        _graph = graph;
        _dominators = dominators;
        _reader = reader;
        _objects = objects;
        _values = values;
        TotalObjects = totalObjects;
        TotalBytes = totalBytes;
        MatchedTypes = matchedTypes;
        EstimatedBytes = (4L * objects.Length) + (values is null ? 0 : values.Sum(v => 24L + (2L * (v?.Length ?? 0))));
    }

    public IReadOnlyList<ResultColumn> Columns => _dominators is null ? s_columns : s_columnsWithRetained;

    public int Count => _objects.Length;

    public long EstimatedBytes { get; }

    public long TotalObjects { get; }

    public long TotalBytes { get; }

    public int MatchedTypes { get; }

    /// <summary>
    /// What the filter could not test (sprint 2.3.7 review M10): strings that could not be read and regular expression
    /// matches that timed out are left out; null when nothing was.
    /// </summary>
    public string? Note { get; }

    /// <summary>
    /// Lists the objects of the types keyed by <paramref name="request"/>, filtered and sorted. Memory stays at one
    /// <see cref="int"/> per listed object plus, while sorting, one <see cref="ulong"/> key each; string values are read
    /// and tested one at a time and kept only for a sort by value, which is limited to <see cref="MaxValueSort"/>
    /// strings (sprint 1.5 review H3).
    /// </summary>
    /// <exception cref="ArgumentException">The filter is not a valid regular expression, the sort column is unknown, or a
    /// sort by value covers too many strings.</exception>
    /// <exception cref="CapabilityNotSupportedException">The filter or sort needs string text and there is no reader.</exception>
    /// <summary>
    /// Checks <paramref name="request"/>'s sort column and filter, and whether any type matches, before the worker computes
    /// the dominators for it (sprint 2.3 review M7: a typo must not cost seconds of dominators). Returns false when no
    /// type matches, so the retained column is not worth computing.
    /// </summary>
    /// <exception cref="ArgumentException">The sort column is unknown or the filter is not a valid regular expression.</exception>
    public static bool Validate(HeapGraph graph, InstancesRequest request)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(request);
        var sortColumn = request.SortColumn ?? InstanceColumns.Bytes;
        var columns = request.IncludeRetained ? s_columnsWithRetained : s_columns;
        if (!columns.Any(c => c.Name == sortColumn))
        {
            throw new ArgumentException($"Unknown sort column {sortColumn}. Sort by one of: {string.Join(", ", columns.Select(c => c.Name))}.");
        }

        InstanceFilter.Parse(request.Filter);
        TypeGroups.MatchTypes(graph, request.TypeName, request.Module, out var matchedTypes);
        return matchedTypes > 0;
    }

    /// <param name="dominators">The snapshot's dominators, for the retained bytes column (sprint 2.3); null without it.</param>
    public static InstancesSource Create(HeapGraph graph, IObjectReader? reader, InstancesRequest request, CancellationToken cancellationToken, IProgress<JobProgress>? progress = null, Dominators? dominators = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(request);
        var sortColumn = request.SortColumn ?? InstanceColumns.Bytes;
        var columns = dominators is null ? s_columns : s_columnsWithRetained;
        if (!columns.Any(c => c.Name == sortColumn))
        {
            throw new ArgumentException($"Unknown sort column {sortColumn}. Sort by one of: {string.Join(", ", columns.Select(c => c.Name))}.");
        }

        var filter = InstanceFilter.Parse(request.Filter);
        var matches = TypeGroups.MatchTypes(graph, request.TypeName, request.Module, out var matchedTypes);
        var strings = false;
        for (var t = 0; t < matches.Length; t++)
        {
            strings |= matches[t] && graph.Types.Kinds[t].HasFlag(TypeKind.String);
        }

        // Without string text a value filter or sort on strings would silently match or order nothing.
        if (strings && reader is null && (filter.NeedsValues || sortColumn == InstanceColumns.Value))
        {
            throw new CapabilityNotSupportedException(SnapshotCapabilities.StringContents);
        }

        // Count first, then fill an array of exactly that size: no doubling copies of per-object data.
        var types = graph.TypeIndices.Span;
        var total = 0;
        for (var i = 0; i < types.Length; i++)
        {
            if ((i & 0xFFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (matches[types[i]])
            {
                total++;
            }
        }

        var objects = new int[total];
        long totalBytes = 0;
        var filled = 0;
        for (var i = 0; i < types.Length && filled < total; i++)
        {
            if (matches[types[i]])
            {
                objects[filled++] = i;
                totalBytes += (long)graph.GetSize(i);
            }
        }

        // A scope (sprint 1.6, what an inspection row opens) narrows the set first; totals then describe the scope.
        if (request.Scope != InstanceScope.All)
        {
            objects = Scoped(graph, reader, objects, request, cancellationToken);
            total = objects.Length;
            totalBytes = 0;
            foreach (var obj in objects)
            {
                totalBytes += (long)graph.GetSize(obj);
            }
        }

        // Filter in place: the kept objects move to the front, still in address order. Strings that cannot be read are
        // counted, so a filter that silently missed them says so (sprint 2.3.7 review M10).
        var kept = total;
        var unreadable = 0;
        if (filter.IsActive)
        {
            kept = 0;
            for (var k = 0; k < total; k++)
            {
                if ((k & 0xFFF) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    progress?.Report(new JobProgress(filter.NeedsValues ? "Reading values" : "Filtering", k, total));
                }

                var obj = objects[k];
                var value = filter.NeedsValues ? Value(graph, reader, obj) : null;
                if (filter.NeedsValues && value is null && graph.Types.Kinds[graph.TypeIndices[obj]].HasFlag(TypeKind.String))
                {
                    unreadable++;
                }

                if (filter.IsMatch(graph.Addresses[obj], value))
                {
                    objects[kept++] = obj;
                }
            }

            Array.Resize(ref objects, kept);
        }

        var note = (unreadable, filter.Timeouts) switch
        {
            (0, 0) => null,
            (_, 0) => string.Create(CultureInfo.InvariantCulture, $"{unreadable:N0} strings could not be read from the dump, so the filter could not test them; they are left out."),
            (0, _) => string.Create(CultureInfo.InvariantCulture, $"The pattern took too long on {filter.Timeouts:N0} values (over 100 ms each); they are left out."),
            _ => string.Create(CultureInfo.InvariantCulture, $"{unreadable:N0} strings could not be read and the pattern took too long on {filter.Timeouts:N0} values; both are left out."),
        };

        if (sortColumn == InstanceColumns.Value && strings)
        {
            return SortedByText(graph, reader, objects, request.Descending, total, totalBytes, matchedTypes, dominators, progress, cancellationToken, note);
        }

        SortByKey(graph, dominators, objects, sortColumn, request.Descending, cancellationToken);
        return new InstancesSource(graph, reader, objects, null, total, totalBytes, matchedTypes, dominators, note);
    }

    /// <summary>The objects of the scope: on the large object heap, still to be finalized, or strings equal to one string.</summary>
    private static int[] Scoped(HeapGraph graph, IObjectReader? reader, int[] objects, InstancesRequest request, CancellationToken cancellationToken)
    {
        switch (request.Scope)
        {
            case InstanceScope.LargeObjectHeap:
                return [.. objects.Where(o => graph.HeapKinds[o] == HeapKind.LargeObjectHeap)];
            case InstanceScope.Finalizable:
                var ready = HeapScope.Analysis.Inspections.Finalization.ReadyObjects(graph);
                return [.. objects.Where(o => graph.Flags[o].HasFlag(ObjectFlags.HasFinalizer) || ready.Contains(o))];
            case InstanceScope.SameValue:
                if (reader is null)
                {
                    throw new CapabilityNotSupportedException(SnapshotCapabilities.StringContents);
                }

                var modelHash = HeapScope.Analysis.Inspections.StringHashes.Compute(reader, [request.SameValueAs], out var modelValid, cancellationToken);
                if (!graph.TryGetIndex(request.SameValueAs, out var model) || !modelValid[0])
                {
                    throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"There is no string at 0x{request.SameValueAs:x} in this snapshot."));
                }

                // Equal text means equal size: only strings of the model's size are read, and compared by the same
                // streamed hash duplicate strings groups by (no text is built, review H2).
                var size = graph.GetSize(model);
                var candidates = objects.Where(o => graph.GetSize(o) == size).ToArray();
                var hashes = HeapScope.Analysis.Inspections.StringHashes.Compute(reader, [.. candidates.Select(o => graph.Addresses[o])], out var valid, cancellationToken);
                return [.. candidates.Where((_, k) => valid[k] && hashes[k] == modelHash[0])];
            default:
                throw new ArgumentException($"Unknown instance scope {request.Scope}.");
        }
    }

    /// <summary>Strings a sort by value may hold (their first <see cref="ValueLength"/> characters each).</summary>
    public const int MaxValueSort = 250_000;

    private static InstancesSource SortedByText(
        HeapGraph graph,
        IObjectReader? reader,
        int[] objects,
        bool descending,
        long total,
        long totalBytes,
        int matchedTypes,
        Dominators? dominators,
        IProgress<JobProgress>? progress,
        CancellationToken cancellationToken,
        string? note)
    {
        var count = objects.Length;
        if (count > MaxValueSort)
        {
            throw new ArgumentException(string.Create(CultureInfo.InvariantCulture,
                $"Sorting {count:N0} strings by value would hold all their text; filter them to at most {MaxValueSort:N0} first, or sort by another column."));
        }

        var values = new string?[count];
        var positions = new int[count];
        for (var k = 0; k < count; k++)
        {
            if ((k & 0xFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new JobProgress("Reading values", k, count));
            }

            values[k] = Value(graph, reader, objects[k]);
            positions[k] = k;
        }

        // Ties by address (the current order) keep paging stable between queries.
        Array.Sort(positions, (a, b) =>
        {
            var primary = string.CompareOrdinal(values[a], values[b]);
            return primary != 0 ? (descending ? -primary : primary) : a.CompareTo(b);
        });
        var sortedObjects = new int[count];
        var sortedValues = new string?[count];
        for (var k = 0; k < count; k++)
        {
            sortedObjects[k] = objects[positions[k]];
            sortedValues[k] = values[positions[k]];
        }

        return new InstancesSource(graph, reader, sortedObjects, sortedValues, total, totalBytes, matchedTypes, dominators, note);
    }

    /// <summary>
    /// Sorts objects (in address order) by a 33-bit key in the high bits of a <see cref="ulong"/> and their position in
    /// the low 31, so ties keep address order and one primitive sort does it all. Arrays sort by value numerically,
    /// by their length; retained sizes above 8 GB tie at the top.
    /// </summary>
    private static void SortByKey(HeapGraph graph, Dominators? dominators, int[] objects, string sortColumn, bool descending, CancellationToken cancellationToken)
    {
        if (sortColumn == InstanceColumns.Address)
        {
            if (descending)
            {
                Array.Reverse(objects);
            }

            return;
        }

        var keys = new ulong[objects.Length];
        for (var k = 0; k < objects.Length; k++)
        {
            if ((k & 0xFFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var obj = objects[k];
            var primary = sortColumn switch
            {
                InstanceColumns.Type => (ulong)graph.TypeIndices[obj],
                InstanceColumns.Generation => (ulong)graph.HeapKinds[obj],
                InstanceColumns.Value => (ulong)Math.Min(ArrayLength(graph, obj) ?? 0, uint.MaxValue),
                InstanceColumns.Retained => Math.Min(dominators!.RetainedSize(obj), MaxSortKey),
                _ => Math.Min(graph.GetSize(obj), MaxSortKey),
            };
            keys[k] = ((descending ? MaxSortKey - primary : primary) << 31) | (uint)k;
        }

        Array.Sort(keys, objects);
    }

    public void FormatRow(int index, string?[] cells)
    {
        var obj = _objects[index];
        cells[0] = string.Create(CultureInfo.InvariantCulture, $"0x{_graph.Addresses[obj]:x}");
        cells[1] = _graph.Types.GetName(_graph.TypeIndices[obj]);
        cells[2] = _graph.GetSize(obj).ToString(CultureInfo.InvariantCulture);
        cells[3] = _graph.HeapKinds[obj].ToString();
        cells[4] = _values is not null ? _values[index] : Value(_graph, _reader, obj);
        if (_dominators is not null)
        {
            cells[5] = _dominators.IsReachable(obj) ? _dominators.RetainedSize(obj).ToString(CultureInfo.InvariantCulture) : null;
        }
    }

    /// <summary>A string's text (shortened), or an array's length, or nothing. An unreadable string is nothing.</summary>
    private static string? Value(HeapGraph graph, IObjectReader? reader, int obj)
    {
        var type = graph.TypeIndices[obj];
        if (graph.Types.Kinds[type].HasFlag(TypeKind.String))
        {
            string? text;
            try
            {
                text = reader?.ReadString(graph.Addresses[obj], ValueLength + 1);
            }
            catch (ArgumentException)
            {
                // Not a valid object in the dump (a partial or damaged dump): one bad string must not fail the list.
                return null;
            }

            return text is not null && text.Length > ValueLength ? string.Concat(text.AsSpan(0, ValueLength), "...") : text;
        }

        return ArrayLength(graph, obj) is { } length ? string.Create(CultureInfo.InvariantCulture, $"[{length:N0}]") : null;
    }

    /// <summary>An array's element count from its size in the graph; null for anything else.</summary>
    private static ulong? ArrayLength(HeapGraph graph, int obj)
    {
        var type = graph.TypeIndices[obj];
        var component = graph.Types.ComponentSizes[type];
        return graph.Types.Kinds[type].HasFlag(TypeKind.Array) && component > 0
            ? (graph.GetSize(obj) - graph.Types.BaseSizes[type]) / component
            : null;
    }

    /// <summary>
    /// The instances filter: <c>0x...</c> is an address prefix (hexadecimal, no leading zeros), <c>/pattern/</c> a regular
    /// expression on the value (case-insensitive, 100 ms per match), anything else a case-insensitive substring of the value.
    /// </summary>
    private sealed class InstanceFilter
    {
        private readonly string? _addressPrefix;
        private readonly Regex? _regex;
        private readonly string? _text;

        private InstanceFilter(string? addressPrefix, Regex? regex, string? text)
        {
            _addressPrefix = addressPrefix;
            _regex = regex;
            _text = text;
        }

        public bool NeedsValues => _regex is not null || _text is not null;

        /// <summary>Values the pattern timed out on (left out).</summary>
        public int Timeouts { get; private set; }

        public bool IsActive => NeedsValues || _addressPrefix is not null;

        public static InstanceFilter Parse(string? filter)
        {
            var text = filter?.Trim();
            if (string.IsNullOrEmpty(text))
            {
                return new InstanceFilter(null, null, null);
            }

            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                var hex = text[2..].TrimStart('0');
                if (!hex.All(Uri.IsHexDigit))
                {
                    // The filter's text stays out of the message: the worker logs refusals, and a filter may be a value
                    // copied from the dump (sprint 2.3.7 review M6).
                    throw new ArgumentException("The filter starts with 0x but is not a hexadecimal address.");
                }

                return new InstanceFilter(hex.ToLowerInvariant(), null, null);
            }

            if (text.Length > 1 && text.StartsWith('/') && text.EndsWith('/'))
            {
                try
                {
                    return new InstanceFilter(null, new Regex(text[1..^1], RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)), null);
                }
                catch (RegexParseException ex)
                {
                    throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"The filter is not a valid regular expression: {ex.Error} at position {ex.Offset}."));
                }
            }

            return new InstanceFilter(null, null, text);
        }

        public bool IsMatch(ulong address, string? value)
        {
            if (_addressPrefix is not null)
            {
                return address.ToString("x", CultureInfo.InvariantCulture).StartsWith(_addressPrefix, StringComparison.Ordinal);
            }

            if (_regex is not null)
            {
                try
                {
                    return value is not null && _regex.IsMatch(value);
                }
                catch (RegexMatchTimeoutException)
                {
                    Timeouts++;
                    return false;
                }
            }

            return _text is null || (value?.Contains(_text, StringComparison.OrdinalIgnoreCase) ?? false);
        }
    }
}
