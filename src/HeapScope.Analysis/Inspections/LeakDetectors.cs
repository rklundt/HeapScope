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
using HeapScope.Analysis.Retention;
using HeapScope.Core;
using HeapScope.Core.Graph;

namespace HeapScope.Analysis.Inspections;

// The leak detectors (sprint 2.6, ADR-020 amended): inspections of kind Leak that name a leak pattern, its cause and its
// fix. Each counts reachable objects only (garbage the GC has not collected is no leak), reads the fields it needs
// through the typed reader, and logs its thresholds as parameters and, as its basis, what it scanned, what it left out
// and why, and every row it reports, so a disputed finding is traced to its rule and inputs.

/// <summary>Reads the few runtime fields the detectors need, tolerating missing ones (another runtime version).</summary>
internal static class Fields
{
    /// <summary>An integer field; native ints (<c>IntPtr</c>) arrive as pointers (re-review N1).</summary>
    public static long? Integer(IObjectReader reader, ulong address, string path) =>
        reader.ReadField(address, path) is { } value
            ? value.Kind switch
            {
                ObjectValueKind.Integer => value.Integer,
                ObjectValueKind.Unsigned or ObjectValueKind.Pointer => (long)value.Unsigned,
                _ => null,
            }
            : null;

    public static bool? Boolean(IObjectReader reader, ulong address, string path) =>
        reader.ReadField(address, path) is { Kind: ObjectValueKind.Boolean } value ? value.Boolean : null;

    /// <summary>A flag stored as a bool, or as an integer set with Interlocked (non-zero is set; review M10); null when missing.</summary>
    public static bool? Flag(IObjectReader reader, ulong address, string path) =>
        reader.ReadField(address, path) is { } value
            ? value.Kind switch
            {
                ObjectValueKind.Boolean => value.Boolean,
                ObjectValueKind.Integer => value.Integer != 0,
                ObjectValueKind.Unsigned => value.Unsigned != 0,
                _ => null,
            }
            : null;

    /// <summary>The object a reference field points at: its address and runtime type; null for a null or missing field.</summary>
    public static (ulong Address, string Type)? Reference(IObjectReader reader, ulong address, string path) =>
        reader.ReadField(address, path) is { Kind: ObjectValueKind.Reference or ObjectValueKind.Array } value && value.Address != 0
            ? (value.Address, value.TypeName ?? "?")
            : null;

    /// <summary>Whether the delegate at <paramref name="address"/> is a multicast one: its invocation list is an array.</summary>
    public static bool IsMulticast(IObjectReader reader, ulong address) =>
        Reference(reader, address, "_invocationList") is { Type: var list } && list.EndsWith("[]", StringComparison.Ordinal);

    /// <summary>
    /// The object a single delegate calls its method on; null for a static method (review M4): the runtime stores the
    /// delegate itself in <c>_target</c> for a static or open delegate, and marks it in <c>_methodPtrAux</c>.
    /// </summary>
    public static (ulong Address, string Type)? DelegateTarget(IObjectReader reader, ulong address) =>
        Reference(reader, address, "_target") is { } target && target.Address != address && (Integer(reader, address, "_methodPtrAux") ?? 0) == 0
            ? target
            : null;

    /// <summary>
    /// The targets of a delegate: each one of its invocation list's (a multicast delegate), the wrapped delegate's, or
    /// its own. A static method's delegate has no target (null in the list).
    /// </summary>
    public static List<(ulong Address, string Type)?> DelegateTargets(IObjectReader reader, ulong address, int max, CancellationToken cancellationToken)
    {
        var targets = new List<(ulong Address, string Type)?>();
        if (Reference(reader, address, "_invocationList") is { } list)
        {
            if (list.Type.EndsWith("[]", StringComparison.Ordinal))
            {
                var count = (int)Math.Min(Integer(reader, address, "_invocationCount") ?? max, max);
                foreach (var element in reader.ReadFields(list.Address, count, 0, cancellationToken))
                {
                    if (element.Value is { Kind: ObjectValueKind.Reference, Address: not 0 } item)
                    {
                        targets.Add(DelegateTarget(reader, item.Address));
                    }
                }

                return targets;
            }

            // A wrapper delegate holds the delegate it calls in its invocation list.
            targets.Add(DelegateTarget(reader, list.Address));
            return targets;
        }

        targets.Add(DelegateTarget(reader, address));
        return targets;
    }
}

/// <summary>Type masks, reachable objects per type and names, shared by the detectors.</summary>
internal static class DetectorGraph
{
    /// <summary>A flag per type index: whether <paramref name="match"/> holds for it.</summary>
    public static bool[] TypeMask(HeapGraph graph, Func<int, bool> match)
    {
        var mask = new bool[graph.Types.Count];
        for (var t = 0; t < mask.Length; t++)
        {
            mask[t] = match(t);
        }

        return mask;
    }

    public static bool[] TypesNamed(HeapGraph graph, params string[] names) =>
        TypeMask(graph, t => names.Contains(graph.Types.GetName(t), StringComparer.Ordinal));

    /// <summary>The reachable objects of the types in <paramref name="types"/>, in index order.</summary>
    public static List<int> ReachableOf(HeapGraph graph, Reachability reachable, bool[] types, InspectionContext context, string phase)
    {
        var found = new List<int>();
        if (!types.Contains(true))
        {
            return found;
        }

        var typeIndices = graph.TypeIndices.Span;
        for (var i = 0; i < typeIndices.Length; i++)
        {
            if ((i & 0xFFFF) == 0)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                context.Report(phase, i, typeIndices.Length);
            }

            if (types[typeIndices[i]] && reachable.IsReachable(i))
            {
                found.Add(i);
            }
        }

        return found;
    }

    /// <summary>"Name (count), Name (count)" for the most common names, then how many others.</summary>
    public static string Top(Dictionary<string, long> counts, int shown = 3)
    {
        var top = counts.OrderByDescending(c => c.Value).ThenBy(c => c.Key, StringComparer.Ordinal).ToList();
        var text = string.Join(", ", top.Take(shown).Select(c => string.Create(CultureInfo.InvariantCulture, $"{c.Key} ({c.Value:N0})")));
        return top.Count > shown ? string.Create(CultureInfo.InvariantCulture, $"{text} and {top.Count - shown:N0} more types") : text;
    }

    /// <summary>The framework's own types, by namespace.</summary>
    public static bool IsFramework(string name) =>
        name.StartsWith("System.", StringComparison.Ordinal) || name.StartsWith("Microsoft.", StringComparison.Ordinal) || name.StartsWith("Internal.", StringComparison.Ordinal);

    /// <summary>A type the compiler generated: a closure, a lambda cache or a state machine.</summary>
    public static bool IsCompilerGenerated(string name) =>
        name.StartsWith('<') || name.Contains("+<", StringComparison.Ordinal) || name.Contains(".<", StringComparison.Ordinal);

    public static string Module(HeapGraph graph, int type) => TypeGroups.ModuleKey(graph.Types.GetModule(type));
}

