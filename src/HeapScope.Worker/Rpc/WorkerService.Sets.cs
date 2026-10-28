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


using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using HeapScope.Analysis;
using HeapScope.Analysis.Inspections;
using HeapScope.Analysis.Retention;
using HeapScope.Analysis.Sets;
using HeapScope.Contracts;
using HeapScope.Core;
using HeapScope.Core.Graph;
using HeapScope.Worker.Snapshots;
using Microsoft.Extensions.Logging;
using InspectionColumns = HeapScope.Analysis.Inspections.InspectionColumns;

namespace HeapScope.Worker.Rpc;

/// <summary>Object sets (sprint 3.1, ADR-030): resolving a spec into its members, and a set's dominators.</summary>
internal sealed partial class WorkerService
{
    private readonly ObjectSetCache _sets = CreateSetCache(logger);

    // One resolution per set at a time (the 3.1 review): views asking for the same set together share it.
    private readonly ConcurrentDictionary<(Guid Snapshot, string Key), Lazy<Task<ObjectSet>>> _resolving = new();

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
            var tops = await Task.Run(
                () =>
                {
                    // The walks, the tops' arrays and their sort all run inside the reservation (the 3.1 review).
                    using var reservation = snapshots.Reserve(request.SnapshotId, (long)SetDominators.WorkingBytesPerObject * graph.ObjectCount, phase)
                        ?? throw SnapshotNotLoaded(request.SnapshotId);
                    try
                    {
                        return SetDominators.Top(tree, set, cancellationToken);
                    }
                    finally
                    {
                        MemoryReservations.Collect();
                    }
                },
                cancellationToken).ConfigureAwait(false);
            var source = new SetDominatorsSource(graph, tree, tops);
            long topRetained = 0;
            foreach (var top in tops.Tops)
            {
                topRetained += (long)tree.RetainedSize(top);
            }

            // What the view rests on (2.3.7 review H3): the set, the top members and their bytes, the largest first.
            logger.LogInformation(
                "Set dominators: {Shape} in snapshot {Id}, {Members} members ({Unreachable} unreachable), {Tops} top members retain {Retained} bytes in {Seconds:0.00} s; largest {Largest}",
                request.Set.Shape,
                request.SnapshotId,
                set.Count,
                tops.UnreachableMembers,
                tops.Count,
                topRetained,
                stopwatch.Elapsed.TotalSeconds,
                source.Describe(5));

