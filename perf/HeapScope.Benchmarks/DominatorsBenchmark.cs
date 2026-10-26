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
/// Sprint 2.3: the shipped dominators path on a real index cache, as the worker runs it. Mapped: the reverse references and
/// the dominators are computed straight into sidecar files (in <c>--out</c>, never next to the cache) and mapped; in memory:
/// managed arrays. Reports each phase's time and peak private bytes, the type grouping's lower bounds, and a reopen of the
/// dominators sidecar.
/// </summary>
internal static class DominatorsBenchmark
{
    private const long BudgetBytes = 4L * 1024 * 1024 * 1024;
    private static readonly TimeSpan s_timeBudget = TimeSpan.FromSeconds(60);

    public static int Run(BenchmarkArgs args)
    {
        var cache = args.Get("cache") ?? throw new ArgumentException("--cache <snapshot.hsidx> is required.");
        var mode = args.Get("mode") == "mapped" ? MemoryMode.Mapped : MemoryMode.InMemory;
        // Only the files this run writes are deleted afterwards; the folder itself only when the run created it (review H1).
        var ownFolder = args.Get("out") is null;
        var output = args.Get("out") ?? Path.Combine(Path.GetTempPath(), "HeapScopeBench", Guid.NewGuid().ToString("N"));
        Console.WriteLine($"dominators: {cache}, {mode}, sidecars in {output}");
        var overall = Stopwatch.StartNew();
        using var overallPeak = new PrivateBytesSampler();
        var opened = IndexCacheReader.Open(cache, expected: null, mode);
        if (!opened.Success)
        {
            Console.WriteLine($"The index cache could not be opened: {opened.Detail}");
            return 2;
        }

        using var graph = opened.Graph!;
        Directory.CreateDirectory(output);
        var cap = args.GetInt("max-working-set-mb", 0);
        if (cap > 0)
        {
            // Review M14: pages over the cap are trimmed while the indices are computed (see WorkingSetCap).
            WorkingSetCap.Apply(cap);
            Measurement.Line("Working set capped", $"{cap:N0} MB");
        }

        var faultsBefore = WorkingSetCap.Counters().PageFaults;
        var fingerprint = new SnapshotFingerprint(1, 1, 1);
        var layout = IndexFileLayout.ForDominators(DominatorTree.SemanticsVersion);
        Measurement.Line("Graph", $"{graph.ObjectCount:N0} objects, {graph.EdgeCount:N0} references, {Measurement.Mb(graph.ColumnBytes)}; private after open {Measurement.Mb(PrivateAfterCollect())}");

        try
        {
            var (reverse, reverseTime, reversePeak) = Phase(() => BuildReverse(graph, mode, Path.Combine(output, "bench.rev.hsidx"), fingerprint));
            using (reverse)
            {
                Measurement.Line("Reverse references", $"{reverseTime.TotalSeconds:N1} s, {Measurement.Mb(reverse.ColumnBytes)}, peak {Measurement.Mb(reversePeak)}");
                var sidecar = Path.Combine(output, "bench.dom.hsidx");
                var ((dominators, reachable), time, peak) = Phase(() => Compute(graph, reverse, mode, sidecar, fingerprint, layout));
                using (dominators)
                {
                    Measurement.Line("Dominators and retained sizes", $"{time.TotalSeconds:N1} s, {reachable:N0} reachable, {Measurement.Mb(dominators.ColumnBytes)} kept, peak {Measurement.Mb(peak)}");
                    var (groups, groupTime, groupPeak) = Phase(() =>
                    {
                        var byType = TypeGroups.GroupOfType(graph, GroupKey.Type, out var names);
                        return GroupRetention.Compute(graph, dominators, byType, names.Length);
                    });
                    var largest = Enumerable.Range(0, groups.Length).MaxBy(t => groups[t]);
                    Measurement.Line("Retained by type (lower bounds)", $"{groupTime.TotalSeconds:N1} s, peak {Measurement.Mb(groupPeak)}; largest {graph.Types.GetName(largest)} {Measurement.Mb((long)groups[largest])}");
                }

                if (mode == MemoryMode.Mapped)
                {
                    var stopwatch = Stopwatch.StartNew();
                    var reopened = IndexCacheFile.Open(sidecar, layout, 0, fingerprint, MemoryMode.Mapped);
                    reopened.File?.Dispose();
                    Measurement.Line("Dominators sidecar reopened mapped", $"{stopwatch.Elapsed.TotalSeconds:N2} s ({(reopened.Success ? "ok" : reopened.Detail)}), {Measurement.Mb(new FileInfo(sidecar).Length)}");
                }

                var total = reverseTime + time;
                var (faults, peakWorkingSet) = WorkingSetCap.Counters();
                Measurement.Line("Page faults, peak working set", $"{faults - faultsBefore:N0} faults (soft and hard) after the open; peak working set {Measurement.Mb(peakWorkingSet)}");
                Measurement.Line("Peak private bytes, whole run", $"{Measurement.Mb(overallPeak.Peak)} (budget {Measurement.Mb(BudgetBytes)})");
                Measurement.Line("Total", $"{overall.Elapsed.TotalSeconds:N1} s; reverse references and dominators {total.TotalSeconds:N1} s (budget 60 s)");
                return overallPeak.Peak <= BudgetBytes && total <= s_timeBudget ? 0 : 1;
            }
        }
        finally
        {
            DeleteOwnFiles(output, ownFolder);
        }
    }

