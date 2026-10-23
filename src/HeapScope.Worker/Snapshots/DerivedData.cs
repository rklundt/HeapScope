using HeapScope.Analysis;

namespace HeapScope.Worker.Snapshots;

/// <summary>
/// Data computed from a loaded snapshot on first use and dropped with it in one step when the snapshot closes
/// (sprint 1.3: the type aggregation; later reverse references, dominators and detector results get their slots here).
/// Each slot holds one shared computation that runs for the snapshot's lifetime, not for the caller that started it.
/// </summary>
internal sealed class DerivedData
{
    private readonly Lock _lock = new();

    // Canceled on close, never disposed: a computation still running may register on its token.
    private readonly CancellationTokenSource _lifetime = new();
    private Task<TypeAggregation>? _aggregation;

    /// <summary>The running or finished aggregation; a failed or canceled one is started again.</summary>
    public Task<TypeAggregation> GetOrStartAggregation(Func<CancellationToken, Task<TypeAggregation>> compute)
    {
        lock (_lock)
        {
            if (_aggregation is null || _aggregation.IsFaulted || (_aggregation.IsCanceled && !_lifetime.IsCancellationRequested))
            {
                _aggregation = compute(_lifetime.Token);
            }

            return _aggregation;
        }
    }

    /// <summary>Stops running computations and drops the results.</summary>
    public void Close()
    {
        lock (_lock)
        {
            _lifetime.Cancel();
            _aggregation = null;
        }
    }
}
