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

using HeapScope.Core.Graph;

namespace HeapScope.Analysis.Retention;

/// <summary>
/// Where a retention path starts (sprint 2.2, ARCHITECTURE.md 6.1): an object held by a retaining root, or a frozen object,
/// which is never collected. <see cref="IsFrozen"/> is true for a frozen object with no retaining root;
/// <see cref="Root"/> is then default.
/// </summary>
public readonly record struct PathRoot(int ObjectIndex, GcRoot Root, bool IsFrozen);

/// <summary>The rules that decide where paths start and which root explains an object (sprint 2.2).</summary>
public static class PathRoots
{
    /// <summary>Priority values: lower explains better. Frozen objects come after every root.</summary>
    public const int FrozenPriority = 8;

    /// <summary>
    /// The version of these rules (which objects start paths, in which order). Anything computed from them and kept, such
    /// as 2.3's dominators in a sidecar, records it and is rebuilt when it changes (review M2).
    /// </summary>
    public const int SemanticsVersion = 1;

    /// <summary>
    /// Every path root's object, each once, best first (review M2): objects held by a retaining root in priority order,
    /// then frozen objects. The one definition of what is alive by itself, shared by the root path tree and, in 2.3, the
    /// dominators' virtual root.
    /// </summary>
    public static IEnumerable<int> Sources(HeapGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        var seen = new System.Collections.BitArray(graph.ObjectCount);
        for (var priority = 0; priority < FrozenPriority; priority++)
        {
            for (var i = 0; i < graph.Roots.Count; i++)
            {
                var root = graph.Roots.Roots[i];
                if (root.IsRetaining && Priority(root.Kind) == priority && !seen[root.ObjectIndex])
                {
                    seen[root.ObjectIndex] = true;
                    yield return root.ObjectIndex;
                }
            }
        }

        for (var i = 0; i < graph.ObjectCount; i++)
        {
            if (graph.HeapKinds[i] == HeapKind.Frozen && !seen[i])
            {
                yield return i;
            }
        }
    }

    /// <summary>
    /// How well a root kind explains an object when several are equally close (decision 1): a static field names the code
    /// that keeps it, so it beats the runtime's storage handle for the same value; then stacks, handles, the finalizer queue
    /// and other roots.
    /// </summary>
    public static int Priority(RootKind kind) => kind switch
    {
        RootKind.StaticField => 0,
        RootKind.ThreadStack => 1,
        RootKind.StrongHandle => 2,
        RootKind.PinnedHandle => 3,
        RootKind.AsyncPinnedHandle => 4,
        RootKind.RefCountedHandle => 5,
        RootKind.FinalizerQueue => 6,
        _ => 7,
    };

    /// <summary>
    /// The root a path to <paramref name="objectIndex"/> starts from: its best retaining root, or the object itself when it
    /// is frozen; false when neither (paths pass through it to a root further away).
    /// </summary>
    public static bool TryGet(HeapGraph graph, int objectIndex, out PathRoot root)
    {
        ArgumentNullException.ThrowIfNull(graph);
        GcRoot best = default;
        var bestPriority = int.MaxValue;
        foreach (var candidate in graph.Roots.GetRoots(objectIndex))
        {
            if (candidate.IsRetaining && Priority(candidate.Kind) < bestPriority)
            {
                best = candidate;
                bestPriority = Priority(candidate.Kind);
            }
        }

        if (bestPriority != int.MaxValue)
        {
            root = new PathRoot(objectIndex, best, IsFrozen: false);
            return true;
        }

        if (graph.HeapKinds[objectIndex] == HeapKind.Frozen)
        {
            root = new PathRoot(objectIndex, default, IsFrozen: true);
            return true;
        }

        root = default;
        return false;
    }

    /// <summary>The priority of a path root (frozen objects last).</summary>
    public static int Priority(PathRoot root) => root.IsFrozen ? FrozenPriority : Priority(root.Root.Kind);

    /// <summary>
    /// A path root in plain language (sprint 2.2): what keeps the object alive and, where it helps, what usually puts it
    /// there. <paramref name="detail"/> is the root's detail string (a static field's name, a thread).
    /// </summary>
    public static string Describe(PathRoot root, string? detail)
    {
        if (root.IsFrozen)
        {
            return "Never collected: a frozen object (a string literal or a runtime type object)";
        }

        return root.Root.Kind switch
        {
            RootKind.StaticField => string.IsNullOrEmpty(detail) ? "A static field (its name is not recorded in a gcdump)" : $"Static field {detail}",
            RootKind.ThreadStack => $"Local variable or argument on {(string.IsNullOrEmpty(detail) ? "a thread's stack" : detail)}",
            RootKind.StrongHandle when detail == "static storage" => "The runtime's storage of static fields",
            RootKind.StrongHandle => "Strong GC handle (GCHandle.Alloc, or the runtime's own)",
            RootKind.PinnedHandle => "Pinned GC handle (a fixed buffer, or GCHandle.Alloc with Pinned)",
            RootKind.AsyncPinnedHandle => "Async pinned handle (a buffer of an overlapped I/O operation)",
            RootKind.RefCountedHandle => "Ref-counted handle (a COM or WinRT wrapper still referenced)",
            RootKind.FinalizerQueue => "Waiting for its finalizer to run",
            _ => string.IsNullOrEmpty(detail) ? "A runtime root" : char.ToUpperInvariant(detail[0]) + detail[1..],
        };
    }

    /// <summary>
    /// How roots are grouped in key paths (decision 3, review L8): by kind, and by detail where it names code (a static
    /// field, a handle's purpose), never by thread: a stack or a thread static held on several threads is one group.
    /// </summary>
    public static string GroupKey(PathRoot root, string? detail)
    {
        if (root.IsFrozen)
        {
            return "frozen";
        }

        if (root.Root.Kind == RootKind.ThreadStack)
        {
            return nameof(RootKind.ThreadStack);
        }

        // Thread statics are "thread static, thread 0x1a2c" (the loader's detail): the thread part is dropped.
        var thread = detail?.IndexOf(", thread ", StringComparison.Ordinal) ?? -1;
        return root.Root.Kind + "|" + (thread >= 0 ? detail![..thread] : detail);
    }

    /// <summary>The detail string of a path root, from the graph's string table (null when it has none).</summary>
    public static string? Detail(HeapGraph graph, PathRoot root)
    {
        ArgumentNullException.ThrowIfNull(graph);
        return root.IsFrozen || root.Root.DetailId < 0 || root.Root.DetailId >= graph.Strings.Count ? null : graph.Strings[root.Root.DetailId];
    }
}
