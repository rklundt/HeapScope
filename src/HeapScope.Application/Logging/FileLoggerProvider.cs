using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace HeapScope.Application.Logging;

/// <summary>
/// Writes log records to a daily file (ARCHITECTURE.md 11), by default
/// <c>%LOCALAPPDATA%\HeapScope\logs\heapscope-yyyyMMdd.log</c>. Thread safe; never throws into callers.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly object _gate = new();
    private readonly string _directory;
    private readonly LogLevel _minimumLevel;
    private readonly TimeProvider _timeProvider;
    private StreamWriter? _writer;
    private DateOnly _writerDate;
    private bool _disposed;

    public FileLoggerProvider(string? directory = null, LogLevel minimumLevel = LogLevel.Information, TimeProvider? timeProvider = null)
    {
        _directory = directory ?? DefaultDirectory;
        _minimumLevel = minimumLevel;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public static string DefaultDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HeapScope", "logs");

    public string Directory => _directory;

    /// <summary>Path of the file written on <paramref name="date"/>.</summary>
    public string GetPath(DateOnly date) =>
        Path.Combine(_directory, $"heapscope-{date.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}.log");

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _writer?.Dispose();
            _writer = null;
        }
    }

    private void Write(LogLevel level, string category, string message, Exception? exception)
    {
        var now = _timeProvider.GetLocalNow();
        var line = new StringBuilder()
            .Append(now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
            .Append(" [").Append(ShortName(level)).Append("] ")
            .Append(category).Append(": ").Append(message);
        if (exception is not null)
        {
            line.AppendLine().Append(exception);
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                var date = DateOnly.FromDateTime(now.DateTime);
                if (_writer is null || date != _writerDate)
                {
                    _writer?.Dispose();
                    System.IO.Directory.CreateDirectory(_directory);
                    var stream = new FileStream(GetPath(date), FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                    _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
                    _writerDate = date;
                }

                _writer.WriteLine(line.ToString());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Logging must never take the application down; the debug output still has the record.
                System.Diagnostics.Debug.WriteLine($"HeapScope file log unavailable: {ex.Message}");
            }
        }
    }

    private static string ShortName(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRC",
        LogLevel.Debug => "DBG",
        LogLevel.Information => "INF",
        LogLevel.Warning => "WRN",
        LogLevel.Error => "ERR",
        LogLevel.Critical => "CRT",
        _ => "???",
    };

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= provider._minimumLevel;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            if (IsEnabled(logLevel))
            {
                provider.Write(logLevel, category, formatter(state, exception), exception);
            }
        }
    }
}
