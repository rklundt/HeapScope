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
// through the typed reader, and logs its thresholds as parameters and what it scanned as its basis.

/// <summary>Reads the few runtime fields the detectors need, tolerating missing ones (another runtime version).</summary>
internal static class Fields
{
    public static long? Integer(IObjectReader reader, ulong address, string path) =>
        reader.ReadField(address, path) is { } value
            ? value.Kind switch
            {
                ObjectValueKind.Integer => value.Integer,
                ObjectValueKind.Unsigned => (long)value.Unsigned,
                _ => null,
            }
            : null;

    public static bool? Boolean(IObjectReader reader, ulong address, string path) =>
        reader.ReadField(address, path) is { Kind: ObjectValueKind.Boolean } value ? value.Boolean : null;

    /// <summary>The object a reference field points at: its address and runtime type; null for a null or missing field.</summary>
    public static (ulong Address, string Type)? Reference(IObjectReader reader, ulong address, string path) =>
        reader.ReadField(address, path) is { Kind: ObjectValueKind.Reference or ObjectValueKind.Array } value && value.Address != 0
            ? (value.Address, value.TypeName ?? "?")
            : null;

    /// <summary>
    /// The targets of a delegate: its own <c>_target</c>, or each one of its invocation list's (a multicast delegate).
    /// A static method's delegate has no target (null in the list).
    /// </summary>
    public static List<(ulong Address, string Type)?> DelegateTargets(IObjectReader reader, ulong address, int max, CancellationToken cancellationToken)
    {
        var targets = new List<(ulong Address, string Type)?>();
        if (Reference(reader, address, "_invocationList") is { Type: var listType } list && listType.EndsWith("[]", StringComparison.Ordinal))
        {
            var count = (int)Math.Min(Integer(reader, address, "_invocationCount") ?? max, max);
            foreach (var element in reader.ReadFields(list.Address, count, 0, cancellationToken))
            {
                if (element.Value is { Kind: ObjectValueKind.Reference, Address: not 0 } item)
                {
                    targets.Add(Reference(reader, item.Address, "_target"));
                }
            }

            return targets;
        }

        targets.Add(Reference(reader, address, "_target"));
        return targets;
    }
}

/// <summary>Type lookups by name and reachable counts per type, shared by the detectors.</summary>
internal static class DetectorGraph
{
    public static HashSet<int> TypesNamed(HeapGraph graph, Func<string, bool> match)
    {
        var types = new HashSet<int>();
        for (var t = 0; t < graph.Types.Count; t++)
        {
            if (match(graph.Types.GetName(t)))
            {
                types.Add(t);
            }
        }

        return types;
    }