/// <summary>
/// What a detector left out, by reason, with the most common keys for each (sprint 2.6 re-review): a disputed absence
/// ("why was my event not reported?") is answered from the log by name, not only by a count.
/// </summary>
internal sealed class Exclusions
{
    private readonly List<string> _order = [];
    private readonly Dictionary<string, Dictionary<string, long>> _byReason = new(StringComparer.Ordinal);

    public void Add(string reason, string key)
    {
        if (!_byReason.TryGetValue(reason, out var keys))
        {
            keys = new Dictionary<string, long>(StringComparer.Ordinal);
            _byReason[reason] = keys;
            _order.Add(reason);
        }

        keys[key] = keys.GetValueOrDefault(key) + 1;
    }

    /// <summary>"left out: reason count (Key (n), Key (n), Key (n)), ..."; "none left out" when empty.</summary>
    public override string ToString() =>
        _order.Count == 0
            ? "none left out"
            : "left out: " + string.Join(", ", _order.Select(r => string.Create(CultureInfo.InvariantCulture, $"{r} {_byReason[r].Values.Sum()} ({DetectorGraph.Top(_byReason[r])})")));
}

/// <summary>
/// A row naming a type's objects: what it shows, and the hidden columns that open them. Rows open the type's reachable
/// objects (review H5), a part of the type, so the whole-type actions stay off; an empty <see cref="TypeName"/> opens nothing.
/// </summary>
/// <param name="Members">The objects the row counted (sprint 3.1): what the row opens as a set.</param>
internal sealed record DetectorTypeRow(string Key, string Detail, long Objects, long Bytes, string TypeName, string Module, int[]? Members = null);

/// <summary>
/// Collections a static field holds directly that only grow (sprint 2.6): with a baseline snapshot, those that grew by at
/// least <see cref="MinGrowth"/> elements and <see cref="MinGrowthShare"/>; without one, those of at least
/// <see cref="MinLarge"/> elements (large; growth unknown). Matched across snapshots by the static field's name.
/// </summary>
internal sealed class GrowingStaticCollectionsDetector : IInspection
{
    public const long MinGrowth = 1_000;
    public const double MinGrowthShare = 0.10;
    public const long MinLarge = 10_000;

    private static readonly InspectionColumn[] s_columns =
    [
        new(InspectionColumns.Field, InspectionColumnKind.Text, IsKey: true),
        new(InspectionColumns.Type, InspectionColumnKind.Text),
        new(InspectionColumns.Count, InspectionColumnKind.Integer),
        new(InspectionColumns.Baseline, InspectionColumnKind.Integer),
        new(InspectionColumns.Growth, InspectionColumnKind.Integer),
        new(InspectionColumns.Bytes, InspectionColumnKind.Bytes),
        new(InspectionColumns.Address, InspectionColumnKind.Address),
    ];

    public string Name => "growing-static-collections";

    public string Title => "Growing static collections";

    public string Cause => "A collection held by a static field only grows: what is added is never removed, so it all stays alive for the life of the process.";

    public string Fix => "Bound it: evict old entries, cap its size or use a cache with expiration (MemoryCache), or remove entries when their work is done.";

    public SnapshotCapabilities Requires => SnapshotCapabilities.FieldValues | SnapshotCapabilities.RootDetails;

    public InspectionKind Kind => InspectionKind.Leak;

    public bool UsesBaseline => true;

    /// <summary>The collections counted, by their generic definition's name (the rest are listed as not counted).</summary>
    internal const string Counted = "List,Queue,Stack,ArrayList,Dictionary,HashSet,Hashtable,ConcurrentDictionary,reference arrays";

    public string Parameters => string.Create(CultureInfo.InvariantCulture, $"MinGrowth={MinGrowth};MinGrowthShare={MinGrowthShare:P0};MinLargeWithoutBaseline={MinLarge};Counted={Counted}");

    public InspectionResult Run(InspectionContext context)
    {
        var ct = context.CancellationToken;
        var skipped = new Exclusions();
        var current = Collections(context.Graph, Readers.Of(context), ct, skipped);

        // The worker refuses a baseline it cannot read (review H8): one without a reader here is a caller's mistake.
        var baseline = context.Baseline is { } b
            ? Collections(b.Graph, b.Reader ?? throw new InvalidOperationException("The baseline snapshot has no field values; choose a full dump."), ct, new Exclusions())
            : null;
        var rows = new List<Row>();
        var nearMisses = new List<Row>();
        foreach (var (field, found) in current)
        {
            if (baseline is null)
            {
                // Without growth to go on, the runtime's and the framework's own large tables are by design.
                if (found.Count >= MinLarge && DetectorGraph.IsFramework(field))
                {
                    skipped.Add("framework static without a baseline", field);
                }
                else if (found.Count >= MinLarge)
                {
                    rows.Add(new Row(field, found, null, null));
                }

                continue;
            }

            var before = baseline.TryGetValue(field, out var then) ? then.Count : 0;
            var growth = found.Count - before;
            if (growth >= MinGrowth && growth >= MinGrowthShare * Math.Max(before, 1))
            {
                rows.Add(new Row(field, found, before, growth));
            }
            else if (growth > 0)
            {
                nearMisses.Add(new Row(field, found, before, growth));
            }
        }

        var ordered = rows.OrderByDescending(r => r.Growth ?? r.Collection.Count).ThenBy(r => r.Field, StringComparer.Ordinal).ToArray();
        var note = baseline is null
            ? string.Create(CultureInfo.InvariantCulture, $"No baseline snapshot: these are the application's static collections of at least {MinLarge:N0} elements (the framework's own are left out); whether they grow is unknown. Choose an earlier snapshot of the same process as the baseline (in the CLI, --baseline) to see growth.")
            : BaselineWarning(context.Info, context.Baseline!.Info);
        var basis = string.Create(
            CultureInfo.InvariantCulture,
            $"{current.Count} static collections counted{(baseline is null ? string.Empty : $", {baseline.Count} in the baseline")}; {skipped}; reported: {Describe(ordered)}; largest growth below the thresholds: {Describe(nearMisses.OrderByDescending(r => r.Growth).Take(5))}");
        return new InspectionResult(
            ordered.Length,
            ordered.Sum(r => r.Collection.Bytes),
            null,
            note,
            new RowTable<Row>(s_columns, ordered, 120, (row, cells) =>
            {
                cells[0] = row.Field;
                cells[1] = row.Collection.Type;
                cells[2] = InspectionRows.Number(row.Collection.Count);
                cells[3] = row.Before is { } before ? InspectionRows.Number(before) : null;
                cells[4] = row.Growth is { } growth ? InspectionRows.Number(growth) : null;
                cells[5] = InspectionRows.Number(row.Collection.Bytes);
                cells[6] = InspectionRows.Hex(row.Collection.Address);
            }),
            basis);
    }

