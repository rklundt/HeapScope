using System.Diagnostics;

namespace HeapScope.Benchmarks;

/// <summary>Samples the process's private bytes every 100 ms and keeps the peak.</summary>
internal sealed class PrivateBytesSampler : IDisposable
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