    private static void DeleteOwnFiles(string output, bool ownFolder)
    {
        try
        {
            foreach (var name in new[] { "bench.rev.hsidx", "bench.dom.hsidx" })
            {
                File.Delete(Path.Combine(output, name));
                foreach (var temp in Directory.EnumerateFiles(output, name + ".*.tmp"))
                {
                    File.Delete(temp);
                }
            }

            if (ownFolder)
            {
                Directory.Delete(output);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static ReverseReferences BuildReverse(HeapGraph graph, MemoryMode mode, string sidecar, SnapshotFingerprint fingerprint)
    {
        if (mode == MemoryMode.InMemory)
        {
            return ReverseReferences.Build(graph);
        }

        using (var writer = MappedIndexFileWriter.Create(sidecar, [new(IndexSection.InEdgeStarts, 4, graph.ObjectCount + 1), new(IndexSection.InEdges, 4, graph.EdgeCount)]))
        {
            ReverseReferences.Fill(graph, writer.GetSection<int>(IndexSection.InEdgeStarts), writer.GetSection<int>(IndexSection.InEdges));
            writer.Commit(fingerprint);
        }

        var file = IndexCacheFile.Open(sidecar, IndexFileLayout.ReverseReferences, 0, fingerprint, MemoryMode.Mapped).File!;
        file.TryGetSection<int>(IndexSection.InEdgeStarts, out var starts);
        file.TryGetSection<int>(IndexSection.InEdges, out var edges);
        return ReverseReferences.FromColumns(graph, starts, edges, file);
    }

    private static (Dominators Dominators, int Reachable) Compute(HeapGraph graph, ReverseReferences reverse, MemoryMode mode, string sidecar, SnapshotFingerprint fingerprint, IndexFileLayout layout)
    {
        if (mode == MemoryMode.InMemory)
        {
            var idom = GC.AllocateUninitializedArray<int>(graph.ObjectCount);
            var retained = GC.AllocateUninitializedArray<ulong>(graph.ObjectCount);
            var count = DominatorTree.Compute(graph, reverse, idom, retained);
            var starts = new int[graph.ObjectCount + 2];
            var children = new int[graph.ObjectCount];
            DominatorTree.BuildChildren(idom, retained, starts, children);
            return (Dominators.FromColumns(graph, Column<int>.FromArray(idom), Column<ulong>.FromArray(retained), Column<int>.FromArray(starts), Column<int>.FromArray(children), null), count);
        }

        int reachable;
        using (var writer = MappedIndexFileWriter.Create(
            sidecar,
            [
                new(IndexSection.ImmediateDominators, 4, graph.ObjectCount),
                new(IndexSection.RetainedSizes, 8, graph.ObjectCount),
                new(IndexSection.DominatorChildStarts, 4, graph.ObjectCount + 2),
                new(IndexSection.DominatorChildren, 4, graph.ObjectCount),
            ]))
        {
            var idom = writer.GetSection<int>(IndexSection.ImmediateDominators);
            var retained = writer.GetSection<ulong>(IndexSection.RetainedSizes);
            reachable = DominatorTree.Compute(graph, reverse, idom, retained);
            DominatorTree.BuildChildren(idom, retained, writer.GetSection<int>(IndexSection.DominatorChildStarts), writer.GetSection<int>(IndexSection.DominatorChildren));
            writer.Commit(fingerprint, 0, layout.SemanticsVersion);
        }

        var file = IndexCacheFile.Open(sidecar, layout, 0, fingerprint, MemoryMode.Mapped).File!;
        file.TryGetSection<int>(IndexSection.ImmediateDominators, out var idoms);
        file.TryGetSection<ulong>(IndexSection.RetainedSizes, out var sizes);
        file.TryGetSection<int>(IndexSection.DominatorChildStarts, out var childStarts);
        file.TryGetSection<int>(IndexSection.DominatorChildren, out var childList);
        return (Dominators.FromColumns(graph, idoms, sizes, childStarts, childList, file), reachable);
    }

    private static (T Result, TimeSpan Time, long Peak) Phase<T>(Func<T> body)
    {
        PrivateAfterCollect();
        using var sampler = new PrivateBytesSampler();
        var stopwatch = Stopwatch.StartNew();
        var result = body();
        return (result, stopwatch.Elapsed, sampler.Peak);
    }

    private static long PrivateAfterCollect()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        using var process = Process.GetCurrentProcess();
        return process.PrivateMemorySize64;
    }
}