    /// <summary>Why a growth comparison may mislead (review M8): a baseline captured later, or from another process.</summary>
    internal static string? BaselineWarning(SnapshotInfo current, SnapshotInfo baseline)
    {
        if (current.CapturedAtUtc is { } now && baseline.CapturedAtUtc is { } then && then > now)
        {
            return "The baseline was captured after this snapshot, so what grew shows as shrinking and is not reported. Choose an earlier snapshot as the baseline.";
        }

        if (current.ProcessId is { } pid && baseline.ProcessId is { } basePid && (pid != basePid || current.ProcessName != baseline.ProcessName))
        {
            return string.Create(CultureInfo.InvariantCulture, $"The baseline is from another process ({baseline.ProcessName} {basePid}; this one is {current.ProcessName} {pid}): growth compares two runs, not one process over time.");
        }

        return null;
    }

    private static string Describe(IEnumerable<Row> rows)
    {
        var text = string.Join("; ", rows.Select(r => string.Create(CultureInfo.InvariantCulture, $"{r.Field} {r.Collection.Type} {r.Collection.Count}{(r.Before is { } before ? $" (was {before})" : string.Empty)}")));
        return text.Length == 0 ? "none" : text;
    }

    /// <summary>The collections static fields hold, by "DeclaringType.field", with their element counts.</summary>
    internal static Dictionary<string, Collection> Collections(HeapGraph graph, IObjectReader reader, CancellationToken cancellationToken, Exclusions skipped)
    {
        var found = new Dictionary<string, Collection>(StringComparer.Ordinal);
        foreach (var root in graph.Roots.Roots.Span)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (root.Kind != RootKind.StaticField || graph.Strings[root.DetailId] is not { Length: > 0 } field)
            {
                continue;
            }

            var obj = root.ObjectIndex;
            var type = graph.Types.GetName(graph.TypeIndices[obj]);
            if (found.ContainsKey(field))
            {
                skipped.Add("field name seen twice", field);
            }
            else if (Count(graph, reader, obj, type, cancellationToken) is { } count)
            {
                found[field] = new Collection(type, count, Bytes(graph, reader, obj, type), graph.Addresses[obj]);
            }
            else if (type.Contains("Collection", StringComparison.Ordinal) || type.Contains("Concurrent", StringComparison.Ordinal) || type.Contains("Linked", StringComparison.Ordinal) || type.Contains("Sorted", StringComparison.Ordinal))
            {
                // A collection the detector does not count (Backlog): named, so its absence is explained.
                var open = type.IndexOf('<', StringComparison.Ordinal);
                skipped.Add("collection type not counted", open < 0 ? type : type[..open]);
            }
        }

        return found;
    }

    /// <summary>The collection object and the arrays it holds directly; a ConcurrentDictionary's through its tables (review M10).</summary>
    private static long Bytes(HeapGraph graph, IObjectReader reader, int obj, string type)
    {
        var bytes = (long)graph.GetSize(obj);
        foreach (var child in graph.GetReferences(obj))
        {
            if (InspectionRows.IsArray(graph, child))
            {
                bytes += (long)graph.GetSize(child);
            }
        }

        if (type.StartsWith("System.Collections.Concurrent.ConcurrentDictionary<", StringComparison.Ordinal)
            && Fields.Reference(reader, graph.Addresses[obj], "_tables._buckets") is { } buckets
            && graph.TryGetIndex(buckets.Address, out var index))
        {
            bytes += (long)graph.GetSize(index);
        }

        return bytes;
    }

    /// <summary>
    /// A collection's element count from its own fields; null for an object that is not a known collection. Lists,
    /// queues, stacks, dictionaries, sets, hash tables, concurrent dictionaries and reference arrays are counted; the
    /// other collections are not (Backlog).
    /// </summary>
    private static long? Count(HeapGraph graph, IObjectReader reader, int obj, string type, CancellationToken cancellationToken)
    {
        var address = graph.Addresses[obj];
        if (InspectionRows.IsArray(graph, obj))
        {
            // An array of values holds no objects: only reference arrays are collections that keep things alive.
            return graph.Types.Kinds[graph.TypeIndices[obj]].HasFlag(TypeKind.ReferenceArray) ? InspectionRows.ArrayLength(graph, obj) : null;
        }

        var open = type.IndexOf('<', StringComparison.Ordinal);
        var name = open < 0 ? type : type[..open];
        switch (name)
        {
            case "System.Collections.Generic.List":
            case "System.Collections.Generic.Queue":
            case "System.Collections.Generic.Stack":
            case "System.Collections.ArrayList":
                return Fields.Integer(reader, address, "_size");
            case "System.Collections.Generic.Dictionary":
            case "System.Collections.Generic.HashSet":
                return Fields.Integer(reader, address, "_count") is { } count ? count - (Fields.Integer(reader, address, "_freeCount") ?? 0) : null;
            case "System.Collections.Hashtable":
                return Fields.Integer(reader, address, "_count");
            case "System.Collections.Concurrent.ConcurrentDictionary":
                if (Fields.Reference(reader, address, "_tables._countPerLock") is not { } counts)
                {
                    return null;
                }

                long total = 0;
                foreach (var element in reader.ReadFields(counts.Address, 4096, 0, cancellationToken))
                {
                    total += element.Value.Kind == ObjectValueKind.Integer ? element.Value.Integer : 0;
                }

                return total;
            default:
                return null;
        }
    }

    internal sealed record Collection(string Type, long Count, long Bytes, ulong Address);

    private sealed record Row(string Field, Collection Collection, long? Before, long? Growth);
}

