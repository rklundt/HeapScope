using System.Globalization;
using System.Text.RegularExpressions;
using HeapScope.Analysis;
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

    private readonly HeapGraph _graph;
    private readonly IObjectReader? _reader;
    private readonly int[] _objects;
    private readonly string?[]? _values;

    private InstancesSource(HeapGraph graph, IObjectReader? reader, int[] objects, string?[]? values, long totalObjects, long totalBytes, int matchedTypes)
    {
        _graph = graph;
        _reader = reader;
        _objects = objects;
        _values = values;
        TotalObjects = totalObjects;
        TotalBytes = totalBytes;
        MatchedTypes = matchedTypes;
        EstimatedBytes = (4L * objects.Length) + (values is null ? 0 : values.Sum(v => 24L + (2L * (v?.Length ?? 0))));
    }

    public IReadOnlyList<ResultColumn> Columns => s_columns;

    public int Count => _objects.Length;

    public long EstimatedBytes { get; }

    public long TotalObjects { get; }

    public long TotalBytes { get; }

    public int MatchedTypes { get; }

    /// <summary>
    /// Lists the objects of the types keyed by <paramref name="request"/>, filtered and sorted. Memory stays at one
    /// <see cref="int"/> per listed object plus, while sorting, one <see cref="ulong"/> key each; string values are read
    /// and tested one at a time and kept only for a sort by value, which is limited to <see cref="MaxValueSort"/>
    /// strings (sprint 1.5 review H3).
    /// </summary>
    /// <exception cref="ArgumentException">The filter is not a valid regular expression, the sort column is unknown, or a
    /// sort by value covers too many strings.</exception>
    /// <exception cref="CapabilityNotSupportedException">The filter or sort needs string text and there is no reader.</exception>
    public static InstancesSource Create(HeapGraph graph, IObjectReader? reader, InstancesRequest request, CancellationToken cancellationToken, IProgress<JobProgress>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(request);
        var sortColumn = request.SortColumn ?? InstanceColumns.Bytes;
        if (!s_columns.Any(c => c.Name == sortColumn))
        {
            throw new ArgumentException($"Unknown sort column {sortColumn}. Sort by one of: {string.Join(", ", s_columns.Select(c => c.Name))}.");
        }

        var filter = InstanceFilter.Parse(request.Filter);
        var module = request.Module ?? string.Empty;
        var matches = new bool[graph.Types.Count];
        var matchedTypes = 0;
        var strings = false;
        for (var t = 0; t < matches.Length; t++)
        {
            if (graph.Types.GetName(t) == request.TypeName && string.Equals(TypeGroups.ModuleKey(graph.Types.GetModule(t)), module, StringComparison.OrdinalIgnoreCase))
            {
                matches[t] = true;
                matchedTypes++;
                strings |= graph.Types.Kinds[t].HasFlag(TypeKind.String);
            }
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

        // Filter in place: the kept objects move to the front, still in address order.
        var kept = total;
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
                if (filter.IsMatch(graph.Addresses[obj], filter.NeedsValues ? Value(graph, reader, obj) : null))
                {
                    objects[kept++] = obj;
                }
            }

            Array.Resize(ref objects, kept);
        }

        if (sortColumn == InstanceColumns.Value && strings)
        {
            return SortedByText(graph, reader, objects, request.Descending, total, totalBytes, matchedTypes, progress, cancellationToken);
        }

        SortByKey(graph, objects, sortColumn, request.Descending, cancellationToken);
        return new InstancesSource(graph, reader, objects, null, total, totalBytes, matchedTypes);
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
        IProgress<JobProgress>? progress,
        CancellationToken cancellationToken)
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

        return new InstancesSource(graph, reader, sortedObjects, sortedValues, total, totalBytes, matchedTypes);
    }

    /// <summary>
    /// Sorts objects (in address order) by a 32-bit key in the high half of a <see cref="ulong"/> and their position in
    /// the low half, so ties keep address order and one primitive sort does it all. Arrays sort by value numerically,
    /// by their length.
    /// </summary>
    private static void SortByKey(HeapGraph graph, int[] objects, string sortColumn, bool descending, CancellationToken cancellationToken)
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
                InstanceColumns.Type => (uint)graph.TypeIndices[obj],
                InstanceColumns.Generation => (uint)graph.HeapKinds[obj],
                InstanceColumns.Value => (uint)Math.Min(ArrayLength(graph, obj) ?? 0, uint.MaxValue),
                _ => (uint)Math.Min(graph.GetSize(obj), uint.MaxValue),
            };
            keys[k] = ((ulong)(descending ? uint.MaxValue - primary : primary) << 32) | (uint)k;
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
                    throw new ArgumentException($"{text} is not a hexadecimal address.");
                }

                return new InstanceFilter(hex.ToLowerInvariant(), null, null);
            }

            if (text.Length > 1 && text.StartsWith('/') && text.EndsWith('/'))
            {
                try
                {
                    return new InstanceFilter(null, new Regex(text[1..^1], RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)), null);
                }
                catch (ArgumentException ex)
                {
                    throw new ArgumentException($"{text} is not a valid regular expression: {ex.Message}", ex);
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
                    return false;
                }
            }

            return _text is null || (value?.Contains(_text, StringComparison.OrdinalIgnoreCase) ?? false);
        }
    }
}
