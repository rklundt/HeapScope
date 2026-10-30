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