/// <summary>The typed reader of a run, for the detectors that read fields (they require FieldValues).</summary>
internal static class Readers
{
    public static IObjectReader Of(InspectionContext context) =>
        context.Reader ?? throw new InvalidOperationException("This detector reads fields: the snapshot needs field values.");
}

/// <summary>
/// Event handler leaks (sprint 2.6): an event of a publisher type holding at least <see cref="MinSubscribers"/>
/// subscribers across its publishers. A subscriber is a delegate whose target the event keeps alive; a multicast event
/// holds its subscribers in an invocation list. Only field-like events count (review H3): the delegate is held by a field
/// named like an event (the event's backing field), by a reachable publisher that is not the subscriber's own target, a
/// compiler-generated type or, unless through a multicast, a framework type (their callback fields are not events).
/// Delegates held by arrays or collections are not events (the growing collections detector names those); timers' events
/// are the timer detector's.
/// </summary>
internal sealed class EventHandlerLeaksDetector : IInspection
{
    public const long MinSubscribers = 1_000;

    private static readonly InspectionColumn[] s_columns =
    [
        new(InspectionColumns.Event, InspectionColumnKind.Text, IsKey: true),
        new(InspectionColumns.Publishers, InspectionColumnKind.Integer),
        new(InspectionColumns.Subscribers, InspectionColumnKind.Integer),
        new(InspectionColumns.Bytes, InspectionColumnKind.Bytes),
        new(InspectionColumns.SubscriberTypes, InspectionColumnKind.Text),
        new(InspectionColumns.TypeName, InspectionColumnKind.Text),
        new(InspectionColumns.Module, InspectionColumnKind.Text),
        new(InspectionColumns.Scope, InspectionColumnKind.Text),
    ];

    public string Name => "event-handler-leaks";

    public string Title => "Event handler leaks";

    public string Cause => "Objects subscribe to an event of a long-lived publisher and never unsubscribe: the event keeps every subscriber alive.";

    public string Fix => "Unsubscribe (-=) when the subscriber is done, typically in Dispose; or let the publisher die with its subscribers.";

    public SnapshotCapabilities Requires => SnapshotCapabilities.FieldValues;

    public InspectionKind Kind => InspectionKind.Leak;

    public bool UsesReferrers => true;

    public string Parameters => string.Create(CultureInfo.InvariantCulture, $"MinSubscribers={MinSubscribers};Events=field-like");

    /// <summary>
    /// A field that holds an event: a field-like event's backing field, named as the event is (PascalCase, no prefix); or,
    /// for a multicast delegate, any field of the publisher's own (a hand-written event's <c>_changed</c>; re-review).
    /// </summary>
    internal static bool IsEventField(string field, bool multicast) =>
        field.Length > 0
        && field.IndexOfAny(['.', ',', '[', ' ']) < 0
        && (multicast || (char.IsUpper(field[0]) && !field.Contains('<', StringComparison.Ordinal)));

