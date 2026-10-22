using Microsoft.Extensions.Logging;

namespace HeapScope.Application.Workers;

/// <summary>
/// Forwards the worker's single-line stderr records ("HH:mm:ss.fff info: Category[0] message") into the client
/// log, keeping the level (ARCHITECTURE.md 4.3). Lines without a level (for example an unhandled exception's stack
/// trace) are logged as warnings so they are never lost.
/// </summary>
internal static class WorkerLogForwarder
{
    private static readonly (string Token, LogLevel Level)[] s_levels =
    [
        ("trce:", LogLevel.Trace),
        ("dbug:", LogLevel.Debug),
        ("info:", LogLevel.Information),
        ("warn:", LogLevel.Warning),
        ("fail:", LogLevel.Error),
        ("crit:", LogLevel.Critical),
    ];

    public static void Forward(ILogger logger, string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        var level = LevelOf(line);
        if (logger.IsEnabled(level))
        {
            logger.Log(level, "{WorkerLine}", line);
        }
    }

    internal static LogLevel LevelOf(string line)
    {
        foreach (var (token, level) in s_levels)
        {
            var at = line.IndexOf(token, StringComparison.Ordinal);
            if (at is >= 0 and < 20)
            {
                return level;
            }
        }

        return LogLevel.Warning;
    }
}
