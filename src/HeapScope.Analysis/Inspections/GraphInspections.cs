using System.Globalization;
using HeapScope.Core;
using HeapScope.Core.Graph;

namespace HeapScope.Analysis.Inspections;

/// <summary>Helpers shared by the inspections: per-type totals, cell text and progress.</summary>
internal static class InspectionRows
{
    /// <summary>
    /// Objects per type key (name and module file name, the key that opens a type's objects), largest first. Totals are
    /// kept per type index in arrays while walking the objects and merged by key once at the end.
    /// </summary>
    public static TypeRow[] ByType(HeapGraph graph, Func<int, bool> include, string phase, InspectionContext context, out long count, out long bytes)
    {
        var objectsByType = new long[graph.Types.Count];
        var bytesByType = new long[graph.Types.Count];
        var types = graph.TypeIndices.Span;
        count = 0;
        bytes = 0;
        for (var i = 0; i < types.Length; i++)
        {
            if ((i & 0xFFFF) == 0)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                context.Report(phase, i, types.Length);
            }

            if (!include(i))
            {
                continue;
            }

            var size = (long)graph.GetSize(i);
            objectsByType[types[i]]++;
            bytesByType[types[i]] += size;
            count++;
            bytes += size;
        }

        var totals = new Dictionary<(string Name, string Module), (long Objects, long Bytes)>();
        for (var t = 0; t < objectsByType.Length; t++)
        {
            if (objectsByType[t] > 0)
            {
                var key = (graph.Types.GetName(t), TypeGroups.ModuleKey(graph.Types.GetModule(t)));
                var (objects, total) = totals.GetValueOrDefault(key);
                totals[key] = (objects + objectsByType[t], total + bytesByType[t]);
            }
        }

        return [.. totals.Select(t => new TypeRow(t.Key.Name, t.Key.Module, t.Value.Objects, t.Value.Bytes)).OrderByDescending(r => r.Bytes).ThenBy(r => r.Name, StringComparer.Ordinal)];
    }

    public static readonly InspectionColumn[] TypeColumns =
    [
        new(InspectionColumns.Type, InspectionColumnKind.Text, IsKey: true),
        new(InspectionColumns.Objects, InspectionColumnKind.Integer),
        new(InspectionColumns.Bytes, InspectionColumnKind.Bytes),
        new(InspectionColumns.TypeName, InspectionColumnKind.Text),
        new(InspectionColumns.Module, InspectionColumnKind.Text),
        new(InspectionColumns.Scope, InspectionColumnKind.Text),
    ];

    public static RowTable<TypeRow> TypeTable(TypeRow[] rows, string scope) =>
        new(TypeColumns, rows, 96, (row, cells) =>
        {
            cells[0] = row.Name;
            cells[1] = Number(row.Objects);
            cells[2] = Number(row.Bytes);
            cells[3] = row.Name;
            cells[4] = row.Module;
            cells[5] = scope;
        });

    public static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    public static string Hex(ulong address) => string.Create(CultureInfo.InvariantCulture, $"0x{address:x}");

    /// <summary>An array's element count from its size in the graph.</summary>
    public static long ArrayLength(HeapGraph graph, int obj)
    {
        var type = graph.TypeIndices[obj];
        var component = graph.Types.ComponentSizes[type];
        return component == 0 ? 0 : (long)((graph.GetSize(obj) - graph.Types.BaseSizes[type]) / component);
    }

    public static bool IsArray(HeapGraph graph, int obj) => graph.Types.Kinds[graph.TypeIndices[obj]].HasFlag(TypeKind.Array);
}

internal sealed record TypeRow(string Name, string Module, long Objects, long Bytes);

/// <summary>What is on the large object heap, by type.</summary>
internal sealed class LargeObjectHeapInspection : IInspection
{
    public string Name => "loh";

    public string Title => "Large object heap";

    public string Cause => "Objects of 85,000 bytes or more live on the large object heap, which is collected only with gen2 and not compacted by default.";

    public string Fix => "Reuse large buffers (ArrayPool), or split them below 85,000 bytes.";

    public SnapshotCapabilities Requires => SnapshotCapabilities.Generations;

    public InspectionResult Run(InspectionContext context)
    {
        var graph = context.Graph;
        var kinds = graph.HeapKinds;
        var rows = InspectionRows.ByType(graph, i => kinds[i] == HeapKind.LargeObjectHeap, "Reading the large object heap", context, out var count, out var bytes);
        return new InspectionResult(count, bytes, null, null, InspectionRows.TypeTable(rows, InspectionScopes.LargeObjectHeap));
    }
}