    public InspectionResult Run(InspectionContext context)
    {
        var graph = context.Graph;
        var reader = Readers.Of(context);
        var referrers = context.Referrers ?? throw new InvalidOperationException("The event handler detector needs the incoming references.");
        var ct = context.CancellationToken;
        var delegates = DetectorGraph.TypeMask(graph, t => graph.Types.Kinds[t].HasFlag(TypeKind.Delegate));

        // Only delegate types with enough objects can reach the threshold: the rest are not read.
        var perType = new long[graph.Types.Count];
        var typeIndices = graph.TypeIndices.Span;
        for (var i = 0; i < typeIndices.Length; i++)
        {
            if ((i & 0xFFFF) == 0)
            {
                ct.ThrowIfCancellationRequested();
            }

            if (delegates[typeIndices[i]])
            {
                perType[typeIndices[i]]++;
            }
        }

        var candidates = DetectorGraph.TypeMask(graph, t => perType[t] >= MinSubscribers);
        var reachable = Reachability.For(graph, ct);

        // Subscribers by publisher: each publisher's fields are read once, so two events of one delegate type stay apart (review H2).
        var pending = new Dictionary<int, List<Pending>>();
        var holders = new List<(int Value, int Publisher, bool Multicast)>();
        long read = 0;
        var skipped = new Exclusions();
        foreach (var obj in DetectorGraph.ReachableOf(graph, reachable, candidates, context, "Finding event subscribers"))
        {
            var address = graph.Addresses[obj];

            // A multicast delegate holding an invocation list is the event's value, not a subscriber.
            if (Fields.IsMulticast(reader, address))
            {
                continue;
            }

            read++;
            var target = Fields.DelegateTarget(reader, address);
            var targetIndex = target is { } t && graph.TryGetIndex(t.Address, out var index) ? index : -1;
            var bytes = (long)graph.GetSize(obj) + (targetIndex >= 0 ? (long)graph.GetSize(targetIndex) : 0);
            Holders(graph, referrers, delegates, obj, holders);
            foreach (var (value, publisher, multicast) in holders)
            {
                var name = graph.Types.GetName(graph.TypeIndices[publisher]);
                if (!reachable.IsReachable(publisher) || !reachable.IsReachable(value))
                {
                    skipped.Add("unreachable publisher", name);
                }
                else if (publisher == targetIndex)
                {
                    // A subscriber keeping its own handler to unsubscribe later is no event.
                    skipped.Add("held by its own target", name);
                }
                else if (name == "System.Timers.Timer")
                {
                    skipped.Add("System.Timers.Timer", name);
                }
                else if (DetectorGraph.IsCompilerGenerated(name))
                {
                    skipped.Add("compiler-generated holder", name);
                }
                else if (!multicast && DetectorGraph.IsFramework(name))
                {
                    // Lazy's factory, a task's action, a cancellation callback: one delegate a framework object calls.
                    skipped.Add("framework callback field", name);
                }
                else
                {
                    if (!pending.TryGetValue(publisher, out var list))
                    {
                        list = [];
                        pending[publisher] = list;
                    }

                    list.Add(new Pending(value, targetIndex < 0 ? -1 : graph.TypeIndices[targetIndex], bytes, multicast, obj, targetIndex));
                }
            }
        }

        // Only publishers of types whose subscribers can reach the threshold are read (re-review N3): each read is a full
        // read of the publisher's fields.
        var perPublisherType = new Dictionary<int, long>();
        foreach (var (publisher, subscribers) in pending)
        {
            var type = graph.TypeIndices[publisher];
            perPublisherType[type] = perPublisherType.GetValueOrDefault(type) + subscribers.Count;
        }

        var events = new Dictionary<(int Publisher, string Field), Event>();
        long publishersRead = 0;
        foreach (var (publisher, subscribers) in pending)
        {
            if (perPublisherType[graph.TypeIndices[publisher]] < MinSubscribers)
            {
                foreach (var _ in subscribers)
                {
                    skipped.Add("publisher type below the threshold", graph.Types.GetName(graph.TypeIndices[publisher]));
                }

                continue;
            }

            if ((++publishersRead & 0x3FF) == 0)
            {
                ct.ThrowIfCancellationRequested();
                context.Report("Naming events", publishersRead, pending.Count);
            }

            var paths = reader.ReadReferencePaths(graph.Addresses[publisher], 0, ct);
            var publisherType = graph.TypeIndices[publisher];
            foreach (var subscriber in subscribers)
            {
                if (paths.GetValueOrDefault(graph.Addresses[subscriber.Value]) is not { } field)
                {
                    skipped.Add("field not found", graph.Types.GetName(publisherType));
                    continue;
                }

                if (!IsEventField(field, subscriber.Multicast))
                {
                    skipped.Add("field not an event", graph.Types.GetName(publisherType) + "." + field);
                    continue;
                }

                if (!events.TryGetValue((publisherType, field), out var found))
                {
                    found = new Event();
                    events[(publisherType, field)] = found;
                }

                found.Publishers.Add(publisher);
                found.Subscribers++;
                // The objects whose bytes the row counts (sprint 3.1 review H1): the subscriber delegate (never the shared
                // multicast delegate holding it) and its target.
                found.Members.Add(subscriber.Subscriber);
                if (subscriber.Target >= 0)
                {
                    found.Members.Add(subscriber.Target);
                }
                found.Bytes += subscriber.Bytes;
                var targetType = subscriber.TargetType < 0 ? "(static method)" : graph.Types.GetName(subscriber.TargetType);
                found.TargetTypes[targetType] = found.TargetTypes.GetValueOrDefault(targetType) + 1;
            }
        }

        var rows = events
            .Where(e => e.Value.Subscribers >= MinSubscribers)
            .Select(e => new EventRow(
                graph.Types.GetName(e.Key.Publisher) + "." + e.Key.Field,
                e.Value.Publishers.Count,
                e.Value.Subscribers,
                e.Value.Bytes,
                DetectorGraph.Top(e.Value.TargetTypes),
                graph.Types.GetName(e.Key.Publisher),
                DetectorGraph.Module(graph, e.Key.Publisher),
                [.. e.Value.Members]))
            .OrderByDescending(r => r.Subscribers).ThenBy(r => r.Key, StringComparer.Ordinal)
            .ToArray();
        string Describe(IEnumerable<KeyValuePair<(int Publisher, string Field), Event>> list) =>
            string.Join("; ", list.Select(e => string.Create(CultureInfo.InvariantCulture, $"{graph.Types.GetName(e.Key.Publisher)}.{e.Key.Field} {e.Value.Subscribers} subscribers on {e.Value.Publishers.Count} publishers")));
        var basis = string.Create(
            CultureInfo.InvariantCulture,
            $"{candidates.Count(c => c)} delegate types with {MinSubscribers}+ objects, {read} subscribers read, {pending.Count} publishers, {events.Count} events; {skipped}; reported: {Describe(events.Where(e => e.Value.Subscribers >= MinSubscribers).OrderByDescending(e => e.Value.Subscribers))}; largest below the threshold: {Describe(events.Where(e => e.Value.Subscribers < MinSubscribers).OrderByDescending(e => e.Value.Subscribers).Take(5))}");
        return new InspectionResult(
            rows.Length,
            rows.Sum(r => r.Bytes),
            null,
            null,
            new RowTable<EventRow>(s_columns, rows, 160, (row, cells) =>
            {
                cells[0] = row.Key;
                cells[1] = InspectionRows.Number(row.Publishers);
                cells[2] = InspectionRows.Number(row.Subscribers);
                cells[3] = InspectionRows.Number(row.Bytes);
                cells[4] = row.TargetTypes;
                cells[5] = row.TypeName;
                cells[6] = row.Module;
                cells[7] = InspectionScopes.Reachable;
            },
            row => row.Members),
            basis);
    }

    /// <summary>
    /// The events holding a subscriber delegate, into <paramref name="found"/>: the delegate itself in a publisher's field,
    /// or the multicast delegate whose invocation list holds it; each with the publisher (a referrer that is neither an
    /// array nor a delegate).
    /// </summary>
    private static void Holders(HeapGraph graph, ReverseReferences referrers, bool[] delegates, int subscriber, List<(int Value, int Publisher, bool Multicast)> found)
    {
        found.Clear();
        foreach (var holder in referrers.GetReferrers(subscriber))
        {
            if (InspectionRows.IsArray(graph, holder))
            {
                foreach (var multicast in referrers.GetReferrers(holder))
                {
                    if (delegates[graph.TypeIndices[multicast]])
                    {
                        foreach (var publisher in referrers.GetReferrers(multicast))
                        {
                            if (IsPublisher(graph, delegates, publisher))
                            {
                                found.Add((multicast, publisher, true));
                            }
                        }
                    }
                }
            }
            else if (IsPublisher(graph, delegates, holder))
            {
                found.Add((subscriber, holder, false));
            }
        }
    }

    private static bool IsPublisher(HeapGraph graph, bool[] delegates, int obj) =>
        !InspectionRows.IsArray(graph, obj) && !delegates[graph.TypeIndices[obj]];

    /// <param name="Value">What the publisher's field holds: the subscriber, or the multicast delegate whose list holds it.</param>
    /// <param name="Subscriber">The subscriber delegate itself.</param>
    /// <param name="Target">The subscriber's target object, or -1 for a static method.</param>
    private readonly record struct Pending(int Value, int TargetType, long Bytes, bool Multicast, int Subscriber, int Target);

    private sealed record EventRow(string Key, long Publishers, long Subscribers, long Bytes, string TargetTypes, string TypeName, string Module, int[] Members);

    private sealed class Event
    {
        public HashSet<int> Publishers { get; } = [];

        public long Subscribers { get; set; }

        public long Bytes { get; set; }

        public Dictionary<string, long> TargetTypes { get; } = new(StringComparer.Ordinal);

        /// <summary>The subscriber delegates and their targets counted (sprint 3.1): what the row opens as a set.</summary>
        public List<int> Members { get; } = [];
    }
}

