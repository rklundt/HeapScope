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
using HeapScope.Analysis;
using HeapScope.Analysis.Retention;
using HeapScope.Core.Graph;
using HeapScope.Storage;

namespace HeapScope.Benchmarks;

/// <summary>
/// Retention paths on a real index cache (sprint 2.2): the root path tree (time, private bytes), the key paths of the types
/// with the most objects, and the backward search of an object of each. Budget: the tree within the 60 s Phase 2 index
/// budget, a type's key paths and an object's search interactive (under 5 s).
/// Usage: retention --cache &lt;snapshot.hsidx&gt; [--mode memory|mapped] [--types 3].
/// </summary>
internal static class RetentionBenchmark
{
    private static readonly TimeSpan s_treeBudget = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan s_queryBudget = TimeSpan.FromSeconds(5);

    public static int Run(BenchmarkArgs args)
    {
        var cache = args.Get("cache") ?? throw new ArgumentException("--cache <snapshot.hsidx> is required.");
        var mode = args.Get("mode") == "mapped" ? MemoryMode.Mapped : MemoryMode.InMemory;
        var typeCount = args.GetInt("types", 3);
        Console.WriteLine($"retention: {cache}, graph {mode}");

        using var sampler = new PrivateBytesSampler();
        var opened = IndexCacheReader.Open(cache, IndexCacheSigner.None, expected: null, mode);
        if (!opened.Success)
        {
            Console.WriteLine($"The index cache could not be opened: {opened.Detail}");
            return 2;
        }

        using var graph = opened.Graph!;
        Measurement.Line("Graph", $"{graph.ObjectCount:N0} objects, {graph.EdgeCount:N0} references, {graph.DependentHandles.Length:N0} dependent handles");
        var before = PrivateAfterCollect();
        var stopwatch = Stopwatch.StartNew();
        var tree = RootPathTree.Build(graph);
        var treeTime = stopwatch.Elapsed;
        Measurement.Line("Root path tree", $"{treeTime.TotalSeconds:N1} s, {tree.ReachableCount:N0} reachable, {Measurement.Mb(tree.ColumnBytes)}, private +{Measurement.Mb(PrivateAfterCollect() - before)}");

        stopwatch.Restart();
        using var reverse = ReverseReferences.Build(graph);
        Measurement.Line("Reverse references (for the object searches)", $"{stopwatch.Elapsed.TotalSeconds:N1} s");

        var aggregation = TypeAggregation.Compute(graph);
        var slowest = TimeSpan.Zero;
        foreach (var type in Enumerable.Range(0, graph.Types.Count).OrderByDescending(t => aggregation.Counts[t]).Take(typeCount))
        {
            var name = graph.Types.GetName(type);
            var types = TypeGroups.MatchTypes(graph, name, TypeGroups.ModuleKey(graph.Types.GetModule(type)), out _);
            stopwatch.Restart();
            var paths = KeyPaths.ForTypes(graph, tree, types);
            var keyTime = stopwatch.Elapsed;
            Measurement.Line($"Key paths of {name}", $"{keyTime.TotalSeconds:N1} s: {paths.Objects:N0} objects, {paths.Paths.Count + paths.OtherPaths:N0} paths, {paths.Unreachable:N0} unreachable");

            var example = paths.Paths.Count > 0 ? paths.Paths[0].ExampleObject : -1;
            if (example >= 0)
            {
                stopwatch.Restart();
                var search = RetentionSearch.Find(graph, reverse, example, paths: tree);
                var searchTime = stopwatch.Elapsed;
                Measurement.Line("  one object's paths", $"{searchTime.TotalMilliseconds:N0} ms: {search.Roots.Count} roots, {search.Visited:N0} visited, {search.Nodes.Count:N0} nodes{(search.StoppedEarly ? ", stopped early" : string.Empty)}");
                slowest = searchTime > slowest ? searchTime : slowest;
            }

            slowest = keyTime > slowest ? keyTime : slowest;
        }

        // Review M7: the worst case before H2 was an unreachable object behind a large garbage cone; with the search pruned to
        // reachable objects it is answered at once.
        var unreachable = Enumerable.Range(0, graph.ObjectCount).FirstOrDefault(o => !tree.IsReachable(o), -1);
        if (unreachable >= 0)
        {
            stopwatch.Restart();
            var search = RetentionSearch.Find(graph, reverse, unreachable, paths: tree);
            Measurement.Line("An unreachable object's paths", $"{stopwatch.Elapsed.TotalMilliseconds:N0} ms: {search.Visited:N0} visited");
        }

        Measurement.Line("Peak private bytes", Measurement.Mb(sampler.Peak));
        var met = treeTime <= s_treeBudget && slowest <= s_queryBudget;
        Measurement.Line("Budgets (tree 60 s, queries 5 s)", met ? "met" : "MISSED");
        return met ? 0 : 1;
    }

    private static long PrivateAfterCollect()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        using var process = Process.GetCurrentProcess();
        return process.PrivateMemorySize64;
    }
}