/// <summary>Arrays of 85,000 bytes or more, one row each, largest first.</summary>
internal sealed class LargeArraysInspection : IInspection
{
    /// <summary>The large object heap threshold.</summary>
    public const long MinBytes = 85_000;

    private static readonly InspectionColumn[] s_columns =
    [
        new(InspectionColumns.Address, InspectionColumnKind.Address, IsKey: true),
        new(InspectionColumns.Type, InspectionColumnKind.Text),
        new(InspectionColumns.Length, InspectionColumnKind.Integer),
        new(InspectionColumns.Bytes, InspectionColumnKind.Bytes),
        new(InspectionColumns.Generation, InspectionColumnKind.Text),
    ];

    public string Name => "large-arrays";

    public string Title => "Large arrays";

    public string Cause => "Each array of 85,000 bytes or more is a large object: costly to allocate, collected only with gen2.";

    public string Fix => "Pool and reuse them (ArrayPool<T>.Shared), or size them to the data actually held.";

    public SnapshotCapabilities Requires => SnapshotCapabilities.ObjectAddresses;

    public InspectionResult Run(InspectionContext context)
    {
        var graph = context.Graph;
        var found = new List<int>();
        long bytes = 0;
        for (var i = 0; i < graph.ObjectCount; i++)
        {
            if ((i & 0xFFFF) == 0)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                context.Report("Finding large arrays", i, graph.ObjectCount);
            }

            if (InspectionRows.IsArray(graph, i) && (long)graph.GetSize(i) >= MinBytes)
            {
                found.Add(i);
                bytes += (long)graph.GetSize(i);
            }
        }

        var rows = found.OrderByDescending(i => graph.GetSize(i)).ThenBy(i => i).ToArray();
        return new InspectionResult(rows.Length, bytes, null, null, new RowTable<int>(s_columns, rows, 4, (obj, cells) =>
        {
            cells[0] = InspectionRows.Hex(graph.Addresses[obj]);
            cells[1] = graph.Types.GetName(graph.TypeIndices[obj]);
            cells[2] = InspectionRows.Number(InspectionRows.ArrayLength(graph, obj));
            cells[3] = InspectionRows.Number((long)graph.GetSize(obj));
            cells[4] = graph.HeapKinds[obj].ToString();
        }));
    }
}

/// <summary>Reference arrays of at least 16 slots, at least half of them null.</summary>
internal sealed class SparseArraysInspection : IInspection
{
    public const int MinLength = 16;

    private static readonly InspectionColumn[] s_columns =
    [
        new(InspectionColumns.Address, InspectionColumnKind.Address, IsKey: true),
        new(InspectionColumns.Type, InspectionColumnKind.Text),
        new(InspectionColumns.Length, InspectionColumnKind.Integer),
        new(InspectionColumns.NullSlots, InspectionColumnKind.Integer),
        new(InspectionColumns.Wasted, InspectionColumnKind.Bytes),
        new(InspectionColumns.Bytes, InspectionColumnKind.Bytes),
    ];

    public string Name => "sparse-arrays";

    public string Title => "Sparse arrays";

    public string Cause => "Arrays of references that are mostly null: every empty slot still costs a pointer.";

    public string Fix => "Size the array to its contents, or use a dictionary or a list for sparse data.";

    public SnapshotCapabilities Requires => SnapshotCapabilities.ObjectAddresses | SnapshotCapabilities.CompleteGraph;

    public SnapshotFacts RequiresFacts => SnapshotFacts.ReferenceArrayMarks;

    public InspectionResult Run(InspectionContext context)
    {
        var graph = context.Graph;
        var pointer = context.Info.PointerSize;
        var rows = new List<SparseRow>();
        long wasted = 0, bytes = 0;
        for (var i = 0; i < graph.ObjectCount; i++)
        {
            if ((i & 0xFFFF) == 0)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                context.Report("Finding sparse arrays", i, graph.ObjectCount);
            }

            if (!graph.Types.Kinds[graph.TypeIndices[i]].HasFlag(TypeKind.ReferenceArray))
            {
                continue;
            }

            // A reference array's non-null slots are its outgoing references (the graph keeps repeated targets).
            var length = InspectionRows.ArrayLength(graph, i);
            var nulls = Math.Max(0, length - graph.GetReferences(i).Length);
            if (length >= MinLength && nulls * 2 >= length)
            {
                var row = new SparseRow(i, length, nulls, nulls * pointer);
                rows.Add(row);
                wasted += row.Wasted;
                bytes += (long)graph.GetSize(i);
            }
        }

