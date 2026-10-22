using System.Diagnostics;
using System.Globalization;

namespace HeapScope.Benchmarks;

/// <summary>Process-level memory numbers for benchmark reports.</summary>
internal static class Measurement
{
    public static long ManagedHeapAfterFullGc()
    {
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        return GC.GetTotalMemory(forceFullCollection: true);
    }

    public static long PeakWorkingSet()
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        return process.PeakWorkingSet64;
    }

    public static string Mb(long bytes) => (bytes / (1024.0 * 1024.0)).ToString("N0", CultureInfo.InvariantCulture) + " MB";

    public static void Line(string label, string value) => Console.WriteLine($"  {label,-34} {value}");
}
