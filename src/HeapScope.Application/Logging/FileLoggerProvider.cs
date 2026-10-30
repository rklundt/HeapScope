// HeapScope
// Copyright (C) 2026 Ray Klundt
// https://github.com/rklundt/HeapScope
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU Affero General Public License as published
// by the Free Software Foundation, version 3 only, with additional terms
// under Section 7 of that license.
//
// Additional terms, including attribution requirements, apply.
// See the ADDITIONAL-TERMS.md file distributed with this program.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU Affero General Public License for more details.
//
// You should have received a copy of the GNU Affero General Public License
// along with this program (see the LICENSE file). If not, see
// <https://www.gnu.org/licenses/>.
//
// Commercial licenses are available. See COMMERCIAL.md.

using System.Globalization;
using System.Text;
using HeapScope.Contracts;
using HeapScope.Core;
using Microsoft.Extensions.Logging;

namespace HeapScope.Application.Logging;

/// <summary>
/// Writes log records to a file of this process (ARCHITECTURE.md 11), by default under
/// <c>%LOCALAPPDATA%\HeapScope\logs</c> (<c>~/.local/share/HeapScope/logs</c> on Linux,
/// <c>~/Library/Application Support/HeapScope/logs</c> on macOS). Thread safe; never throws into callers.
/// </summary>
/// <remarks>
/// Sprint 2.3.7 (review H1, M17): one file per process and day, <c>heapscope-yyyyMMdd-&lt;app&gt;-&lt;pid&gt;.log</c>, so
/// two HeapScope processes never write over each other's lines; each file starts with a header naming the build, the
/// system and the process; lines carry an ISO 8601 time with offset and the active scopes (the trace id ties the UI or
/// CLI lines to the worker lines they caused); a file rolls at <see cref="DefaultMaxFileBytes"/> and files older than
/// <see cref="DefaultKeepDays"/> days are removed when the first file of a process opens.
/// </remarks>
public sealed class FileLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    /// <summary>The size at which a file rolls over to a numbered one.</summary>
    public const long DefaultMaxFileBytes = 50L * 1024 * 1024;

    /// <summary>How many days log files are kept.</summary>
    public const int DefaultKeepDays = 14;

    private readonly object _gate = new();
    private readonly string _directory;
    private readonly LogLevel _minimumLevel;
    private readonly TimeProvider _timeProvider;
    private readonly string _app;
    private readonly int _processId;
    private readonly long _maxFileBytes;
    private readonly int _keepDays;
    private IExternalScopeProvider? _scopes;
    private StreamWriter? _writer;
    private DateOnly _writerDate;
    private int _part;
    private long _written;
    private bool _cleaned;
    private bool _disposed;

    /// <param name="directory">The log folder; null for <see cref="DefaultDirectory"/>.</param>
    /// <param name="minimumLevel">The lowest level written.</param>
    /// <param name="timeProvider">The clock (tests).</param>
    /// <param name="app">Which front end writes the file: <c>ui</c>, <c>cli</c>.</param>
    /// <param name="processId">The process id in the file name; null for this process.</param>
    /// <param name="maxFileBytes">The size at which a file rolls.</param>
    /// <param name="keepDays">How many days files are kept.</param>
    public FileLoggerProvider(
        string? directory = null,
        LogLevel minimumLevel = LogLevel.Information,
        TimeProvider? timeProvider = null,
        string app = "app",
        int? processId = null,
        long maxFileBytes = DefaultMaxFileBytes,
        int keepDays = DefaultKeepDays)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(app);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxFileBytes, 1024);
        ArgumentOutOfRangeException.ThrowIfLessThan(keepDays, 1);
        _directory = string.IsNullOrWhiteSpace(directory) ? DefaultDirectory : directory;
        _minimumLevel = minimumLevel;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _app = app;
        _processId = processId ?? Environment.ProcessId;
        _maxFileBytes = maxFileBytes;
        _keepDays = keepDays;
    }

    public static string DefaultDirectory => HeapScope.Core.HeapScopeFolders.Default.Logs;

    /// <summary>The folder this provider writes to (messages that point users to the log name it, review M16).</summary>
    public string Directory => _directory;

    /// <summary>The lowest level written.</summary>
    public LogLevel MinimumLevel => _minimumLevel;

    /// <summary>
    /// Reads <c>Logging:File:MinimumLevel</c> (Trace, Debug, Information, Warning, Error, Critical); Information when unset
    /// or not a level. Support raises it to Debug for one run (review M17).
    /// </summary>
    public static LogLevel ParseLevel(string? value) =>
        Enum.TryParse<LogLevel>(value, ignoreCase: true, out var level) && level is >= LogLevel.Trace and <= LogLevel.Critical
            ? level
            : LogLevel.Information;

    /// <summary>Path of this process's file written on <paramref name="date"/> (its first part).</summary>
    public string GetPath(DateOnly date) => GetPath(date, part: 1);

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _writer?.Dispose();
            _writer = null;
        }
    }

    private string GetPath(DateOnly date, int part) => Path.Combine(
        _directory,
        string.Create(
            CultureInfo.InvariantCulture,
            $"heapscope-{date:yyyyMMdd}-{_app}-{_processId}{(part > 1 ? "-" + part.ToString(CultureInfo.InvariantCulture) : string.Empty)}.log"));

    private void Write(LogLevel level, string category, string message, Exception? exception)
    {
        var now = _timeProvider.GetLocalNow();
        var line = new StringBuilder()
            .Append(Timestamp(now))
            .Append(" [").Append(ShortName(level)).Append("] ")
            .Append(category).Append(": ").Append(message);
        AppendScopes(line);
        if (exception is not null)
        {
            line.AppendLine().Append(exception);
        }

        var text = line.ToString();
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                var date = DateOnly.FromDateTime(now.DateTime);
                if (_writer is null || date != _writerDate || _written >= _maxFileBytes)
                {
                    Open(date, now);
                }

                _writer!.WriteLine(text);
                _written += Encoding.UTF8.GetByteCount(text) + Environment.NewLine.Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Logging must never take the application down; the debug output still has the record.
                System.Diagnostics.Debug.WriteLine($"HeapScope file log unavailable: {ex.Message}");
            }
        }
    }

    private static string Timestamp(DateTimeOffset time) =>
        time.ToString("yyyy-MM-dd'T'HH:mm:ss.fffzzz", CultureInfo.InvariantCulture);

    // Under the gate: the day's first file, the next part when the current one is full, or the next day's file.
    private void Open(DateOnly date, DateTimeOffset now)
    {
        _part = _writer is not null && date == _writerDate ? _part + 1 : 1;
        _writer?.Dispose();
        _writer = null;
        PrivateFiles.CreateDirectory(_directory);
        if (!_cleaned)
        {
            _cleaned = true;
            RemoveOldFiles(now);
        }

        // A file left by an earlier process with the same id (ids are reused) is appended to, after a new header. Shared
        // for writing: the UI's last-chance start logger may open this process's file while the host's provider has it.
        var stream = PrivateFiles.Open(GetPath(date, _part), FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        stream.Seek(0, SeekOrigin.End);
        _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
        _writerDate = date;
        _written = stream.Length;
        var header = string.Create(
            CultureInfo.InvariantCulture,
            $"# {RuntimeEnvironment.Summary}; {_app} pid {_processId}; part {_part}; level {_minimumLevel}; log folder {_directory}; opened {Timestamp(now)}");
        _writer.WriteLine(header);
        _written += Encoding.UTF8.GetByteCount(header) + Environment.NewLine.Length;
    }

    private void RemoveOldFiles(DateTimeOffset now)
    {
        var cutoff = now.UtcDateTime.AddDays(-_keepDays);
        foreach (var file in System.IO.Directory.EnumerateFiles(_directory, "heapscope-*.log"))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                {
                    File.Delete(file);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Still open by another process, or not ours to delete: kept.
                System.Diagnostics.Debug.WriteLine($"HeapScope old log {file} kept: {ex.Message}");
            }
        }
    }

    // The active scopes after the message, as " => TraceId:..., JobId:7". Activity tracking adds the trace id as a scope.
    private void AppendScopes(StringBuilder line) => _scopes?.ForEachScope(
        static (scope, builder) =>
        {
            if (scope is IEnumerable<KeyValuePair<string, object?>> pairs)
            {
                var first = true;
                foreach (var (key, value) in pairs)
                {
                    if (key == "{OriginalFormat}")
                    {
                        continue;
                    }

                    builder.Append(first ? " => " : ", ").Append(key).Append(':').Append(Convert.ToString(value, CultureInfo.InvariantCulture));
                    first = false;
                }
            }
            else if (scope is not null)
            {
                builder.Append(" => ").Append(Convert.ToString(scope, CultureInfo.InvariantCulture));
            }
        },
        line);

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
            where TState : notnull => provider._scopes?.Push(state);

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
