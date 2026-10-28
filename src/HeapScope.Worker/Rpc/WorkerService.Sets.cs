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

using System.Diagnostics;
using System.Globalization;
using HeapScope.Analysis;
using HeapScope.Analysis.Inspections;
using InspectionColumns = HeapScope.Analysis.Inspections.InspectionColumns;
using HeapScope.Analysis.Retention;
using HeapScope.Analysis.Sets;
using HeapScope.Contracts;
using HeapScope.Core;
using HeapScope.Core.Graph;
using HeapScope.Worker.Snapshots;
using Microsoft.Extensions.Logging;

namespace HeapScope.Worker.Rpc;

/// <summary>Object sets (sprint 3.1, ADR-030): resolving a spec into its members, and a set's dominators.</summary>
internal sealed partial class WorkerService
{
    private readonly ObjectSetCache _sets = new();

    public Task<SetDominatorsResult> QuerySetDominatorsAsync(SetDominatorsRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken) =>
        GuardAsync(request, async () =>
        {
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(request.Set);
            var (snapshot, lease) = Lease(request.SnapshotId);
            using var _ = lease;
            var graph = snapshot.Graph;
            var set = await ResolveSetAsync(request.SnapshotId, snapshot, request.Set, progress, cancellationToken).ConfigureAwait(false);
            RequireMembers(set);
            var (dominators, info) = await GetDominatorsAsync(request.SnapshotId, progress, cancellationToken).ConfigureAwait(false);
            var tree = dominators.Dominators;
            var stopwatch = Stopwatch.StartNew();
            const string phase = "Finding the set's dominators";
            progress?.Report(new JobProgress(phase, 0, null));
            var (tops, unreachable) = await Task.Run(
                () =>
                {
                    using var reservation = snapshots.Reserve(request.SnapshotId, (long)SetDominators.WorkingBytesPerObject * graph.ObjectCount, phase)
                        ?? throw SnapshotNotLoaded(request.SnapshotId);
                    try
                    {
                        var found = SetDominators.Top(tree, set, out var none, cancellationToken);
                        return (found, none);
                    }
                    finally
                    {
                        MemoryReservations.Collect();
                    }
                },
                cancellationToken).ConfigureAwait(false);
            var source = new SetDominatorsSource(graph, tree, tops);
            var topRetained = tops.Sum(t => (long)tree.RetainedSize(t.ObjectIndex));

            // What the view rests on (2.3.7 review H3): the set, the top members and their bytes, the largest first.
            logger.LogInformation(
                "Set dominators: {Shape} in snapshot {Id}, {Members} members ({Unreachable} unreachable), {Tops} top members retain {Retained} bytes in {Seconds:0.00} s; largest {Largest}",
                request.Set.Shape,
                request.SnapshotId,
                set.Count,
                unreachable,
                tops.Count,
                topRetained,
                stopwatch.Elapsed.TotalSeconds,
                source.Describe(5));
            return new SetDominatorsResult(resultSets.Add(source), Info(set), tops.Count, topRetained, unreachable, info);
        });

    /// <summary>
    /// The members of <paramref name="spec"/> in a snapshot, from the cache or resolved now (and logged with their size, so
    /// a disputed view traces to what the set held). The caller holds the snapshot's lease.
    /// </summary>
    private async Task<ObjectSet> ResolveSetAsync(Guid snapshotId, ISnapshot snapshot, ObjectSetSpec spec, IProgress<JobProgress>? progress, CancellationToken cancellationToken, int depth = 0)
    {
        ArgumentNullException.ThrowIfNull(spec);
        if (depth > ObjectSetLimits.MaxDepth)
        {
            throw new ArgumentException("The set nests too deeply.");
        }

        var key = spec.CacheKey;
        if (_sets.TryGet(snapshotId, key) is { } cached)
        {
            return cached;
        }

        var graph = snapshot.Graph;
        var stopwatch = Stopwatch.StartNew();
        ObjectSet set;
        int matched;
        switch (spec.Kind)
        {
            case ObjectSetKind.Types:
                var keys = spec.Types ?? [];
                if (keys.Count == 0 || keys.Count > ObjectSetLimits.MaxTypes)
                {
                    throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"A set names 1 to {ObjectSetLimits.MaxTypes:N0} types."));
                }