        var sorted = rows.OrderByDescending(r => r.Wasted).ThenBy(r => r.Object).ToArray();
        // References to non-objects are dropped from the graph and would read as null slots.
        var note = graph.DroppedReferenceCount > 0
            ? string.Create(CultureInfo.InvariantCulture, $"{graph.DroppedReferenceCount:N0} references to non-objects were dropped when loading; slots holding them count as null here.")
            : null;
        return new InspectionResult(sorted.Length, bytes, wasted, note, new RowTable<SparseRow>(s_columns, sorted, 32, (row, cells) =>
        {
            cells[0] = InspectionRows.Hex(graph.Addresses[row.Object]);
            cells[1] = graph.Types.GetName(graph.TypeIndices[row.Object]);
            cells[2] = InspectionRows.Number(row.Length);
            cells[3] = InspectionRows.Number(row.Nulls);
            cells[4] = InspectionRows.Number(row.Wasted);
            cells[5] = InspectionRows.Number((long)graph.GetSize(row.Object));
        }));
    }

    private sealed record SparseRow(int Object, long Length, long Nulls, long Wasted);
}

/// <summary>
/// Objects with a finalizer, by type (review H1 of 1.6): those registered and still reachable (their finalizer runs
/// after they die) and those already dead and queued for the finalizer thread ("ready"), which a slow or blocked
/// finalizer thread lets pile up.
/// </summary>
internal sealed class FinalizableObjectsInspection : IInspection
{
    private static readonly InspectionColumn[] s_columns =
    [
        new(InspectionColumns.Type, InspectionColumnKind.Text, IsKey: true),
        new(InspectionColumns.Objects, InspectionColumnKind.Integer),
        new(InspectionColumns.Ready, InspectionColumnKind.Integer),
        new(InspectionColumns.Bytes, InspectionColumnKind.Bytes),
        new(InspectionColumns.TypeName, InspectionColumnKind.Text),
        new(InspectionColumns.Module, InspectionColumnKind.Text),
        new(InspectionColumns.Scope, InspectionColumnKind.Text),
    ];

    public string Name => "finalizable";

    public string Title => "Finalizable objects";

    public string Cause => "An object with a finalizer survives at least one more collection and is finalized on a single thread; \"ready\" ones are dead and still waiting for it.";

    public string Fix => "Dispose these objects and call GC.SuppressFinalize in Dispose; prefer SafeHandle over finalizers; a growing ready count means a slow or blocked finalizer.";

    public SnapshotCapabilities Requires => SnapshotCapabilities.ObjectAddresses | SnapshotCapabilities.ExactCounts;

    public InspectionResult Run(InspectionContext context)
    {
        var graph = context.Graph;
        var ready = Finalization.ReadyObjects(graph);
        var flags = graph.Flags;
        var totals = new Dictionary<(string Name, string Module), (long Objects, long Ready, long Bytes)>();
        var types = graph.TypeIndices.Span;
        long count = 0, bytes = 0;
        for (var i = 0; i < types.Length; i++)
        {
            if ((i & 0xFFFF) == 0)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                context.Report("Finding finalizable objects", i, types.Length);
            }

            var isReady = ready.Contains(i);
            if (!isReady && !flags[i].HasFlag(ObjectFlags.HasFinalizer))
            {
                continue;
            }

            var size = (long)graph.GetSize(i);
            var key = (graph.Types.GetName(types[i]), TypeGroups.ModuleKey(graph.Types.GetModule(types[i])));
            var (objects, readyCount, total) = totals.GetValueOrDefault(key);
            totals[key] = (objects + 1, readyCount + (isReady ? 1 : 0), total + size);
            count++;
            bytes += size;
        }

        var rows = totals.Select(t => (t.Key.Name, t.Key.Module, t.Value.Objects, t.Value.Ready, t.Value.Bytes))
            .OrderByDescending(r => r.Ready).ThenByDescending(r => r.Bytes).ThenBy(r => r.Name, StringComparer.Ordinal)
            .ToArray();
        return new InspectionResult(count, bytes, null, null, new RowTable<(string Name, string Module, long Objects, long Ready, long Bytes)>(s_columns, rows, 104, (row, cells) =>
        {
            cells[0] = row.Name;
            cells[1] = InspectionRows.Number(row.Objects);
            cells[2] = InspectionRows.Number(row.Ready);
            cells[3] = InspectionRows.Number(row.Bytes);
            cells[4] = row.Name;
            cells[5] = row.Module;
            cells[6] = InspectionScopes.Finalizable;
        }));
    }
}

