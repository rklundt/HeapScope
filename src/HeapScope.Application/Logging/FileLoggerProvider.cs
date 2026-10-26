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
using Microsoft.Extensions.Logging;
using HeapScope.Core;

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
                    PrivateFiles.CreateDirectory(_directory);
                    var stream = PrivateFiles.Open(GetPath(date), FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                    stream.Seek(0, SeekOrigin.End);
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
