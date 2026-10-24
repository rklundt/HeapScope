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
using System.Runtime;
using HeapScope.Core.Graph;
using HeapScope.Core.Synthetic;
using HeapScope.Storage;

namespace HeapScope.Benchmarks;

/// <summary>
/// Prototype of the Phase 2 indices (sprint 1.9, ARCHITECTURE.md 6.4): reverse references as a second CSR, dominators
/// from a virtual root joined to every retaining root and every frozen object (section 6.1), and retained sizes up the
/// dominator tree. Two dominator algorithms: semi-NCA (Georgiadis, the Lengauer-Tarjan family: one pass over a depth-first
/// preorder, near-linear on any graph) and Cooper-Harvey-Kennedy (iterative to a fixpoint; its pass count grows with the
/// back edges a path crosses, so a doubly linked list of n nodes needs about n passes; capped here). Each phase runs after
/// a compacting collection and reports its own peak private bytes; with both algorithms, their dominators are compared.
/// Usage: phase2-indices [--shape random|chain|backref|twoends] [--objects N] [--refs 3] [--seed 42] [--algorithm snca|chk|both]
/// [--max-passes 1000] | [--cache path.hsidx --source dump.dmp [--mapped 1]]
/// </summary>
internal static class Phase2IndexBenchmark
{
    private const long WorkerBudgetBytes = 4L * 1024 * 1024 * 1024;

    public static int Run(BenchmarkArgs args)
    {
        var algorithm = args.Get("algorithm") ?? "snca";
        var maxPasses = args.GetInt("max-passes", 1000);
        var overall = Stopwatch.StartNew();
        HeapGraph graph;
        long loadPeak;
        using (var sampler = new PrivateBytesSampler())
        {
            graph = Load(args);
            loadPeak = sampler.Peak;
        }

        using (graph)
        {
            Collect();
            var baseline = PrivateBytes();
            Console.WriteLine($"phase2-indices: {graph.ObjectCount:N0} objects, {graph.EdgeCount:N0} references, {graph.Roots.Count:N0} roots, {(args.Get("mapped") == "1" ? "mapped" : "in memory")}, {(GCSettings.IsServerGC ? "server" : "workstation")} GC");
            Measurement.Line("Graph", $"{Measurement.Mb(graph.ColumnBytes)} columns; load peak {Measurement.Mb(loadPeak)}; private after load {Measurement.Mb(baseline)}");

            var (reverse, reverseTime, reversePeak) = Phase(() => ReverseReferences(graph));
            var (inStarts, inEdges) = reverse;
            var reverseBytes = (inStarts.LongLength + inEdges.LongLength) * sizeof(int);
            Measurement.Line("Reverse references", $"{reverseTime.TotalSeconds:N1} s, {Measurement.Mb(reverseBytes)} ({(double)reverseBytes / graph.ObjectCount:N1} B/object), peak {Measurement.Mb(reversePeak)}");

            var targets = RootTargets(graph);
            int[]? idom = null;
            var worstPeak = reversePeak;
            if (algorithm is "snca" or "both")
            {
                var (result, time, peak) = Phase(() => SemiNca(graph, inStarts, inEdges, targets));
                idom = result.Idom;
                worstPeak = Math.Max(worstPeak, peak);
                Measurement.Line("Dominators, semi-NCA", $"{time.TotalSeconds:N1} s, {result.Reachable:N0} reachable, peak {Measurement.Mb(peak)}");
            }

            if (algorithm is "chk" or "both")
            {
                var (result, time, peak) = Phase(() => CooperHarveyKennedy(graph, inStarts, inEdges, targets, maxPasses));
                worstPeak = Math.Max(worstPeak, peak);
                Measurement.Line("Dominators, Cooper-Harvey-Kennedy", $"{time.TotalSeconds:N1} s, {result.Passes} passes{(result.Converged ? string.Empty : " (did not converge, capped)")}, peak {Measurement.Mb(peak)}");
                if (idom is not null && result.Converged)
                {
                    var differ = 0;
                    for (var i = 0; i < idom.Length; i++)
                    {
                        differ += idom[i] != result.Idom[i] ? 1 : 0;
                    }

                    Measurement.Line("Algorithms agree", differ == 0 ? "yes" : $"NO: {differ:N0} objects differ");
                }

                idom ??= result.Idom;
            }

            var (retained, retainedTime, retainedPeak) = Phase(() => RetainedSizes(graph, idom!));
            worstPeak = Math.Max(worstPeak, retainedPeak);
            Measurement.Line("Retained sizes", $"{retainedTime.TotalSeconds:N1} s, the roots retain {Measurement.Mb((long)retained[graph.ObjectCount])} of {Measurement.Mb((long)graph.TotalSize)}, peak {Measurement.Mb(retainedPeak)}");

            // What the 2.x design keeps per object (ARCHITECTURE.md 6.4): reverse CSR, immediate dominator, retained size.
            var keptBytes = reverseBytes + (idom!.LongLength * sizeof(int)) + (retained.LongLength * sizeof(ulong));
            Measurement.Line("Kept index bytes", $"{Measurement.Mb(keptBytes)} ({(double)keptBytes / graph.ObjectCount:N1} B/object); graph plus indices {Measurement.Mb(graph.ColumnBytes + keptBytes)}");
            Measurement.Line("Peak private bytes, index phases", $"{Measurement.Mb(worstPeak)} (budget {Measurement.Mb(WorkerBudgetBytes)})");
            Measurement.Line("Total", $"{overall.Elapsed.TotalSeconds:N1} s");
            GC.KeepAlive(inStarts);
            GC.KeepAlive(inEdges);
            GC.KeepAlive(idom);
            GC.KeepAlive(retained);
            return worstPeak <= WorkerBudgetBytes ? 0 : 1;
        }
    }

