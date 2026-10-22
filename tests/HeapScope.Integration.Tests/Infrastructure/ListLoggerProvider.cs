using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace HeapScope.Integration.Tests.Infrastructure;

/// <summary>Collects log records in memory so tests can assert on forwarded worker output.</summary>
internal sealed class ListLoggerProvider : ILoggerProvider, ILoggerFactory
{
    public ConcurrentQueue<(string Category, LogLevel Level, string Message)> Records { get; } = new();

    public ILogger CreateLogger(string categoryName) => new ListLogger(this, categoryName);

    public void AddProvider(ILoggerProvider provider)
    {
    }

    public void Dispose()
    {
    }

    private sealed class ListLogger(ListLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            owner.Records.Enqueue((category, logLevel, formatter(state, exception)));
    }
}