                var typeMask = new bool[graph.Types.Count];
                matched = 0;
                foreach (var type in keys)
                {
                    var matches = TypeGroups.MatchTypes(graph, type.Name, type.Module, out var count);
                    matched += count;
                    for (var t = 0; t < matches.Length; t++)
                    {
                        typeMask[t] |= matches[t];
                    }
                }

                if (matched == 0)
                {
                    throw new ArgumentException("None of the set's types is in this snapshot. Names are case sensitive and include the namespace.");
                }

                set = await Task.Run(() => ObjectSet.OfTypes(graph, typeMask, cancellationToken), cancellationToken).ConfigureAwait(false);
                break;
            case ObjectSetKind.Namespace:
            case ObjectSetKind.Assembly:
                var grouping = spec.Kind == ObjectSetKind.Namespace ? GroupKey.Namespace : GroupKey.Assembly;
                var groupMask = TypeGroups.MatchGroup(graph, grouping, spec.Name ?? string.Empty, out matched);
                if (matched == 0)
                {
                    throw new ArgumentException(spec.Kind == ObjectSetKind.Namespace ? "No type of this snapshot is in that namespace." : "No type of this snapshot is in that assembly.");
                }

                set = await Task.Run(() => ObjectSet.OfTypes(graph, groupMask, cancellationToken), cancellationToken).ConfigureAwait(false);
                break;
            case ObjectSetKind.Objects:
                var addresses = spec.Addresses ?? [];
                if (addresses.Count == 0 || addresses.Count > ObjectSetLimits.MaxAddresses)
                {
                    throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"A set names 1 to {ObjectSetLimits.MaxAddresses:N0} objects."));
                }

                set = ObjectSet.OfAddresses(graph, addresses);
                break;
            case ObjectSetKind.InspectionRow:
                set = await ResolveInspectionRowAsync(snapshotId, snapshot, spec, progress, cancellationToken).ConfigureAwait(false);
                break;
            case ObjectSetKind.RetainedBy:
                var innerSpec = spec.Inner ?? throw new ArgumentException("A retained-by set needs its inner set.");
                var inner = await ResolveSetAsync(snapshotId, snapshot, innerSpec, progress, cancellationToken, depth + 1).ConfigureAwait(false);
                set = await RetainedObjectsAsync(snapshotId, graph, inner, progress, cancellationToken).ConfigureAwait(false);
                break;
            default:
                throw new ArgumentException($"Unknown set kind {(int)spec.Kind}.");
        }

        _sets.Add(snapshotId, key, set);
        logger.LogInformation(
            "Object set resolved: {Shape} in snapshot {Id}: {Objects} objects of {Bytes} bytes, {Types} types, in {Seconds:0.000} s",
            spec.Shape,
            snapshotId,
            set.Count,
            set.Bytes,
            set.TypeCount,
            stopwatch.Elapsed.TotalSeconds);
        return set;
    }

    /// <summary>The objects a row of an inspection counted: its own member list, or what its hidden columns open.</summary>
    private async Task<ObjectSet> ResolveInspectionRowAsync(Guid snapshotId, ISnapshot snapshot, ObjectSetSpec spec, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        var name = spec.Name ?? throw new ArgumentException("An inspection row set needs the inspection's name.");
        var table = _sets.TryGetTable(snapshotId, name, spec.Baseline);
        if (table is null)
        {
            // Run again (the table was dropped, or the worker restarted); its result set is not needed.
            var answer = await RunInspectionAsync(new InspectionRequest(snapshotId, name, spec.Baseline), progress, cancellationToken).ConfigureAwait(false);
            resultSets.Release(answer.Rows.Id);
            table = _sets.TryGetTable(snapshotId, name, spec.Baseline) ?? throw new InvalidOperationException("The inspection's rows were not kept.");
        }

        if (spec.Row < 0 || spec.Row >= table.Count)
        {
            throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"The inspection has {table.Count:N0} rows; there is no row {spec.Row}."));
        }

        var graph = snapshot.Graph;
        if (table.MembersOf(spec.Row) is { } members)
        {
            return ObjectSet.OfIndices(graph, members);
        }

        var cells = new string?[table.Columns.Count];
        table.FormatRow(spec.Row, cells);
        string? Cell(string column)
        {
            for (var i = 0; i < table.Columns.Count; i++)
            {
                if (table.Columns[i].Name == column)
                {
                    return cells[i];
                }
            }

            return null;
        }

        var address = ParseAddress(Cell(InspectionColumns.Address));
        var typeName = Cell(InspectionColumns.TypeName);
        if (string.IsNullOrEmpty(typeName))
        {
            return address != 0 ? ObjectSet.OfAddresses(graph, [address]) : throw new ArgumentException("That inspection row names no objects.");
        }

        var scope = Enum.TryParse<InstanceScope>(Cell(InspectionColumns.Scope), out var parsed) ? parsed : InstanceScope.All;
        var module = Cell(InspectionColumns.Module);
        snapshot.TryGetService<IObjectReader>(out var reader);
        var objects = await Task.Run(() => InstancesSource.Select(graph, reader, typeName, module, scope, address, cancellationToken), cancellationToken).ConfigureAwait(false);
        return ObjectSet.OfIndices(graph, objects);
    }

    /// <summary>What <paramref name="inner"/> retains, as a set (the retained objects, its reachable members included).</summary>
    private async Task<ObjectSet> RetainedObjectsAsync(Guid snapshotId, HeapGraph graph, ObjectSet inner, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        RequireMembers(inner);
        var (dominators, _) = await GetDominatorsAsync(snapshotId, progress, cancellationToken).ConfigureAwait(false);
        const string phase = "Finding what the set retains";
        return await Task.Run(
            () =>
            {
                using var reservation = snapshots.Reserve(snapshotId, ((long)SetRetention.WorkingBytesPerObject * graph.ObjectCount) + ObjectSet.WorkingBytes(graph), phase)
                    ?? throw SnapshotNotLoaded(snapshotId);
                try
                {
                    return SetRetention.Compute(graph, dominators.Dominators, inner, collectRetained: true, cancellationToken: cancellationToken).Retained!;
                }
                finally
                {
                    MemoryReservations.Collect();
                }
            },
            cancellationToken).ConfigureAwait(false);
    }

    private static ulong ParseAddress(string? text) =>
        text is { Length: > 2 } && text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && ulong.TryParse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var address) ? address : 0;

    private static void RequireMembers(ObjectSet set)
    {
        if (set.Count == 0)
        {
            throw new ArgumentException("The set has no objects in this snapshot.");
        }
    }

    private static ObjectSetInfo Info(ObjectSet set) => new(set.Count, (long)set.Bytes, set.TypeCount);
}