    private static HeapGraph Load(BenchmarkArgs args)
    {
        if (args.Get("cache") is { } cache)
        {
            var source = args.Get("source") ?? throw new ArgumentException("--cache needs --source, the dump the cache was built from.");
            var mode = args.Get("mapped") == "1" ? MemoryMode.Mapped : MemoryMode.InMemory;
            var result = IndexCacheReader.Open(cache, SnapshotFingerprint.Compute(source), mode);
            return result.Success ? result.Graph! : throw new InvalidOperationException(result.Detail);
        }

        var objects = args.GetInt("objects", 50_000_000);
        return (args.Get("shape") ?? "random") switch
        {
            "random" => SyntheticGraphs.Random(new RandomGraphOptions(objects, args.GetDouble("refs", 3), Seed: (ulong)args.GetInt("seed", 42))),
            "chain" => Chain(objects, backToHead: false),
            "backref" => Chain(objects, backToHead: true),
            "twoends" => Chain(objects, backToHead: false, rootTail: true),
            var shape => throw new ArgumentException($"Unknown shape {shape}: random, chain or backref."),
        };
    }

    /// <summary>
    /// A list owner and <paramref name="length"/> nodes: doubly linked (LinkedList, timer queues), or singly linked with
    /// every node pointing back to the head. Both make a pass of Cooper-Harvey-Kennedy settle about one more node.
    /// </summary>
    private static HeapGraph Chain(int length, bool backToHead, bool rootTail = false)
    {
        var builder = new SyntheticGraphBuilder();
        var owner = builder.Object("Synthetic.List");
        var nodes = new SyntheticObject[length];
        for (var i = 0; i < length; i++)
        {
            nodes[i] = builder.Object("Synthetic.ListNode");
        }

        builder.Reference(owner, nodes[0]);
        for (var i = 0; i < length; i++)
        {
            if (i + 1 < length)
            {
                builder.Reference(nodes[i], nodes[i + 1]);
            }

            if (backToHead)
            {
                builder.Reference(nodes[i], nodes[0]);
            }
            else if (i > 0)
            {
                builder.Reference(nodes[i], nodes[i - 1]);
            }
        }

        builder.Root(owner, RootKind.StaticField, "Synthetic.List.s_list");
        if (rootTail)
        {
            // Held from both ends (a deque, a circular list): no node but the owner's dominates the middle.
            var tailOwner = builder.Object("Synthetic.Tail");
            builder.Reference(tailOwner, nodes[^1]);
            builder.Root(tailOwner, RootKind.StaticField, "Synthetic.List.s_tail");
        }
        return builder.Build();
    }

    private static (T Result, TimeSpan Time, long Peak) Phase<T>(Func<T> body)
    {
        Collect();
        using var sampler = new PrivateBytesSampler();
        var stopwatch = Stopwatch.StartNew();
        var result = body();
        return (result, stopwatch.Elapsed, sampler.Peak);
    }