    /// <summary>The reachable objects of <paramref name="types"/>, in index order.</summary>
    public static List<int> ReachableOf(HeapGraph graph, Reachability reachable, HashSet<int> types, InspectionContext context, string phase)
    {
        var found = new List<int>();
        var typeIndices = graph.TypeIndices.Span;
        for (var i = 0; i < typeIndices.Length; i++)
        {
            if ((i & 0xFFFF) == 0)
            {
                context.CancellationToken.ThrowIfCancellationRequested();
                context.Report(phase, i, typeIndices.Length);
            }

            if (types.Contains(typeIndices[i]) && reachable.IsReachable(i))
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
}

/// <summary>A row naming a type's objects: what it shows, and the hidden columns that open the type's objects.</summary>
internal sealed record DetectorTypeRow(string Key, string Detail, long Objects, long Bytes, string TypeName, string Module, string? Growth = null);

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

    public string Parameters => string.Create(CultureInfo.InvariantCulture, $"MinGrowth={MinGrowth};MinGrowthShare={MinGrowthShare:P0};MinLargeWithoutBaseline={MinLarge}");

    public InspectionResult Run(InspectionContext context)
    {
        var current = Collections(context.Graph, context.Reader!, context.CancellationToken);
        var baseline = context.Baseline is { Reader: { } reader } b ? Collections(b.Graph, reader, context.CancellationToken) : null;
        var rows = new List<Row>();
        foreach (var (field, found) in current)
        {
            if (baseline is null)
            {
                // Without growth to go on, the runtime's and the framework's own large tables are by design.
                if (found.Count >= MinLarge && !IsFramework(field))
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
        }

        var ordered = rows.OrderByDescending(r => r.Growth ?? r.Collection.Count).ThenBy(r => r.Field, StringComparer.Ordinal).ToArray();
        var note = baseline is null
            ? "No baseline snapshot: these are the application's static collections of at least 10,000 elements (the framework's own are left out); whether they grow is unknown. Choose an earlier snapshot of the same process as the baseline to see growth."
            : null;
        var basis = string.Create(
            CultureInfo.InvariantCulture,
            $"{current.Count} static collections counted{(baseline is null ? string.Empty : $", {baseline.Count} in the baseline")}; {string.Join("; ", ordered.Take(20).Select(r => $"{r.Field} {r.Collection.Type} {r.Collection.Count}{(r.Before is { } before ? $" (was {before})" : string.Empty)}"))}");
        var graph = context.Graph;
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

    private static bool IsFramework(string field) =>
        field.StartsWith("System.", StringComparison.Ordinal) || field.StartsWith("Microsoft.", StringComparison.Ordinal) || field.StartsWith("Internal.", StringComparison.Ordinal);

    /// <summary>The collections static fields hold, by "DeclaringType.field", with their element counts.</summary>
    internal static Dictionary<string, Collection> Collections(HeapGraph graph, IObjectReader reader, CancellationToken cancellationToken)
    {
        var found = new Dictionary<string, Collection>(StringComparer.Ordinal);
        foreach (var root in graph.Roots.Roots.Span)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (root.Kind != RootKind.StaticField || graph.Strings[root.DetailId] is not { Length: > 0 } field || found.ContainsKey(field))
            {
                continue;
            }

            var obj = root.ObjectIndex;
            var type = graph.Types.GetName(graph.TypeIndices[obj]);
            if (Count(graph, reader, obj, type) is { } count)
            {
                var bytes = (long)graph.GetSize(obj);
                foreach (var child in graph.GetReferences(obj))
                {
                    if (InspectionRows.IsArray(graph, child))
                    {
                        bytes += (long)graph.GetSize(child);
                    }
                }

                found[field] = new Collection(type, count, bytes, graph.Addresses[obj]);
            }
        }

        return found;
    }

    /// <summary>A collection's element count from its own fields; null for an object that is not a known collection.</summary>
    private static long? Count(HeapGraph graph, IObjectReader reader, int obj, string type)
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
                foreach (var element in reader.ReadFields(counts.Address, 4096, 0, CancellationToken.None))
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

/// <summary>
/// Event handler leaks (sprint 2.6): an event field of a publisher type holding at least <see cref="MinSubscribers"/>
/// subscribers across its publishers. A subscriber is a delegate whose target the event keeps alive; a multicast event
/// holds its subscribers in an invocation list. Delegates held by arrays or collections are not events (the growing
/// collections detector names those); timers' events are the timer detector's.
/// </summary>
internal sealed class EventHandlerLeaksDetector : IInspection
{
    public const long MinSubscribers = 1_000;

    private static readonly InspectionColumn[] s_columns =
    [
        new(InspectionColumns.Event, InspectionColumnKind.Text, IsKey: true),
        new(InspectionColumns.Publishers, InspectionColumnKind.Integer),
        new(InspectionColumns.Subscribers, InspectionColumnKind.Integer),
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

    public string Parameters => string.Create(CultureInfo.InvariantCulture, $"MinSubscribers={MinSubscribers}");

    /// <summary>Delegate types events use: EventHandler and its kin, Action and Func.</summary>
    internal static bool IsEventDelegate(string type) =>
        type.EndsWith("EventHandler", StringComparison.Ordinal)
        || type.StartsWith("System.EventHandler<", StringComparison.Ordinal)
        || type.Contains("EventHandler<", StringComparison.Ordinal)
        || type == "System.Action" || type.StartsWith("System.Action<", StringComparison.Ordinal)
        || type.StartsWith("System.Func<", StringComparison.Ordinal);

    public InspectionResult Run(InspectionContext context)
    {
        var graph = context.Graph;
        var reader = context.Reader!;
        var referrers = context.Referrers ?? throw new InvalidOperationException("The event handler detector needs the incoming references.");
        var ct = context.CancellationToken;
        var delegateTypes = DetectorGraph.TypesNamed(graph, IsEventDelegate);

        // Only delegate types with enough objects can reach the threshold: the rest are not read.
        var perType = new Dictionary<int, long>();
        var typeIndices = graph.TypeIndices.Span;
        for (var i = 0; i < typeIndices.Length; i++)
        {
            if (delegateTypes.Contains(typeIndices[i]))
            {
                perType[typeIndices[i]] = perType.GetValueOrDefault(typeIndices[i]) + 1;
            }
        }

        var candidates = perType.Where(p => p.Value >= MinSubscribers).Select(p => p.Key).ToHashSet();
        var reachable = Reachability.Compute(graph, ct);
        var events = new Dictionary<(int Publisher, string Field), Event>();
        var fieldNames = new Dictionary<(int Publisher, int Value), string?>();
        long read = 0;
        foreach (var obj in candidates.Count == 0 ? [] : DetectorGraph.ReachableOf(graph, reachable, candidates, context, "Finding event subscribers"))
        {
            var address = graph.Addresses[obj];

            // A multicast delegate holding an invocation list is the event's value, not a subscriber.
            if (Fields.Reference(reader, address, "_invocationList") is { Type: var list } && list.EndsWith("[]", StringComparison.Ordinal))
            {
                continue;
            }

            read++;
            foreach (var (value, publisher) in Holders(graph, referrers, delegateTypes, obj))
            {
                var publisherType = graph.TypeIndices[publisher];
                if (graph.Types.GetName(publisherType) == "System.Timers.Timer")
                {
                    continue;
                }

                var key = (publisherType, graph.TypeIndices[value]);
                if (!fieldNames.TryGetValue(key, out var field))
                {
                    // One read per publisher type and delegate type: its field names its events.
                    field = reader.ReadReferencePaths(graph.Addresses[publisher], 0, ct).GetValueOrDefault(graph.Addresses[value]);
                    fieldNames[key] = field;
                }

                if (field is null)
                {
                    continue;
                }

                if (!events.TryGetValue((publisherType, field), out var found))
                {
                    found = new Event();
                    events[(publisherType, field)] = found;
                }

                found.Publishers.Add(publisher);
                found.Subscribers++;
                var target = Fields.Reference(reader, address, "_target")?.Type ?? "(static method)";
                found.TargetTypes[target] = found.TargetTypes.GetValueOrDefault(target) + 1;
            }
        }

        var rows = events
            .Where(e => e.Value.Subscribers >= MinSubscribers)
            .Select(e => new DetectorTypeRow(
                graph.Types.GetName(e.Key.Publisher) + "." + e.Key.Field,
                DetectorGraph.Top(e.Value.TargetTypes),
                e.Value.Subscribers,
                e.Value.Publishers.Count,
                graph.Types.GetName(e.Key.Publisher),
                TypeGroups.ModuleKey(graph.Types.GetModule(e.Key.Publisher))))
            .OrderByDescending(r => r.Objects).ThenBy(r => r.Key, StringComparer.Ordinal)
            .ToArray();
        var basis = string.Create(
            CultureInfo.InvariantCulture,
            $"{candidates.Count} delegate types with {MinSubscribers}+ objects, {read} subscribers read, {events.Count} events; {string.Join("; ", events.OrderByDescending(e => e.Value.Subscribers).Take(10).Select(e => $"{graph.Types.GetName(e.Key.Publisher)}.{e.Key.Field} {e.Value.Subscribers} subscribers on {e.Value.Publishers.Count} publishers"))}");
        return new InspectionResult(
            rows.Length,
            rows.Sum(r => r.Objects),
            null,
            null,
            new RowTable<DetectorTypeRow>(s_columns, rows, 160, (row, cells) =>
            {
                cells[0] = row.Key;
                cells[1] = InspectionRows.Number(row.Bytes);
                cells[2] = InspectionRows.Number(row.Objects);
                cells[3] = row.Detail;
                cells[4] = row.TypeName;
                cells[5] = row.Module;
                cells[6] = "All";
            }),
            basis);
    }

    /// <summary>
    /// The events holding a subscriber delegate: the delegate itself in a publisher's field, or the multicast delegate whose
    /// invocation list holds it; each with the publisher (a referrer that is neither an array nor a delegate).
    /// </summary>
    private static IEnumerable<(int Value, int Publisher)> Holders(HeapGraph graph, ReverseReferences referrers, HashSet<int> delegateTypes, int subscriber)
    {
        foreach (var holder in referrers.GetReferrers(subscriber).ToArray())
        {
            if (InspectionRows.IsArray(graph, holder))
            {
                foreach (var multicast in referrers.GetReferrers(holder).ToArray())
                {
                    if (delegateTypes.Contains(graph.TypeIndices[multicast]))
                    {
                        foreach (var publisher in referrers.GetReferrers(multicast).ToArray())
                        {
                            if (IsPublisher(graph, delegateTypes, publisher))
                            {
                                yield return (multicast, publisher);
                            }
                        }
                    }
                }
            }
            else if (IsPublisher(graph, delegateTypes, holder))
            {
                yield return (subscriber, holder);
            }
        }
    }

    private static bool IsPublisher(HeapGraph graph, HashSet<int> delegateTypes, int obj) =>
        !InspectionRows.IsArray(graph, obj) && !delegateTypes.Contains(graph.TypeIndices[obj]);

    private sealed class Event
    {
        public HashSet<int> Publishers { get; } = [];

        public long Subscribers { get; set; }

        public Dictionary<string, long> TargetTypes { get; } = new(StringComparer.Ordinal);
    }
}

/// <summary>
/// Timer leaks (sprint 2.6): at least <see cref="MinTimers"/> active timers whose callbacks target one type. The
/// runtime's timer queue holds every started timer, so each keeps its callback's target alive until it is disposed. A
/// <c>System.Timers.Timer</c>'s callback is the timer itself: its <c>Elapsed</c> handlers' targets are counted.
/// </summary>
internal sealed class TimerLeaksDetector : IInspection
{
    public const long MinTimers = 500;

    private static readonly InspectionColumn[] s_columns =
    [
        new(InspectionColumns.Target, InspectionColumnKind.Text, IsKey: true),
        new(InspectionColumns.Timers, InspectionColumnKind.Integer),
        new(InspectionColumns.Type, InspectionColumnKind.Text),
        new(InspectionColumns.TypeName, InspectionColumnKind.Text),
        new(InspectionColumns.Module, InspectionColumnKind.Text),
        new(InspectionColumns.Scope, InspectionColumnKind.Text),
    ];

    public string Name => "timer-leaks";

    public string Title => "Timer leaks";

    public string Cause => "Started timers that are never stopped or disposed: the runtime's timer queue keeps each one, and the object its callback belongs to, alive.";

    public string Fix => "Dispose the timer (or stop it) when its owner is done, typically in the owner's Dispose.";

    public SnapshotCapabilities Requires => SnapshotCapabilities.FieldValues;

    public InspectionKind Kind => InspectionKind.Leak;

    public string Parameters => string.Create(CultureInfo.InvariantCulture, $"MinTimers={MinTimers}");

    public InspectionResult Run(InspectionContext context)
    {
        var graph = context.Graph;
        var reader = context.Reader!;
        var ct = context.CancellationToken;
        var timerTypes = DetectorGraph.TypesNamed(graph, name => name == "System.Threading.TimerQueueTimer");
        var reachable = Reachability.Compute(graph, ct);
        var targets = new Dictionary<string, (long Timers, string Kind, string? Module)>(StringComparer.Ordinal);
        long active = 0, canceled = 0;
        foreach (var obj in DetectorGraph.ReachableOf(graph, reachable, timerTypes, context, "Reading timers"))
        {
            var address = graph.Addresses[obj];
            if (Fields.Boolean(reader, address, "_canceled") == true)
            {
                canceled++;
                continue;
            }

            active++;
            var callback = Fields.Reference(reader, address, "_timerCallback._target");
            var (kind, owners) = callback is { Type: "System.Timers.Timer" } timer
                ? ("System.Timers.Timer", Fields.Reference(reader, timer.Address, "_onIntervalElapsed") is { } elapsed
                    ? Fields.DelegateTargets(reader, elapsed.Address, 64, ct)
                    : [])
                : ("System.Threading.Timer", [callback]);
            foreach (var owner in owners)
            {
                var type = owner?.Type ?? "(static method)";
                var (timers, _, module) = targets.GetValueOrDefault(type);
                targets[type] = (timers + 1, kind, module ?? ModuleOf(graph, owner));
            }
        }

        var rows = targets
            .Where(t => t.Value.Timers >= MinTimers)
            .Select(t => new DetectorTypeRow(t.Key, t.Value.Kind, t.Value.Timers, 0, t.Key, t.Value.Module ?? string.Empty))
            .OrderByDescending(r => r.Objects).ThenBy(r => r.Key, StringComparer.Ordinal)
            .ToArray();
        var basis = string.Create(
            CultureInfo.InvariantCulture,
            $"{active} active timers, {canceled} canceled; by callback target: {string.Join("; ", targets.OrderByDescending(t => t.Value.Timers).Take(10).Select(t => $"{t.Key} {t.Value.Timers} ({t.Value.Kind})"))}");
        return new InspectionResult(
            rows.Length,
            rows.Sum(r => r.Objects),
            null,
            null,
            new RowTable<DetectorTypeRow>(s_columns, rows, 120, (row, cells) =>
            {
                cells[0] = row.Key;
                cells[1] = InspectionRows.Number(row.Objects);
                cells[2] = row.Detail;
                cells[3] = row.TypeName;
                cells[4] = row.Module;
                cells[5] = "All";
            }),
            basis);
    }

    private static string? ModuleOf(HeapGraph graph, (ulong Address, string Type)? owner) =>
        owner is { } o && graph.TryGetIndex(o.Address, out var index) ? TypeGroups.ModuleKey(graph.Types.GetModule(graph.TypeIndices[index])) : null;
}

/// <summary>
/// Disposed but referenced (sprint 2.6): at least <see cref="MinObjects"/> reachable objects of one type whose disposed
/// flag is set (the field names the .NET dispose pattern and its templates use). Something still holds objects that are
/// done: a list never cleared, a cache, an event.
/// </summary>
internal sealed class DisposedButReferencedDetector : IInspection
{
    public const long MinObjects = 100;

    /// <summary>Disposed flags, as the dispose pattern, its Visual Studio template and common code name them.</summary>
    internal static readonly string[] FlagNames = ["_disposed", "disposed", "_disposedValue", "disposedValue", "_isDisposed", "isDisposed", "m_disposed", "IsDisposed", "Disposed"];

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
        var reader = context.Reader!;
        var ct = context.CancellationToken;
        var reachable = Reachability.Compute(graph, ct);

        // Reachable objects per type; only types with enough of them are probed for a flag.
        var perType = new long[graph.Types.Count];
        var typeIndices = graph.TypeIndices.Span;
        for (var i = 0; i < typeIndices.Length; i++)
        {
            if (reachable.IsReachable(i))
            {
                perType[typeIndices[i]]++;
            }
        }

        var candidates = new Dictionary<int, string>();
        long probed = 0;
        for (var t = 0; t < perType.Length; t++)
        {
            if (perType[t] < MinObjects || graph.Types.Kinds[t].HasFlag(TypeKind.Array) || graph.Types.GetName(t) == "System.String")
            {
                continue;
            }

            // One instance tells whether the type has a disposed flag.
            for (var i = 0; i < typeIndices.Length; i++)
            {
                if (typeIndices[i] == t && reachable.IsReachable(i))
                {
                    probed++;
                    if (FlagNames.FirstOrDefault(f => Fields.Boolean(reader, graph.Addresses[i], f) is not null) is { } flag)
                    {
                        candidates[t] = flag;
                    }

                    break;
                }
            }

            ct.ThrowIfCancellationRequested();
        }

        var disposed = new Dictionary<int, (long Objects, long Bytes)>();
        if (candidates.Count > 0)
        {
            foreach (var obj in DetectorGraph.ReachableOf(graph, reachable, [.. candidates.Keys], context, "Reading disposed flags"))
            {
                var type = graph.TypeIndices[obj];
                if (Fields.Boolean(reader, graph.Addresses[obj], candidates[type]) == true)
                {
                    var (objects, bytes) = disposed.GetValueOrDefault(type);
                    disposed[type] = (objects + 1, bytes + (long)graph.GetSize(obj));
                }
            }
        }

        var rows = disposed
            .Where(d => d.Value.Objects >= MinObjects)
            .Select(d => new DetectorTypeRow(graph.Types.GetName(d.Key), candidates[d.Key], d.Value.Objects, d.Value.Bytes, graph.Types.GetName(d.Key), TypeGroups.ModuleKey(graph.Types.GetModule(d.Key))))
            .OrderByDescending(r => r.Objects).ThenBy(r => r.Key, StringComparer.Ordinal)
            .ToArray();
        var basis = string.Create(
            CultureInfo.InvariantCulture,
            $"{reachable.Count} reachable objects; {probed} types with {MinObjects}+ probed, {candidates.Count} with a disposed flag; disposed: {string.Join("; ", disposed.OrderByDescending(d => d.Value.Objects).Take(10).Select(d => $"{graph.Types.GetName(d.Key)} {d.Value.Objects} by {candidates[d.Key]}"))}");
        return new InspectionResult(rows.Length, rows.Sum(r => r.Bytes), null, null, DetectorTables.Types(rows, "Flag"), basis);
    }
}

/// <summary>
/// Many HttpClient instances (sprint 2.6): at least <see cref="MinInstances"/> reachable <c>HttpClient</c>s or message
/// handlers of one type. Each handler keeps its own connection pool; creating clients per request exhausts sockets.
/// </summary>
internal sealed class HttpClientInstancesDetector : IInspection
{
    public const long MinInstances = 20;

    private static readonly string[] s_types = ["System.Net.Http.HttpClient", "System.Net.Http.HttpClientHandler", "System.Net.Http.SocketsHttpHandler"];

    public string Name => "httpclient-instances";

    public string Title => "Many HttpClient instances";

    public string Cause => "Many HttpClient or handler instances are alive: each handler has its own connection pool, so creating clients per use wastes memory and sockets.";

    public string Fix => "Create one HttpClient and reuse it, or use IHttpClientFactory (AddHttpClient) to share handlers.";

    public SnapshotCapabilities Requires => SnapshotCapabilities.ObjectGraph;

    public InspectionKind Kind => InspectionKind.Leak;

    public string Parameters => string.Create(CultureInfo.InvariantCulture, $"MinInstances={MinInstances}");

    public InspectionResult Run(InspectionContext context)
    {
        var graph = context.Graph;
        var reachable = Reachability.Compute(graph, context.CancellationToken);
        var types = DetectorGraph.TypesNamed(graph, name => s_types.Contains(name, StringComparer.Ordinal));
        var counts = new Dictionary<int, (long Objects, long Bytes)>();
        foreach (var obj in types.Count == 0 ? [] : DetectorGraph.ReachableOf(graph, reachable, types, context, "Counting HTTP clients"))
        {
            var (objects, bytes) = counts.GetValueOrDefault(graph.TypeIndices[obj]);
            counts[graph.TypeIndices[obj]] = (objects + 1, bytes + (long)graph.GetSize(obj));
        }

        var rows = counts
            .Where(c => c.Value.Objects >= MinInstances)
            .Select(c => new DetectorTypeRow(graph.Types.GetName(c.Key), string.Empty, c.Value.Objects, c.Value.Bytes, graph.Types.GetName(c.Key), TypeGroups.ModuleKey(graph.Types.GetModule(c.Key))))
            .OrderByDescending(r => r.Objects).ThenBy(r => r.Key, StringComparer.Ordinal)
            .ToArray();
        var basis = string.Join("; ", counts.Select(c => string.Create(CultureInfo.InvariantCulture, $"{graph.Types.GetName(c.Key)} {c.Value.Objects} reachable")));
        return new InspectionResult(rows.Length, rows.Sum(r => r.Bytes), null, null, DetectorTables.Types(rows, null), basis.Length == 0 ? "no HttpClient or handler objects" : basis);
    }
}

/// <summary>
/// Undisposed service scopes (sprint 2.6, ASP.NET Core): at least <see cref="MinScopes"/> reachable dependency injection
/// scopes not disposed. A scope created with CreateScope and never disposed keeps its scoped and disposable services.
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
        var reader = context.Reader!;
        var reachable = Reachability.Compute(graph, context.CancellationToken);
        var types = DetectorGraph.TypesNamed(graph, name => name == ScopeType);
        long open = 0, closed = 0, bytes = 0;
        foreach (var obj in types.Count == 0 ? [] : DetectorGraph.ReachableOf(graph, reachable, types, context, "Reading service scopes"))
        {
            if (Fields.Boolean(reader, graph.Addresses[obj], "_disposed") == true)
            {
                closed++;
            }
            else
            {
                open++;
                bytes += (long)graph.GetSize(obj);
            }
        }

        DetectorTypeRow[] rows = open >= MinScopes ? [new DetectorTypeRow(ScopeType, string.Empty, open, bytes, ScopeType, string.Empty)] : [];
        var module = types.Count > 0 ? TypeGroups.ModuleKey(graph.Types.GetModule(types.First())) : string.Empty;
        rows = [.. rows.Select(r => r with { Module = module })];
        var basis = string.Create(CultureInfo.InvariantCulture, $"{open} reachable scopes not disposed, {closed} disposed");
        return new InspectionResult(rows.Length, bytes, null, null, DetectorTables.Types(rows, null), basis);
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
        return new RowTable<DetectorTypeRow>(columns, rows, 120, (row, cells) =>
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
            cells[at] = "All";
        });
    }
}
