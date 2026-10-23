using HeapScope.Benchmarks;

// Benchmarks are added by the sprints that own the measured code (DEVELOPER.md section 6).
var benchmarks = new Dictionary<string, Func<BenchmarkArgs, int>>(StringComparer.OrdinalIgnoreCase)
{
    ["graph-build"] = GraphBuildBenchmark.Run,
    ["dump-stats"] = DumpStatsBenchmark.Run,
    ["stack-roots"] = StackRootsBenchmark.Run,
};

if (args.Length == 0 || !benchmarks.TryGetValue(args[0], out var run))
{
    Console.WriteLine("Usage: HeapScope.Benchmarks <benchmark> [--name value ...]");
    Console.WriteLine("Benchmarks: " + string.Join(", ", benchmarks.Keys));
    return args.Length == 0 ? 0 : 2;
}

return run(new BenchmarkArgs(args[1..]));