/// <summary>
/// Timer leaks (sprint 2.6): at least <see cref="MinTimers"/> periodic timers in the runtime's timer queue whose
/// callbacks belong to one type. The queue holds every started timer, so each keeps its callback's target alive until it
/// is disposed. Only periodic timers in the queue count (review H4): a one-shot timer (<c>Task.Delay</c>,
/// <c>CancelAfter</c>) releases itself when it fires, and a paused or fired one is out of the queue. A
/// <c>System.Timers.Timer</c>'s callback is the timer itself: its <c>Elapsed</c> handlers' targets are counted. A callback
/// on a static method or a lambda cache belongs to the timer's state.
/// </summary>
internal sealed class TimerLeaksDetector : IInspection
{
    public const long MinTimers = 500;

    /// <summary>The runtime's "infinite" due time and period (<c>Timeout.UnsignedInfinite</c>).</summary>
    private const long Infinite = uint.MaxValue;

    private const string StaticMethod = "(static method)";

    private static readonly InspectionColumn[] s_columns =
    [
        new(InspectionColumns.Target, InspectionColumnKind.Text, IsKey: true),
        new(InspectionColumns.Timers, InspectionColumnKind.Integer),
        new(InspectionColumns.Bytes, InspectionColumnKind.Bytes),
        new(InspectionColumns.Type, InspectionColumnKind.Text),
        new(InspectionColumns.TypeName, InspectionColumnKind.Text),
        new(InspectionColumns.Module, InspectionColumnKind.Text),
        new(InspectionColumns.Scope, InspectionColumnKind.Text),
    ];

    public string Name => "timer-leaks";

    public string Title => "Timer leaks";

    public string Cause => "Started periodic timers that are never stopped or disposed: the runtime's timer queue keeps each one, and the object its callback belongs to, alive.";

    public string Fix => "Dispose the timer (or stop it) when its owner is done, typically in the owner's Dispose.";

    public SnapshotCapabilities Requires => SnapshotCapabilities.FieldValues;

    public InspectionKind Kind => InspectionKind.Leak;

    public string Parameters => string.Create(CultureInfo.InvariantCulture, $"MinTimers={MinTimers};Timers=periodic,queued");

    public InspectionResult Run(InspectionContext context)
    {
        var graph = context.Graph;
        var reader = Readers.Of(context);
        var ct = context.CancellationToken;
        var reachable = Reachability.For(graph, ct);
        var targets = new Dictionary<string, Target>(StringComparer.Ordinal);
        long active = 0, canceled = 0, paused = 0, oneShot = 0, unreadable = 0;
        var owners = new HashSet<string>(StringComparer.Ordinal);
        foreach (var obj in DetectorGraph.ReachableOf(graph, reachable, DetectorGraph.TypesNamed(graph, "System.Threading.TimerQueueTimer"), context, "Reading timers"))
        {
            var address = graph.Addresses[obj];
            var (isCanceled, due, period) = (Fields.Boolean(reader, address, "_canceled"), Fields.Integer(reader, address, "_dueTime"), Fields.Integer(reader, address, "_period"));
            if (isCanceled is null || due is null || period is null)
            {
                unreadable++;
                continue;
            }

            if (isCanceled.Value)
            {
                canceled++;
                continue;
            }

            if (due == Infinite)
            {
                paused++;
                continue;
            }

            if (period == Infinite)
            {
                oneShot++;
                continue;
            }

            active++;
            var bytes = (long)graph.GetSize(obj);
            var callback = Fields.Reference(reader, address, "_timerCallback") is { } c ? Fields.DelegateTarget(reader, c.Address) : null;
            string kind;
            List<(ulong Address, string Type)?> found;
            var outerTimer = -1;
            if (callback is { Type: "System.Timers.Timer" } timer)
            {
                kind = "System.Timers.Timer";
                found = Fields.Reference(reader, timer.Address, "_onIntervalElapsed") is { } elapsed ? Fields.DelegateTargets(reader, elapsed.Address, 64, ct) : [];
                if (graph.TryGetIndex(timer.Address, out var outer))
                {
                    bytes += (long)graph.GetSize(outer);
                    outerTimer = outer;
                }
            }
            else
            {
                // A static callback or a lambda cache names no owner: the state passed to the timer does (review H4).
                kind = "System.Threading.Timer";
                found = [callback is { } own && !DetectorGraph.IsCompilerGenerated(own.Type) ? own : Fields.Reference(reader, address, "_state") ?? callback];
            }

            // Each timer once per owner type, however many handlers it has.
            owners.Clear();
            foreach (var owner in found)
            {
                var type = owner?.Type ?? StaticMethod;
                if (!owners.Add(type))
                {
                    continue;
                }

                var entry = targets.GetValueOrDefault(type) ?? new Target(kind, owner is null ? string.Empty : ModuleOf(graph, owner.Value));
                entry.Timers++;
                entry.Bytes += bytes;
                entry.Members.Add(obj);
                if (outerTimer >= 0)
                {
                    entry.Members.Add(outerTimer);
                }
                targets[type] = entry;
            }
        }

        var rows = targets
            .Where(t => t.Value.Timers >= MinTimers)
            .Select(t => new DetectorTypeRow(t.Key, t.Value.Kind, t.Value.Timers, t.Value.Bytes, t.Key == StaticMethod ? string.Empty : t.Key, t.Value.Module, [.. t.Value.Members]))
            .OrderByDescending(r => r.Objects).ThenBy(r => r.Key, StringComparer.Ordinal)
            .ToArray();
        var basis = string.Create(
            CultureInfo.InvariantCulture,
            $"{active} periodic timers in the queue, {canceled} canceled, {paused} paused or fired, {oneShot} one-shot, {unreadable} unreadable; by callback owner: {string.Join("; ", targets.OrderByDescending(t => t.Value.Timers).Take(Math.Max(10, rows.Length)).Select(t => $"{t.Key} {t.Value.Timers} ({t.Value.Kind})"))}");
        return new InspectionResult(
            rows.Length,
            rows.Sum(r => r.Bytes),
            null,
            null,
            new RowTable<DetectorTypeRow>(s_columns, rows, 120, (row, cells) =>
            {
                cells[0] = row.Key;
                cells[1] = InspectionRows.Number(row.Objects);
                cells[2] = InspectionRows.Number(row.Bytes);
                cells[3] = row.Detail;
                cells[4] = row.TypeName;
                cells[5] = row.Module;
                cells[6] = InspectionScopes.Reachable;
            },
            row => row.Members),
            basis);
    }

    private static string ModuleOf(HeapGraph graph, (ulong Address, string Type) owner) =>
        graph.TryGetIndex(owner.Address, out var index) ? DetectorGraph.Module(graph, graph.TypeIndices[index]) : string.Empty;

