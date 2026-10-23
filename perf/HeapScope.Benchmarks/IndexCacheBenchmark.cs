using System.Diagnostics;
using HeapScope.Core;
using HeapScope.Core.Synthetic;
using HeapScope.Storage;

namespace HeapScope.Benchmarks;

/// <summary>
/// Writes a synthetic graph to an index cache and reopens it in both memory modes, against the budgets of
/// ARCHITECTURE.md 10: reopen in memory under 20 s, mapped under 2 s, mapped private memory under 1 GB.
/// Usage: index-cache [--objects 50000000] [--refs 3] [--dir &lt;folder&gt;] [--phase all|write|reopen].
/// <c>--phase write</c> keeps the cache; <c>--phase reopen</c> reopens it in this (fresh) process, mapped first, so the
/// mapped numbers do not follow an in-memory read of the same file.
/// </summary>
internal static class IndexCacheBenchmark
{
    private static readonly TimeSpan s_inMemoryBudget = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan s_mappedBudget = TimeSpan.FromSeconds(2);
    private const long MappedPrivateBudget = 1L * 1024 * 1024 * 1024;

    public static int Run(BenchmarkArgs args)
    {
        var objects = args.GetInt("objects", 50_000_000);
        var refs = args.GetDouble("refs", 3);
        var phase = args.Get("phase") ?? "all";
        var directory = args.Get("dir") ?? Path.Combine(Path.GetTempPath(), "HeapScopeBench");
        Directory.CreateDirectory(directory);
        var source = Path.Combine(directory, "synthetic.source");
        var cache = Path.Combine(directory, "synthetic.hsidx");
        Console.WriteLine($"index-cache: {objects:N0} objects, {refs} references per object, phase {phase}, folder {directory}");

        if (phase is "all" or "write")
        {
            Write(objects, refs, source, cache);
        }

        if (phase == "write")
        {
            return 0;
        }

        try
        {
            var fingerprint = SnapshotFingerprint.Compute(source);
            var (mappedOk, mappedTime, mappedPrivate) = Reopen(cache, fingerprint, MemoryMode.Mapped, scanEdges: true);
            var (inMemoryOk, inMemoryTime, _) = Reopen(cache, fingerprint, MemoryMode.InMemory, scanEdges: false);

            var met = mappedOk && inMemoryOk && inMemoryTime <= s_inMemoryBudget && mappedTime <= s_mappedBudget && mappedPrivate <= MappedPrivateBudget;
            Measurement.Line("Budgets (in memory 20 s, mapped 2 s, mapped private 1 GB)", met ? "met" : "MISSED");
            return met ? 0 : 1;
        }
        finally
        {
            File.Delete(cache);
            File.Delete(source);
        }
    }

    private static void Write(int objects, double refs, string source, string cache)
    {
        File.WriteAllBytes(source, new byte[4096]);
        var stopwatch = Stopwatch.StartNew();
        using var graph = SyntheticGraphs.Random(new RandomGraphOptions(objects, refs));
        Measurement.Line("Build", $"{stopwatch.Elapsed.TotalSeconds:N1} s");
        stopwatch.Restart();
        var metadata = new IndexCacheMetadata(new SnapshotInfo(SnapshotKind.FullDump, source), SnapshotCapabilities.FullDump, IndexCacheMetadata.CurrentWriterVersion);
        IndexCacheWriter.Write(cache, graph, metadata, SnapshotFingerprint.Compute(source));
        Measurement.Line("Write", $"{stopwatch.Elapsed.TotalSeconds:N1} s ({Measurement.Mb(new FileInfo(cache).Length)})");
    }

    private static (bool Ok, TimeSpan Time, long PrivateDelta) Reopen(string cache, SnapshotFingerprint fingerprint, MemoryMode mode, bool scanEdges)
    {
        var (privateBefore, workingBefore) = MemoryAfterCollect();
        var stopwatch = Stopwatch.StartNew();
        var result = IndexCacheReader.Open(cache, fingerprint, mode);
        var time = stopwatch.Elapsed;
        var (privateAfter, workingAfter) = MemoryAfterCollect();
        Measurement.Line(
            $"Reopen {mode} (verify all sections)",
            $"{time.TotalSeconds:N1} s, private +{Measurement.Mb(privateAfter - privateBefore)}, working set +{Measurement.Mb(workingAfter - workingBefore)}, {(result.Success ? "ok" : result.Detail)}");
        if (result.Graph is { } graph)
        {
            if (scanEdges)
            {
                stopwatch.Restart();
                long sum = 0;
                foreach (var edge in graph.Edges.Span)
                {
                    sum += edge;
                }

                Measurement.Line($"Scan all edges, {mode}", $"{stopwatch.Elapsed.TotalSeconds:N1} s (checksum {sum % 1000})");
            }

            graph.Dispose();
        }

        return (result.Success, time, privateAfter - privateBefore);
    }

    private static (long Private, long WorkingSet) MemoryAfterCollect()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        using var process = Process.GetCurrentProcess();
        return (process.PrivateMemorySize64, process.WorkingSet64);
    }
}
