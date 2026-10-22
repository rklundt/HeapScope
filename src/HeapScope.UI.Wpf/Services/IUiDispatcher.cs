using System.Windows.Threading;

namespace HeapScope.UI.Wpf.Services;

/// <summary>
/// Runs work on the UI thread (ARCHITECTURE.md 8). Application raises events on any thread; view models marshal
/// through this abstraction, which tests replace with an immediate implementation.
/// </summary>
public interface IUiDispatcher
{
    /// <summary>Queues <paramref name="action"/> on the UI thread, or runs it now when already there.</summary>
    void Post(Action action);
}

/// <summary>The WPF dispatcher of the application.</summary>
public sealed class WpfUiDispatcher(Dispatcher dispatcher) : IUiDispatcher
{
    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.BeginInvoke(action, DispatcherPriority.Normal);
        }
    }
}

/// <summary>Runs actions immediately on the calling thread (tests, design time).</summary>
public sealed class ImmediateUiDispatcher : IUiDispatcher
{
    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        action();
    }
}