            // The rows read the graph and the dominators while they live, so they keep a lease of their own (the 3.1 review).
            return new SetDominatorsResult(resultSets.Add(source, snapshot.AcquireLease()), Info(set), tops.Count, topRetained, tops.UnreachableMembers, info);
        });

    private static ObjectSetCache CreateSetCache(ILogger logger)
    {
        var cache = new ObjectSetCache();
        cache.Evicted += what => logger.LogDebug("Object set cache: dropped {What} to stay within its budget", what);
        return cache;
    }

    /// <summary>
    /// The members of <paramref name="spec"/> in a snapshot: from the cache, from a resolution already running, or resolved
    /// now. Every query logs the set it used (the 3.1 review), so a disputed view traces to what the set held. The caller
    /// holds the snapshot's lease.
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
            logger.LogInformation("Object set (cached): {Shape} in snapshot {Id}: {Objects} objects of {Bytes} bytes, {Types} types", spec.Shape, snapshotId, cached.Count, cached.Bytes, cached.TypeCount);
            return cached;
        }

        while (true)
        {
            var shared = _resolving.GetOrAdd((snapshotId, key), _ => new Lazy<Task<ObjectSet>>(() => ResolveNowAsync(snapshotId, snapshot, spec, progress, cancellationToken, depth)));
            try
            {
                return await shared.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // The query that started the shared resolution was canceled, not this one: resolve again.
                _resolving.TryRemove(new KeyValuePair<(Guid, string), Lazy<Task<ObjectSet>>>((snapshotId, key), shared));
            }
            finally
            {
                if (shared.Value.IsCompleted)
                {
                    _resolving.TryRemove(new KeyValuePair<(Guid, string), Lazy<Task<ObjectSet>>>((snapshotId, key), shared));
                }
            }
        }
    }

    private async Task<ObjectSet> ResolveNowAsync(Guid snapshotId, ISnapshot snapshot, ObjectSetSpec spec, IProgress<JobProgress>? progress, CancellationToken cancellationToken, int depth)
    {
        var graph = snapshot.Graph;
        var stopwatch = Stopwatch.StartNew();
        ObjectSet set;
        string detail;
        switch (spec.Kind)
        {
            case ObjectSetKind.Types:
                var keys = spec.Types ?? [];
                if (keys.Count == 0 || keys.Count > ObjectSetLimits.MaxTypes)
                {
                    throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"A set names 1 to {ObjectSetLimits.MaxTypes:N0} types."));
                }

                (set, detail) = await Task.Run(() => OfTypeKeys(snapshotId, graph, keys, cancellationToken), cancellationToken).ConfigureAwait(false);
                break;
            case ObjectSetKind.Namespace:
            case ObjectSetKind.Assembly:
                var grouping = spec.Kind == ObjectSetKind.Namespace ? GroupKey.Namespace : GroupKey.Assembly;
                var groupMask = TypeGroups.MatchGroup(graph, grouping, spec.Name ?? string.Empty, out var matched);
                if (matched == 0)
                {
                    throw new ArgumentException(spec.Kind == ObjectSetKind.Namespace ? "No type of this snapshot is in that namespace." : "No type of this snapshot is in that assembly.");
                }

                set = await Task.Run(() => Reserved(snapshotId, graph, () => ObjectSet.OfTypes(graph, groupMask, cancellationToken)), cancellationToken).ConfigureAwait(false);
                detail = string.Create(CultureInfo.InvariantCulture, $"{matched} type entries");
                break;
            case ObjectSetKind.Objects:
                var addresses = spec.Addresses ?? [];
                if (addresses.Count == 0 || addresses.Count > ObjectSetLimits.MaxAddresses)
                {
                    throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"A set names 1 to {ObjectSetLimits.MaxAddresses:N0} objects."));
                }

                set = Reserved(snapshotId, graph, () => ObjectSet.OfAddresses(graph, addresses));
                detail = string.Create(CultureInfo.InvariantCulture, $"{addresses.Count - set.Count} addresses with no object");
                break;
            case ObjectSetKind.InspectionRow:
                (set, detail) = await ResolveInspectionRowAsync(snapshotId, snapshot, spec, progress, cancellationToken).ConfigureAwait(false);
                break;
            case ObjectSetKind.RetainedBy:
                var innerSpec = spec.Inner ?? throw new ArgumentException("A retained-by set needs its inner set.");
                var inner = await ResolveSetAsync(snapshotId, snapshot, innerSpec, progress, cancellationToken, depth + 1).ConfigureAwait(false);
                set = await RetainedObjectsAsync(snapshotId, graph, inner, progress, cancellationToken).ConfigureAwait(false);
                detail = string.Create(CultureInfo.InvariantCulture, $"the inner set's {inner.Count} objects");
                break;
            default:
                throw new ArgumentException($"Unknown set kind {(int)spec.Kind}.");
        }

        // A snapshot closed meanwhile keeps nothing in the cache.
        if (snapshots.TryGet(snapshotId) is not null)
        {
            _sets.Add(snapshotId, spec.CacheKey, set);
        }

        logger.LogInformation(
            "Object set resolved: {Shape} in snapshot {Id}: {Objects} objects of {Bytes} bytes, {Types} types ({Detail}) in {Seconds:0.000} s",
            spec.Shape,
            snapshotId,
            set.Count,
            set.Bytes,
            set.TypeCount,
            detail,
            stopwatch.Elapsed.TotalSeconds);
        return set;
    }

    /// <summary>
    /// The objects of the type keys, matched in one pass over the type table (the 3.1 review), with how many type entries
    /// each key matched for the log (the keys that matched nothing named, up to 20).
    /// </summary>
    private (ObjectSet Set, string Detail) OfTypeKeys(Guid snapshotId, HeapGraph graph, IReadOnlyList<TypeKeyDto> keys, CancellationToken cancellationToken)
    {
        var wanted = new Dictionary<(string Name, string Module), int>();
        foreach (var key in keys)
        {
            wanted.TryAdd((key.Name, TypeGroups.ModuleKey(key.Module).ToUpperInvariant()), 0);
        }

        var typeMask = new bool[graph.Types.Count];
        var matched = 0;
        for (var t = 0; t < typeMask.Length; t++)
        {
            if ((t & 0xFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var entry = (graph.Types.GetName(t), TypeGroups.ModuleKey(graph.Types.GetModule(t)).ToUpperInvariant());
            if (wanted.TryGetValue(entry, out var count))
            {
                wanted[entry] = count + 1;
                typeMask[t] = true;
                matched++;
            }
        }

        if (matched == 0)
        {
            throw new ArgumentException("None of the set's types is in this snapshot. Names are case sensitive and include the namespace.");
        }

        var missing = wanted.Where(w => w.Value == 0).Select(w => w.Key.Name).ToList();
        var set = Reserved(snapshotId, graph, () => ObjectSet.OfTypes(graph, typeMask, cancellationToken));
        return (set, string.Create(
            CultureInfo.InvariantCulture,
            $"{matched} type entries for {wanted.Count} keys{(missing.Count == 0 ? string.Empty : $"; no match for {missing.Count}: {string.Join(", ", missing.Take(20))}")}"));
    }

    /// <summary>Builds a set inside a reservation of what one set can take (a bit and an index per object; the 3.1 review).</summary>
    private ObjectSet Reserved(Guid snapshotId, HeapGraph graph, Func<ObjectSet> build)
    {
        using var reservation = snapshots.Reserve(snapshotId, ObjectSet.WorkingBytes(graph), "Resolving an object set") ?? throw SnapshotNotLoaded(snapshotId);
        try
        {
            return build();
        }
        finally
        {
            MemoryReservations.Collect();
        }
    }

    /// <summary>
    /// The objects a row of an inspection counted: its own member list (a leak detector), or what its hidden columns open
    /// (a type in a scope, or one object). Returns how the row was resolved, for the log: the source and the row's
    /// metadata cells (type, module, scope), never a value column.
    /// </summary>
    private async Task<(ObjectSet Set, string Detail)> ResolveInspectionRowAsync(Guid snapshotId, ISnapshot snapshot, ObjectSetSpec spec, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        var name = spec.Name ?? throw new ArgumentException("An inspection row set needs the inspection's name.");
        var table = _sets.TryGetTable(snapshotId, name, spec.Baseline);
        var rerun = string.Empty;
        if (table is null)
        {
            // Not kept (a waste inspection, eviction or a worker restart): run it again for its rows only.
            logger.LogInformation("Object set: running {Inspection} on snapshot {Id} again to resolve its row {Row} (its rows were not kept)", name, snapshotId, spec.Row);
            var run = await RunInspectionCoreAsync(new InspectionRequest(snapshotId, name, spec.Baseline), progress, cancellationToken).ConfigureAwait(false);
            run.Lease.Dispose();
            table = run.Result.Rows;
            rerun = "inspection run again; ";
        }

        if (spec.Row < 0 || spec.Row >= table.Count)
        {
            throw new ArgumentException(string.Create(CultureInfo.InvariantCulture, $"The inspection has {table.Count:N0} rows; there is no row {spec.Row}."));
        }

        var graph = snapshot.Graph;
        var version = InspectionCatalog.Find(name)?.Version ?? 0;
        if (table.MembersOf(spec.Row) is { } members)
        {
            var listed = members switch
            {
                int[] array => Reserved(snapshotId, graph, () => ObjectSet.OfIndices(graph, array.AsSpan())),
                _ => Reserved(snapshotId, graph, () => ObjectSet.OfIndices(graph, members)),
            };
            return (listed, string.Create(CultureInfo.InvariantCulture, $"{rerun}rule v{version}; the {members.Count} objects the row counted"));
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
            return address != 0
                ? (ObjectSet.OfAddresses(graph, [address]), $"{rerun}rule v{version}; the row's one object")
                : throw new ArgumentException("That inspection row names no objects.");
        }

        var scope = Enum.TryParse<InstanceScope>(Cell(InspectionColumns.Scope), out var parsed) ? parsed : InstanceScope.All;
        var module = Cell(InspectionColumns.Module);
        snapshot.TryGetService<IObjectReader>(out var reader);
        var objects = await Task.Run(() => InstancesSource.Select(graph, reader, typeName, module, scope, address, cancellationToken), cancellationToken).ConfigureAwait(false);
        return (Reserved(snapshotId, graph, () => ObjectSet.OfIndices(graph, objects.AsSpan())),
            $"{rerun}rule v{version}; type {typeName} ({(string.IsNullOrEmpty(module) ? "no module" : module)}) in scope {scope}");
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

/// <summary>A set's top members (sprint 3.1), most retained first (<see cref="SetDominatorColumns"/>); its rows read the graph under a lease.</summary>
internal sealed class SetDominatorsSource(HeapGraph graph, Dominators dominators, SetDominatorList tops) : IResultSource
{
    private static readonly ResultColumn[] s_columns =
    [
        new(SetDominatorColumns.Address, ResultColumnKind.Address, IsKey: true),
        new(SetDominatorColumns.Type, ResultColumnKind.Text),
        new(SetDominatorColumns.Bytes, ResultColumnKind.Bytes),
        new(SetDominatorColumns.Retained, ResultColumnKind.Bytes),
        new(SetDominatorColumns.Members, ResultColumnKind.Integer),
    ];

    public IReadOnlyList<ResultColumn> Columns => s_columns;

    public int Count => tops.Count;

    public long EstimatedBytes => 8L * tops.Count;

    public void FormatRow(int index, string?[] cells)
    {
        var top = tops.Tops[index];
        cells[0] = string.Create(CultureInfo.InvariantCulture, $"0x{graph.Addresses[top]:x}");
        cells[1] = graph.Types.GetName(graph.TypeIndices[top]);
        cells[2] = graph.GetSize(top).ToString(CultureInfo.InvariantCulture);
        cells[3] = dominators.RetainedSize(top).ToString(CultureInfo.InvariantCulture);
        cells[4] = tops.Below[index].ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The first rows' types and retained bytes, for the log (no addresses).</summary>
    public string Describe(int count) =>
        string.Join("; ", Enumerable.Range(0, Math.Min(count, tops.Count)).Select(i => string.Create(CultureInfo.InvariantCulture, $"{graph.Types.GetName(graph.TypeIndices[tops.Tops[i]])} {dominators.RetainedSize(tops.Tops[i])} bytes, {tops.Below[i]} members below")));
}
