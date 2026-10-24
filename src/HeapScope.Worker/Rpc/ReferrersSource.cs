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
using HeapScope.Core.Graph;

namespace HeapScope.Worker.Rpc;

/// <summary>
/// The objects referencing one object (sprint 2.1), each once, in address order. With an object reader, each referrer's
/// field or array index holding the object is named, for objects with up to <see cref="MaxNamedReferrers"/> referrers.
/// The rows are copied out of the reverse index when the set is created, so pages read only the graph.
/// </summary>
internal sealed class ReferrersSource : IResultSource
{
    /// <summary>Referrers whose fields are named; an object with more gets an explanation instead.</summary>
    public const int MaxNamedReferrers = 1_000;

    /// <summary>Array elements walked per referrer to name its field (a large array may hold the object further on).</summary>
    public const int MaxNamedElements = 100_000;

    /// <summary>
    /// Array elements walked per query (review M5): an object held by many large arrays (a shared string, a singleton)
    /// would otherwise cost 10^8 element reads under the reader's lock. Naming stops when it is spent.
    /// </summary>
    public const long MaxNamedElementsPerQuery = 2_000_000;

    private static readonly ResultColumn[] s_columns =
    [
        new(ReferrerColumns.Field, ResultColumnKind.Text),
        new(ReferrerColumns.Address, ResultColumnKind.Address, IsKey: true),
        new(ReferrerColumns.Type, ResultColumnKind.Text),
        new(ReferrerColumns.Bytes, ResultColumnKind.Bytes),
        new(ReferrerColumns.References, ResultColumnKind.Integer),
    ];

    private readonly HeapGraph _graph;
    private readonly Referrer[] _referrers;
    private readonly string[]? _fields;

    private ReferrersSource(HeapGraph graph, Referrer[] referrers, string[]? fields, string? fieldsNote)
    {
        _graph = graph;
        _referrers = referrers;
        _fields = fields;
        FieldsNote = fieldsNote;
        EstimatedBytes = (8L * referrers.Length) + (fields is null ? 0 : fields.Sum(f => 48L + (2L * f.Length)));
    }

    public IReadOnlyList<ResultColumn> Columns => s_columns;

    public int Count => _referrers.Length;

    public long EstimatedBytes { get; }

    /// <summary>References to the object (a referrer holding it twice counts 2).</summary>
    public long ReferenceCount => _referrers.Sum(r => (long)r.References);

    /// <summary>Why the Field column is empty, when it is.</summary>
    public string? FieldsNote { get; }

    /// <param name="noReaderNote">Why fields cannot be named without a reader (it depends on the snapshot's kind).</param>
    public static ReferrersSource Create(
        HeapGraph graph,
        ReverseReferences reverse,
        IObjectReader? reader,
        int objectIndex,
        string noReaderNote,
        IProgress<JobProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var referrers = IncomingReferences.ForObject(reverse, objectIndex);
        if (referrers.Length == 0)
        {
            return new ReferrersSource(graph, referrers, null, null);
        }

        if (reader is null)
        {
            return new ReferrersSource(graph, referrers, null, noReaderNote);
        }

        if (referrers.Length > MaxNamedReferrers)
        {
            return new ReferrersSource(
                graph,
                referrers,
                null,
                string.Create(CultureInfo.InvariantCulture, $"Fields are named for objects with up to {MaxNamedReferrers:N0} referrers; this one has {referrers.Length:N0}. Open a referrer to see its fields."));
        }

        const string phase = "Naming referrer fields";
        var target = graph.Addresses[objectIndex];
        var fields = new string[referrers.Length];
        var budget = MaxNamedElementsPerQuery;
        string? note = null;
        for (var i = 0; i < referrers.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var walked = Math.Min(MaxNamedElements, ArrayLength(graph, referrers[i].ObjectIndex));
            if (walked > budget)
            {
                Array.Fill(fields, string.Empty, i, fields.Length - i);
                note = string.Create(CultureInfo.InvariantCulture, $"Fields are named for {i:N0} of {referrers.Length:N0} referrers: the others are large arrays, which take long to search. Open a referrer to see its fields.");
                break;
            }

            budget -= walked;
            var paths = reader.ReadReferencePaths(graph.Addresses[referrers[i].ObjectIndex], MaxNamedElements, cancellationToken);
            fields[i] = paths.GetValueOrDefault(target, string.Empty);
            progress?.Report(new JobProgress(phase, i + 1, referrers.Length));
        }

        return new ReferrersSource(graph, referrers, fields, note);
    }

    /// <summary>Elements of an array object (0 for other objects), from its size and its type's component size.</summary>
    private static long ArrayLength(HeapGraph graph, int objectIndex)
    {
        var type = graph.TypeIndices[objectIndex];
        var component = graph.Types.ComponentSizes[type];
        if (!graph.Types.Kinds[type].HasFlag(TypeKind.Array) || component == 0)
        {
            return 0;
        }

        var size = graph.GetSize(objectIndex);
        var header = graph.Types.BaseSizes[type];
        return size > header ? (long)((size - header) / component) : 0;
    }

    public void FormatRow(int index, string?[] cells)
    {
        var referrer = _referrers[index].ObjectIndex;
        cells[0] = _fields?[index] ?? string.Empty;
        cells[1] = string.Create(CultureInfo.InvariantCulture, $"0x{_graph.Addresses[referrer]:x}");
        cells[2] = _graph.Types.GetName(_graph.TypeIndices[referrer]);
        cells[3] = _graph.GetSize(referrer).ToString(CultureInfo.InvariantCulture);
        cells[4] = _referrers[index].References.ToString(CultureInfo.InvariantCulture);
    }
}