/// <summary>Finalization facts of a graph, shared by the finalizable inspection and the finalizable instance scope.</summary>
public static class Finalization
{
    /// <summary>Objects dead and queued for the finalizer thread: the f-reachable queue, recorded as finalizer queue roots.</summary>
    public static HashSet<int> ReadyObjects(HeapGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var ready = new HashSet<int>();
        foreach (var root in graph.Roots.Roots.Span)
        {
            if (root.Kind == RootKind.FinalizerQueue)
            {
                ready.Add(root.ObjectIndex);
            }
        }

        return ready;
    }
}

/// <summary>
/// Free space and pinning per heap kind: free bytes (the GC's free objects, from the loader) next to live bytes, and
/// the objects pinned by handles or living on the pinned object heap, which keep gaps from being compacted away.
/// </summary>
internal sealed class FragmentationInspection : IInspection
{
    private static readonly HeapKind[] s_kinds = [HeapKind.Gen0, HeapKind.Gen1, HeapKind.Gen2, HeapKind.LargeObjectHeap, HeapKind.PinnedObjectHeap];

    private static readonly InspectionColumn[] s_columns =
    [
        new(InspectionColumns.Heap, InspectionColumnKind.Text, IsKey: true),
        new(InspectionColumns.Live, InspectionColumnKind.Bytes),
        new(InspectionColumns.Free, InspectionColumnKind.Bytes),
        new(InspectionColumns.FreePercent, InspectionColumnKind.Percent),
        new(InspectionColumns.PinnedObjects, InspectionColumnKind.Integer),
        new(InspectionColumns.PinnedBytes, InspectionColumnKind.Bytes),
    ];

    public string Name => "fragmentation";

    public string Title => "Fragmentation";

    public string Cause => "Free gaps between live objects cost memory until the GC compacts them; pinned objects stop compaction around them.";

    public string Fix => "Pin briefly (fixed blocks, Memory<T>.Pin), allocate long-lived pinned buffers on the pinned object heap, pool large buffers.";

    public SnapshotCapabilities Requires => SnapshotCapabilities.Generations;

    public InspectionResult Run(InspectionContext context)
    {
        var graph = context.Graph;
        var live = new long[Enum.GetValues<HeapKind>().Length];
        var pinnedObjects = new long[live.Length];
        var pinnedBytes = new long[live.Length];
        var pinnedByRoot = PinnedByRoot(graph);
        var kinds = graph.HeapKinds.Span;
        for (var i = 0; i < kinds.Length; i++)
        {
            if ((i & 0xFFFF) == 0)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                context.Report("Measuring fragmentation", i, kinds.Length);
            }

            var size = (long)graph.GetSize(i);
            live[(int)kinds[i]] += size;
            if (kinds[i] == HeapKind.PinnedObjectHeap || pinnedByRoot.Contains(i))
            {
                pinnedObjects[(int)kinds[i]]++;
                pinnedBytes[(int)kinds[i]] += size;
            }
        }

        var free = context.Info.FreeBytes;
        var rows = s_kinds.Select(k => new FragmentRow(k, live[(int)k], free is { } f && (int)k < f.Count ? f[(int)k] : null, pinnedObjects[(int)k], pinnedBytes[(int)k])).ToArray();
        var totalFree = free?.Sum();
        var note = free is null ? "Free space is unknown for this snapshot: its index cache was written before HeapScope 1.6. Reopen it with Rebuild to measure it." : null;
        return new InspectionResult(pinnedObjects.Sum(), pinnedBytes.Sum(), totalFree, note, new RowTable<FragmentRow>(s_columns, rows, 64, (row, cells) =>
        {
            cells[0] = row.Kind.ToString();
            cells[1] = InspectionRows.Number(row.Live);
            cells[2] = row.Free is { } bytes ? InspectionRows.Number(bytes) : null;
            cells[3] = row.Free is { } part && part + row.Live > 0 ? (100.0 * part / (part + row.Live)).ToString("F2", CultureInfo.InvariantCulture) : null;
            cells[4] = InspectionRows.Number(row.PinnedObjects);
            cells[5] = InspectionRows.Number(row.PinnedBytes);
        }));
    }

    /// <summary>
    /// Objects a root pins, in one walk of the root table: pinned and async pinned handles, and pinned stack roots
    /// (<c>fixed</c> and pinned locals, review of 1.6).
    /// </summary>
    private static HashSet<int> PinnedByRoot(HeapGraph graph)
    {
        var pinned = new HashSet<int>();
        foreach (var root in graph.Roots.Roots.Span)
        {
            if (root.Kind is RootKind.PinnedHandle or RootKind.AsyncPinnedHandle || root.Flags.HasFlag(RootFlags.Pinned))
            {
                pinned.Add(root.ObjectIndex);
            }
        }

        return pinned;
    }

    private sealed record FragmentRow(HeapKind Kind, long Live, long? Free, long PinnedObjects, long PinnedBytes);
}
