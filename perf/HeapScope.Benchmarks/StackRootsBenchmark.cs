using Microsoft.Diagnostics.Runtime;

namespace HeapScope.Benchmarks;

/// <summary>
/// Diagnostic: prints every managed thread's frames and the stack roots ClrMD reports for them.
/// Usage: stack-roots --dump &lt;path&gt;.
/// </summary>
internal static class StackRootsBenchmark
{
    public static int Run(BenchmarkArgs args)
    {
        var path = args.Get("dump") ?? throw new ArgumentException("--dump <path> is required.");
        using var target = DataTarget.LoadDump(path);
        using var runtime = target.ClrVersions[0].CreateRuntime();
        foreach (var thread in runtime.Threads)
        {
            Console.WriteLine($"thread {thread.ManagedThreadId}");
            foreach (var root in thread.EnumerateStackRoots())
            {
                Console.WriteLine($"  root {root.Object.Type?.Name} interior={root.IsInterior} frame={root.StackFrame?.Method?.Name ?? root.StackFrame?.Kind.ToString()}");
            }
        }

        return 0;
    }
}