    private sealed class Target(string kind, string module)
    {
        public string Kind { get; } = kind;

        public string Module { get; } = module;

        public long Timers { get; set; }

        public long Bytes { get; set; }

        /// <summary>The timers counted (the queue entry and, for a System.Timers.Timer, the timer): what the row opens.</summary>
        public List<int> Members { get; } = [];
    }
}

/// <summary>
/// Disposed but referenced (sprint 2.6): at least <see cref="MinObjects"/> reachable objects of one type whose disposed
/// flag is set (the field names the .NET dispose pattern, its templates and auto-properties use; a bool, or an integer
/// set with Interlocked). Something still holds objects that are done: a list never cleared, a cache, an event.
/// </summary>
internal sealed class DisposedButReferencedDetector : IInspection
{
    public const long MinObjects = 100;

    /// <summary>Disposed flags, as the dispose pattern, its Visual Studio template, auto-properties and common code name them.</summary>
    internal static readonly string[] FlagNames =
    [
        "_disposed", "disposed", "_disposedValue", "disposedValue", "_isDisposed", "isDisposed", "m_disposed", "IsDisposed", "Disposed",
        "<IsDisposed>k__BackingField", "<Disposed>k__BackingField",
    ];

    public string Name => "disposed-but-referenced";

    public string Title => "Disposed but referenced";

    public string Cause => "Objects that were disposed are still referenced: whatever holds them (a list, a cache, an event) keeps memory alive that is no longer used.";

    public string Fix => "Remove disposed objects from the collections and events that hold them, or stop holding them once disposed.";

    public SnapshotCapabilities Requires => SnapshotCapabilities.FieldValues;

    public InspectionKind Kind => InspectionKind.Leak;

    public string Parameters => string.Create(CultureInfo.InvariantCulture, $"MinObjects={MinObjects};Flags={string.Join(",", FlagNames)}");

    public InspectionResult Run(InspectionContext context)
    {
        var graph = context.Graph;
        var reader = Readers.Of(context);
        var ct = context.CancellationToken;
        var reachable = Reachability.For(graph, ct);

        // Reachable objects per type, and each type's first one: only types with enough of them are probed (review H7).
        var perType = new long[graph.Types.Count];
        var first = new int[graph.Types.Count];
        Array.Fill(first, -1);
        var typeIndices = graph.TypeIndices.Span;
        for (var i = 0; i < typeIndices.Length; i++)
        {
            if ((i & 0xFFFF) == 0)
            {
                ct.ThrowIfCancellationRequested();
                context.Report("Counting reachable objects", i, typeIndices.Length);
            }

            if (reachable.IsReachable(i))
            {
                var type = typeIndices[i];
                if (perType[type]++ == 0)
                {
                    first[type] = i;
                }
            }
        }

        // One instance tells whether the type has a disposed flag.
        var candidates = new Dictionary<int, string>();
        var noFlag = new Dictionary<string, long>(StringComparer.Ordinal);
        long probed = 0;
        for (var t = 0; t < perType.Length; t++)
        {
            if (perType[t] < MinObjects || graph.Types.Kinds[t].HasFlag(TypeKind.Array) || graph.Types.Kinds[t].HasFlag(TypeKind.String))
            {
                continue;
            }

            if ((++probed & 0xFF) == 0)
            {
                ct.ThrowIfCancellationRequested();
            }

            if (FlagNames.FirstOrDefault(f => Fields.Flag(reader, graph.Addresses[first[t]], f) is not null) is { } flag)
            {
                candidates[t] = flag;
            }
            else
            {
                noFlag[graph.Types.GetName(t)] = perType[t];
            }
        }

        var disposed = new Dictionary<int, (long Objects, long Bytes)>();
        var disposedObjects = new Dictionary<int, List<int>>();
        var mask = DetectorGraph.TypeMask(graph, candidates.ContainsKey);
        foreach (var obj in DetectorGraph.ReachableOf(graph, reachable, mask, context, "Reading disposed flags"))
        {
            var type = graph.TypeIndices[obj];
            if (Fields.Flag(reader, graph.Addresses[obj], candidates[type]) == true)
            {
                var (objects, bytes) = disposed.GetValueOrDefault(type);
                disposed[type] = (objects + 1, bytes + (long)graph.GetSize(obj));
                if (!disposedObjects.TryGetValue(type, out var list))
                {
                    disposedObjects[type] = list = [];
                }

                list.Add(obj);
            }
        }

        var rows = disposed
            .Where(d => d.Value.Objects >= MinObjects)
            .Select(d => new DetectorTypeRow(graph.Types.GetName(d.Key), candidates[d.Key], d.Value.Objects, d.Value.Bytes, graph.Types.GetName(d.Key), DetectorGraph.Module(graph, d.Key), [.. disposedObjects[d.Key]]))
            .OrderByDescending(r => r.Objects).ThenBy(r => r.Key, StringComparer.Ordinal)
            .ToArray();
        var basis = string.Create(
            CultureInfo.InvariantCulture,
            $"{reachable.Count} reachable objects; {probed} types with {MinObjects}+ probed, {candidates.Count} with a disposed flag (most objects without one: {DetectorGraph.Top(noFlag, 5)}); disposed: {string.Join("; ", disposed.OrderByDescending(d => d.Value.Objects).Take(Math.Max(10, rows.Length)).Select(d => $"{graph.Types.GetName(d.Key)} {d.Value.Objects} by {candidates[d.Key]}"))}");
        return new InspectionResult(rows.Length, rows.Sum(r => r.Bytes), null, null, DetectorTables.Types(rows, "Flag"), basis);
    }
}

/// <summary>
/// Many HttpClient instances (sprint 2.6): reachable <c>HttpClient</c>s that own their handler, over at least
/// <see cref="MinInstances"/> distinct handlers, and so as many connection pools. Clients sharing a handler (IHttpClientFactory, or a handler passed in
/// with disposeHandler false) are the recommended pattern and are not counted (review M11).
/// </summary>
internal sealed class HttpClientInstancesDetector : IInspection
{
    public const long MinInstances = 20;

    private const string ClientType = "System.Net.Http.HttpClient";

    public string Name => "httpclient-instances";

    public string Title => "Many HttpClient instances";

    public string Cause => "Many HttpClients each own their handler: every handler has its own connection pool, so creating clients per use wastes memory and sockets.";

    public string Fix => "Create one HttpClient and reuse it, or use IHttpClientFactory (AddHttpClient) to share handlers.";

