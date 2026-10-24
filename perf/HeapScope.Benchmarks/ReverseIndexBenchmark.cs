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
using HeapScope.Core.Graph;
using HeapScope.Storage;

namespace HeapScope.Benchmarks;

/// <summary>
/// The reverse references index of a real snapshot (sprint 2.1): opens an existing index cache in a memory mode, builds the
/// reverse CSR, writes it as a sidecar and reopens the sidecar in both modes, with times and private bytes. Budget (ARCHITECTURE
/// 10): built well inside the 60 s Phase 2 index budget, the process peak under the 4 GB first-load budget.
/// Usage: reverse-index --cache &lt;snapshot.hsidx&gt; [--mode memory|mapped] [--dir &lt;folder for the sidecar&gt;].
/// </summary>
internal static class ReverseIndexBenchmark
{
    private static readonly TimeSpan s_buildBudget = TimeSpan.FromSeconds(60);
    private const long PeakBudget = 4L * 1024 * 1024 * 1024;

    public static int Run(BenchmarkArgs args)
    {
        var cache = args.Get("cache") ?? throw new ArgumentException("--cache <snapshot.hsidx> is required.");
        var mode = args.Get("mode") == "mapped" ? MemoryMode.Mapped : MemoryMode.InMemory;
        var directory = args.Get("dir") ?? Path.Combine(Path.GetTempPath(), "HeapScopeBench");
        Directory.CreateDirectory(directory);
        var sidecar = Path.Combine(directory, "benchmark.rev.hsidx");
        Console.WriteLine($"reverse-index: {cache}, graph {mode}, sidecar in {directory}");

        using var sampler = new PrivateBytesSampler();
        var stopwatch = Stopwatch.StartNew();
        var opened = IndexCacheReader.Open(cache, expected: null, mode);
        if (!opened.Success)
        {
            Console.WriteLine($"The index cache could not be opened: {opened.Detail}");
            return 2;
        }

        using var graph = opened.Graph!;
        Measurement.Line("Open the graph", $"{stopwatch.Elapsed.TotalSeconds:N1} s, {graph.ObjectCount:N0} objects, {graph.EdgeCount:N0} references");
        var (beforeBuild, _) = MemoryAfterCollect();

        stopwatch.Restart();
        ReverseReferences? reverse = ReverseReferences.Build(graph);
        var buildTime = stopwatch.Elapsed;
        var (afterBuild, _) = MemoryAfterCollect();
        Measurement.Line("Build", $"{buildTime.TotalSeconds:N1} s, index {Measurement.Mb(reverse.ColumnBytes)}, private +{Measurement.Mb(afterBuild - beforeBuild)}");

        var reopened = true;
        try
        {
            stopwatch.Restart();
            using (var writer = IndexCacheFileWriter.Create(sidecar))
            {
                writer.WriteSection(IndexSection.InEdgeStarts, reverse.InEdgeStarts.Span);
                writer.WriteSection(IndexSection.InEdges, reverse.InEdges.Span);
                writer.Commit(new SnapshotFingerprint(1, 2, 3), opened.Identity);
            }

            Measurement.Line("Write the sidecar", $"{stopwatch.Elapsed.TotalSeconds:N1} s ({Measurement.Mb(new FileInfo(sidecar).Length)})");

            // The worker's switch to a mapped index: the built arrays are released by a non-compacting gen2 collection,
            // which pauses every other query in the worker (review M11).
            stopwatch.Restart();
            reverse = null;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: false);
            Measurement.Line("Release the built index (pause)", $"{stopwatch.Elapsed.TotalMilliseconds:N0} ms with the graph {mode}");

            foreach (var reopen in new[] { MemoryMode.Mapped, MemoryMode.InMemory })
            {
                var (before, _) = MemoryAfterCollect();
                stopwatch.Restart();
                var file = IndexCacheFile.Open(sidecar, IndexFileLayout.ReverseReferences, opened.Identity, null, reopen);
                var time = stopwatch.Elapsed;
                var (after, _) = MemoryAfterCollect();
                Measurement.Line($"Reopen the sidecar {reopen}", $"{time.TotalSeconds:N1} s, private +{Measurement.Mb(after - before)}, {(file.Success ? "ok" : file.Detail)}");
                reopened &= file.Success;
                file.File?.Dispose();
            }
        }
        finally
        {
            File.Delete(sidecar);
        }

        var peak = sampler.Peak;
        Measurement.Line("Peak private bytes", Measurement.Mb(peak));
        var met = reopened && buildTime <= s_buildBudget && peak <= PeakBudget;
        Measurement.Line("Budgets (build 60 s, peak 4 GB)", met ? "met" : "MISSED");
        return met ? 0 : 1;
    }

    private static (long Private, long WorkingSet) MemoryAfterCollect()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        using var process = Process.GetCurrentProcess();
        return (process.PrivateMemorySize64, process.WorkingSet64);
    }
}