/// <summary>A set's top members (sprint 3.1), most retained first (<see cref="SetDominatorColumns"/>).</summary>
internal sealed class SetDominatorsSource : IResultSource
{
    private static readonly ResultColumn[] s_columns =
    [
        new(SetDominatorColumns.Address, ResultColumnKind.Address, IsKey: true),
        new(SetDominatorColumns.Type, ResultColumnKind.Text),
        new(SetDominatorColumns.Bytes, ResultColumnKind.Bytes),
        new(SetDominatorColumns.Retained, ResultColumnKind.Bytes),
        new(SetDominatorColumns.Members, ResultColumnKind.Integer),
    ];

    private readonly HeapGraph _graph;
    private readonly Dominators _dominators;
    private readonly SetDominator[] _rows;

    public SetDominatorsSource(HeapGraph graph, Dominators dominators, IReadOnlyList<SetDominator> tops)
    {
        _graph = graph;
        _dominators = dominators;
        _rows = [.. tops.OrderByDescending(t => dominators.RetainedSize(t.ObjectIndex)).ThenBy(t => t.ObjectIndex)];
    }

    public IReadOnlyList<ResultColumn> Columns => s_columns;

    public int Count => _rows.Length;

    public long EstimatedBytes => 8L * _rows.Length;

    public void FormatRow(int index, string?[] cells)
    {
        var row = _rows[index];
        cells[0] = string.Create(CultureInfo.InvariantCulture, $"0x{_graph.Addresses[row.ObjectIndex]:x}");
        cells[1] = _graph.Types.GetName(_graph.TypeIndices[row.ObjectIndex]);
        cells[2] = _graph.GetSize(row.ObjectIndex).ToString(CultureInfo.InvariantCulture);
        cells[3] = _dominators.RetainedSize(row.ObjectIndex).ToString(CultureInfo.InvariantCulture);
        cells[4] = row.MembersBelow.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The first rows' types and retained bytes, for the log (no addresses).</summary>
    public string Describe(int count) =>
        string.Join("; ", _rows.Take(count).Select(r => string.Create(CultureInfo.InvariantCulture, $"{_graph.Types.GetName(_graph.TypeIndices[r.ObjectIndex])} {_dominators.RetainedSize(r.ObjectIndex)} bytes, {r.MembersBelow} members below")));
}