    public SnapshotCapabilities Requires => SnapshotCapabilities.FieldValues;

    public InspectionKind Kind => InspectionKind.Leak;

    public string Parameters => string.Create(CultureInfo.InvariantCulture, $"MinInstances={MinInstances};Counted=owns its handler");

    public InspectionResult Run(InspectionContext context)
    {
        var graph = context.Graph;
        var reader = Readers.Of(context);
        var reachable = Reachability.For(graph, context.CancellationToken);
        long owning = 0, sharing = 0, unreadable = 0, bytes = 0;
        var handlers = new HashSet<ulong>();
        var clients = new List<int>();
        var type = -1;
        foreach (var obj in DetectorGraph.ReachableOf(graph, reachable, DetectorGraph.TypesNamed(graph, ClientType), context, "Counting HTTP clients"))
        {
            type = graph.TypeIndices[obj];
            var address = graph.Addresses[obj];
            switch (Fields.Boolean(reader, address, "_disposeHandler"))
            {
                case true:
                    // new HttpClient(shared) owns a handler it shares: distinct handlers are the pools (re-review N2).
                    owning++;
                    clients.Add(obj);
                    bytes += (long)graph.GetSize(obj);
                    handlers.Add(Fields.Reference(reader, address, "_handler")?.Address ?? address);
                    break;
                case false:
                    sharing++;
                    break;
                default:
                    unreadable++;
                    break;
            }
        }

        DetectorTypeRow[] rows = handlers.Count >= MinInstances
            ? [new DetectorTypeRow(ClientType, InspectionRows.Number(handlers.Count), owning, bytes, ClientType, DetectorGraph.Module(graph, type), [.. clients])]
            : [];
        var basis = string.Create(CultureInfo.InvariantCulture, $"{owning} reachable HttpClients owning their handler ({handlers.Count} distinct handlers), {sharing} sharing one, {unreadable} unreadable; {Reachability.Describe(reachable)}");
        return new InspectionResult(rows.Length, rows.Sum(r => r.Bytes), null, null, DetectorTables.Types(rows, "Handlers"), basis);
    }
}

/// <summary>
/// Undisposed service scopes (sprint 2.6, ASP.NET Core): at least <see cref="MinScopes"/> reachable dependency injection
/// scopes not disposed, the root scope left out. A scope created with CreateScope and never disposed keeps its scoped and
/// disposable services.
/// </summary>
internal sealed class UndisposedServiceScopesDetector : IInspection
{
    public const long MinScopes = 500;

    public const string ScopeType = "Microsoft.Extensions.DependencyInjection.ServiceLookup.ServiceProviderEngineScope";

    public string Name => "undisposed-service-scopes";

    public string Title => "Undisposed service scopes";

    public string Cause => "Dependency injection scopes are created and never disposed: each keeps its scoped services, and every disposable it resolved, alive.";

    public string Fix => "Dispose every scope you create (using var scope = provider.CreateScope()), or use an async scope with await using.";

    public SnapshotCapabilities Requires => SnapshotCapabilities.FieldValues;

    public InspectionKind Kind => InspectionKind.Leak;

    public string Parameters => string.Create(CultureInfo.InvariantCulture, $"MinScopes={MinScopes}");

    public InspectionResult Run(InspectionContext context)
    {
        var graph = context.Graph;
        var reader = Readers.Of(context);
        var reachable = Reachability.For(graph, context.CancellationToken);
        long open = 0, closed = 0, roots = 0, unreadable = 0, bytes = 0;
        var openScopes = new List<int>();
        var type = -1;
        foreach (var obj in DetectorGraph.ReachableOf(graph, reachable, DetectorGraph.TypesNamed(graph, ScopeType), context, "Reading service scopes"))
        {
            type = graph.TypeIndices[obj];
            var address = graph.Addresses[obj];
            if (Fields.Boolean(reader, address, "<IsRootScope>k__BackingField") == true)
            {
                roots++;
                continue;
            }

            switch (Fields.Flag(reader, address, "_disposed"))
            {
                case true:
                    closed++;
                    break;
                case false:
                    open++;
                    openScopes.Add(obj);
                    bytes += (long)graph.GetSize(obj);
                    break;
                default:
                    unreadable++;
                    break;
            }
        }

        DetectorTypeRow[] rows = open >= MinScopes ? [new DetectorTypeRow(ScopeType, string.Empty, open, bytes, ScopeType, DetectorGraph.Module(graph, type), [.. openScopes])] : [];
        var note = rows.Length > 0
            ? "Scopes of requests in flight, and of Blazor Server circuits, are open by design: compare this count with the requests in flight when the dump was taken."
            : null;
        var basis = string.Create(CultureInfo.InvariantCulture, $"{open} reachable scopes not disposed, {closed} disposed, {roots} root, {unreadable} unreadable; {Reachability.Describe(reachable)}");
        return new InspectionResult(rows.Length, rows.Sum(r => r.Bytes), null, note, DetectorTables.Types(rows, null), basis);
    }
}

/// <summary>The detectors' type tables: a type, its objects and bytes, an optional detail, and what a row opens.</summary>
internal static class DetectorTables
{
    public static RowTable<DetectorTypeRow> Types(DetectorTypeRow[] rows, string? detailColumn)
    {
        InspectionColumn[] columns =
        [
            new(InspectionColumns.Type, InspectionColumnKind.Text, IsKey: true),
            new(InspectionColumns.Objects, InspectionColumnKind.Integer),
            new(InspectionColumns.Bytes, InspectionColumnKind.Bytes),
            .. detailColumn is null ? Array.Empty<InspectionColumn>() : [new InspectionColumn(detailColumn, InspectionColumnKind.Text)],
            new(InspectionColumns.TypeName, InspectionColumnKind.Text),
            new(InspectionColumns.Module, InspectionColumnKind.Text),
            new(InspectionColumns.Scope, InspectionColumnKind.Text),
        ];
        var withDetail = detailColumn is not null;
        return new RowTable<DetectorTypeRow>(columns, rows, 120, Format, row => row.Members);

        void Format(DetectorTypeRow row, string?[] cells)
        {
            var at = 0;
            cells[at++] = row.Key;
            cells[at++] = InspectionRows.Number(row.Objects);
            cells[at++] = InspectionRows.Number(row.Bytes);
            if (withDetail)
            {
                cells[at++] = row.Detail;
            }

            cells[at++] = row.TypeName;
            cells[at++] = row.Module;
            cells[at] = InspectionScopes.Reachable;
        }
    }
}