    private static void Collect()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
    }

    private static long PrivateBytes()
    {
        using var process = Process.GetCurrentProcess();
        return process.PrivateMemorySize64;
    }

    /// <summary>The second CSR: for each object, the objects that reference it (in-degree count, then fill).</summary>
    internal static (int[] Starts, int[] Sources) ReverseReferences(HeapGraph graph)
    {
        var n = graph.ObjectCount;
        var starts = new int[n + 1];
        var edges = graph.Edges.Span;
        foreach (var target in edges)
        {
            starts[target + 1]++;
        }

        for (var i = 0; i < n; i++)
        {
            starts[i + 1] += starts[i];
        }

        var sources = GC.AllocateUninitializedArray<int>(edges.Length);
        var fill = GC.AllocateUninitializedArray<int>(n);
        starts.AsSpan(0, n).CopyTo(fill);
        var edgeStarts = graph.EdgeStarts.Span;
        for (var source = 0; source < n; source++)
        {
            for (var e = edgeStarts[source]; e < edgeStarts[source + 1]; e++)
            {
                sources[fill[edges[e]]++] = source;
            }
        }

        return (starts, sources);
    }

    /// <summary>The virtual root's successors: objects of retaining roots and frozen objects (section 6.1), each once.</summary>
    internal static int[] RootTargets(HeapGraph graph)
    {
        var seen = new bool[graph.ObjectCount];
        var targets = new List<int>();
        foreach (var root in graph.Roots.Roots.Span)
        {
            if (root.IsRetaining && !seen[root.ObjectIndex])
            {
                seen[root.ObjectIndex] = true;
                targets.Add(root.ObjectIndex);
            }
        }

        var kinds = graph.HeapKinds.Span;
        for (var i = 0; i < kinds.Length; i++)
        {
            if (kinds[i] == HeapKind.Frozen && !seen[i])
            {
                seen[i] = true;
                targets.Add(i);
            }
        }

        return [.. targets];
    }

    /// <summary>
    /// Semi-NCA: semidominators from one reverse preorder pass with path-compressed evaluation, then each immediate
    /// dominator as the nearest common ancestor of its parent and semidominator. Works in preorder numbers (the virtual
    /// root is 0); returns immediate dominators by object index (the virtual root is index n), -1 when unreachable.
    /// </summary>
    internal static (int[] Idom, int Reachable) SemiNca(HeapGraph graph, int[] inStarts, int[] inEdges, int[] rootTargets)
    {
        var n = graph.ObjectCount;
        var (vertex, number, parent, count) = Preorder(graph, rootTargets);
        var isRootTarget = new bool[n];
        foreach (var t in rootTargets)
        {
            isRootTarget[t] = true;
        }

        var semi = new int[count];
        var label = new int[count];
        var ancestor = new int[count];
        for (var i = 0; i < count; i++)
        {
            semi[i] = i;
            label[i] = i;
            ancestor[i] = -1;
        }

        var path = new List<int>();
        for (var w = count - 1; w > 0; w--)
        {
            var node = vertex[w];
            if (isRootTarget[node])
            {
                semi[w] = 0;
            }

            for (var e = inStarts[node]; e < inStarts[node + 1]; e++)
            {
                var v = number[inEdges[e]];
                if (v < 0)
                {
                    continue; // an unreachable referrer
                }

                var u = Eval(v, ancestor, label, semi, path);
                if (semi[u] < semi[w])
                {
                    semi[w] = semi[u];
                }
            }

            ancestor[w] = parent[w];
        }

        // Nearest common ancestor on the depth-first tree, in preorder: an ancestor has a smaller number.
        var idomByNumber = parent;
        for (var w = 1; w < count; w++)
        {
            while (idomByNumber[w] > semi[w])
            {
                idomByNumber[w] = idomByNumber[idomByNumber[w]];
            }
        }

        var idom = new int[n + 1];
        Array.Fill(idom, -1);
        idom[n] = n;
        for (var w = 1; w < count; w++)
        {
            var dominator = idomByNumber[w];
            idom[vertex[w]] = dominator == 0 ? n : vertex[dominator];
        }

        return (idom, count - 1);
    }

    // The label with the smallest semidominator on v's path to the forest root, compressing the path as it goes.
    private static int Eval(int v, int[] ancestor, int[] label, int[] semi, List<int> path)
    {
        if (ancestor[v] < 0)
        {
            return v;
        }

        path.Clear();
        var x = v;
        while (ancestor[ancestor[x]] >= 0)
        {
            path.Add(x);
            x = ancestor[x];
        }

        for (var i = path.Count - 1; i >= 0; i--)
        {
            var y = path[i];
            var a = ancestor[y];
            if (semi[label[a]] < semi[label[y]])
            {
                label[y] = label[a];
            }

            ancestor[y] = ancestor[a];
        }

        return label[v];
    }

    /// <summary>Depth-first preorder from the virtual root: vertex by number, number by object (-1 unreachable), parents.</summary>
    private static (int[] Vertex, int[] Number, int[] Parent, int Count) Preorder(HeapGraph graph, int[] rootTargets)
    {
        var n = graph.ObjectCount;
        var number = new int[n];
        Array.Fill(number, -1);
        var vertex = GC.AllocateUninitializedArray<int>(n + 1);
        var parent = GC.AllocateUninitializedArray<int>(n + 1);
        var stack = new Stack<(int Node, int Next)>();
        var edgeStarts = graph.EdgeStarts.Span;
        var edges = graph.Edges.Span;
        vertex[0] = n;
        parent[0] = 0;
        var count = 1;
        stack.Push((n, 0));
        while (stack.Count > 0)
        {
            var (node, next) = stack.Pop();
            var parentNumber = node == n ? 0 : number[node];
            var end = node == n ? rootTargets.Length : edgeStarts[node + 1] - edgeStarts[node];
            while (next < end)
            {
                var child = node == n ? rootTargets[next] : edges[edgeStarts[node] + next];
                next++;
                if (number[child] < 0)
                {
                    number[child] = count;
                    vertex[count] = child;
                    parent[count] = parentNumber;
                    count++;
                    stack.Push((node, next));
                    stack.Push((child, 0));
                    break;
                }
            }
        }

        return (vertex, number, parent, count);
    }

    /// <summary>
    /// Cooper, Harvey and Kennedy, "A Simple, Fast Dominance Algorithm": iterate in reverse postorder to a fixpoint, at
    /// most <paramref name="maxPasses"/> passes.
    /// </summary>
    internal static (int[] Idom, int Passes, bool Converged) CooperHarveyKennedy(HeapGraph graph, int[] inStarts, int[] inEdges, int[] rootTargets, int maxPasses)
    {
        var n = graph.ObjectCount;
        var (vertex, number, _, count) = Preorder(graph, rootTargets);
        var isRootTarget = new bool[n];
        foreach (var t in rootTargets)
        {
            isRootTarget[t] = true;
        }

        // Postorder numbers from a second walk would match the paper exactly; reverse postorder is replaced here by
        // preorder, which also visits every node after its depth-first parent, and the intersect uses preorder numbers
        // (an ancestor has a smaller preorder number).
        var idom = new int[count];
        Array.Fill(idom, -1);
        idom[0] = 0;
        var passes = 0;
        bool changed;
        do
        {
            changed = false;
            passes++;
            for (var w = 1; w < count; w++)
            {
                var node = vertex[w];
                var newIdom = isRootTarget[node] ? 0 : -1;
                for (var e = inStarts[node]; e < inStarts[node + 1]; e++)
                {
                    var p = number[inEdges[e]];
                    if (p < 0 || idom[p] < 0)
                    {
                        continue;
                    }

                    newIdom = newIdom < 0 ? p : Intersect(p, newIdom, idom);
                }

                if (newIdom >= 0 && idom[w] != newIdom)
                {
                    idom[w] = newIdom;
                    changed = true;
                }
            }
        }
        while (changed && passes < maxPasses);

        var result = new int[n + 1];
        Array.Fill(result, -1);
        result[n] = n;
        for (var w = 1; w < count; w++)
        {
            result[vertex[w]] = idom[w] == 0 ? n : vertex[idom[w]];
        }

        return (result, passes, !changed);
    }

    private static int Intersect(int a, int b, int[] idom)
    {
        while (a != b)
        {
            while (a > b)
            {
                a = idom[a];
            }

            while (b > a)
            {
                b = idom[b];
            }
        }

        return a;
    }

    /// <summary>
    /// Each object's retained size: its own size plus what it dominates. A dominator is a depth-first ancestor, so summing
    /// in reverse preorder adds every object into its dominator after everything it dominates.
    /// </summary>
    internal static ulong[] RetainedSizes(HeapGraph graph, int[] idom)
    {
        var n = graph.ObjectCount;
        var (vertex, _, _, count) = Preorder(graph, RootTargets(graph));
        var retained = new ulong[n + 1];
        for (var w = count - 1; w > 0; w--)
        {
            var node = vertex[w];
            retained[node] += graph.GetSize(node);
            retained[idom[node]] += retained[node];
        }

        return retained;
    }
}
