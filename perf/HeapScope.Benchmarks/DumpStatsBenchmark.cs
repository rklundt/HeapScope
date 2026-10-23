using System.Diagnostics;
using Microsoft.Diagnostics.Runtime;

namespace HeapScope.Benchmarks;

/// <summary>
/// Walks a dump with ClrMD and reports counts, heap size, time and memory: the raw cost the sprint 1.2 loader builds
/// on. Memory is reported as peak private bytes (committed memory, the budget's metric) and peak working set.
/// Usage: dump-stats --dump &lt;path&gt; [--by-address 1] [--cache-mb N].
/// <c>--by-address 1</c> runs the reference pass the way the loader will (ARCHITECTURE.md 6.3): objects sorted by
/// address, each read with <c>GetObject</c>. <c>--cache-mb N</c> caps ClrMD's dump cache.
/// </summary>
internal static class DumpStatsBenchmark
{
    public static int Run(BenchmarkArgs args)
    {
        var path = args.Get("dump") ?? throw new ArgumentException("--dump <path> is required.");
        var byAddress = args.GetInt("by-address", 0) != 0;
        var cacheMb = args.GetInt("cache-mb", 0);
        Console.WriteLine($"dump-stats: {path} ({new FileInfo(path).Length / (1024.0 * 1024.0):N0} MB), references {(byAddress ? "by address" : "in heap order")}, ClrMD cache {(cacheMb > 0 ? cacheMb + " MB" : "default")}");

        using var sampler = new PrivateBytesSampler();
        var stopwatch = Stopwatch.StartNew();
        var options = new DataTargetOptions();
        if (cacheMb > 0)
        {
            options.CacheOptions.MaxDumpCacheSize = cacheMb * 1024L * 1024L;
        }

        using var target = DataTarget.LoadDump(path, options);
        using var runtime = target.ClrVersions[0].CreateRuntime();
        var heap = runtime.Heap;
        var load = stopwatch.Elapsed;

        long objects = 0, free = 0;
        ulong totalSize = 0;
        var addresses = byAddress ? new List<ulong>() : null;
        foreach (var obj in heap.EnumerateObjects())
        {
            if (obj.IsFree)
            {
                free++;
                continue;
            }

            objects++;
            totalSize += obj.Size;
            addresses?.Add(obj.Address);
        }

        var objectWalk = stopwatch.Elapsed - load;
        long references = 0;
        if (addresses is not null)
        {
            addresses.Sort();
            foreach (var address in addresses)
            {
                foreach (var _ in heap.GetObject(address).EnumerateReferenceAddresses(carefully: false, considerDependantHandles: false))
                {
                    references++;
                }
            }
        }
        else
        {
            foreach (var obj in heap.EnumerateObjects())
            {
                if (!obj.IsFree)
                {
                    foreach (var _ in obj.EnumerateReferenceAddresses(carefully: false, considerDependantHandles: false))
                    {
                        references++;
                    }
                }
            }
        }

        stopwatch.Stop();
        var referenceWalk = stopwatch.Elapsed - load - objectWalk;
        Measurement.Line("Runtime", runtime.ClrInfo.Version.ToString());
        Measurement.Line("Objects (free excluded)", $"{objects:N0} ({free:N0} free)");
        Measurement.Line("Managed heap (live + dead)", Measurement.Mb((long)totalSize));
        Measurement.Line("References", $"{references:N0} ({(double)references / Math.Max(objects, 1):N2} per object)");
        Measurement.Line("Load (dump and runtime)", $"{load.TotalSeconds:N1} s");
        Measurement.Line("Object pass", $"{objectWalk.TotalSeconds:N1} s");
        Measurement.Line("Reference pass", $"{referenceWalk.TotalSeconds:N1} s");
        Measurement.Line("Total", $"{stopwatch.Elapsed.TotalSeconds:N1} s");
        Measurement.Line("Peak private bytes", Measurement.Mb(sampler.Peak));
        Measurement.Line("Peak working set", Measurement.Mb(Measurement.PeakWorkingSet()));
        return 0;
    }

    /// <summary>Samples the process's private bytes every 100 ms and keeps the peak.</summary>
    private sealed class PrivateBytesSampler : IDisposable
    {
        private readonly Timer _timer;
        private long _peak;

        public PrivateBytesSampler() => _timer = new Timer(_ => Sample(), null, TimeSpan.Zero, TimeSpan.FromMilliseconds(100));

        public long Peak
        {
            get
            {
                Sample();
                return Interlocked.Read(ref _peak);
            }
        }

        public void Dispose() => _timer.Dispose();

        private void Sample()
        {
            using var process = Process.GetCurrentProcess();
            var value = process.PrivateMemorySize64;
            long current;
            while (value > (current = Interlocked.Read(ref _peak)) && Interlocked.CompareExchange(ref _peak, value, current) != current)
            {
            }
        }
    }
}
