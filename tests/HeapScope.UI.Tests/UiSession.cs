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


using Avalonia;
using Avalonia.Headless;
using Avalonia.Logging;

[assembly: AssemblyFixture(typeof(HeapScope.UI.Tests.UiSession))]

namespace HeapScope.UI.Tests;

/// <summary>
/// One Avalonia application on the headless platform for the whole test assembly (sprint 2.3.6): views are built,
/// laid out and drawn off screen with Skia, on the session's UI thread. Binding failures are collected, so a test can
/// check that a view binds cleanly.
/// </summary>
public sealed class UiSession : IDisposable
{
    private readonly HeadlessUnitTestSession _session = HeadlessUnitTestSession.StartNew(typeof(UiSession));
    private readonly BindingErrorSink _errors = new();

    public UiSession()
    {
        Logger.Sink = _errors;
    }

    /// <summary>The headless app (Avalonia finds it by this name).</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });

    /// <summary>Runs <paramref name="body"/> on the UI thread and returns its result.</summary>
    public Task<T> RunAsync<T>(Func<Task<T>> body) => _session.Dispatch(body, TestContext.Current.CancellationToken);

    /// <summary>Runs <paramref name="body"/> on the UI thread.</summary>
    public Task RunAsync(Func<Task> body) => _session.Dispatch(
        async () =>
        {
            await body();
            return true;
        },
        TestContext.Current.CancellationToken);

    /// <summary>Clears the binding failures seen so far (on the UI thread).</summary>
    public void ClearBindingErrors() => _errors.Clear();

    /// <summary>The binding failures logged since the last clear.</summary>
    public IReadOnlyList<string> BindingErrors => _errors.Messages;

    // The session's dispose waits for its UI thread to finish, which Avalonia's dispatcher loop does not do on its own; the
    // wait is bounded, and the test process ends the background thread with it.
    public void Dispose() => _ = Task.Run(_session.Dispose).Wait(TimeSpan.FromSeconds(1));

    private sealed class BindingErrorSink : ILogSink
    {
        private readonly List<string> _messages = [];

        public IReadOnlyList<string> Messages
        {
            get
            {
                lock (_messages)
                {
                    return [.. _messages];
                }
            }
        }

        public void Clear()
        {
            lock (_messages)
            {
                _messages.Clear();
            }
        }

        public bool IsEnabled(LogEventLevel level, string area) => level >= LogEventLevel.Warning && area == LogArea.Binding;

        public void Log(LogEventLevel level, string area, object? source, string messageTemplate) =>
            Log(level, area, source, messageTemplate, []);

        public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues)
        {
            if (!IsEnabled(level, area))
            {
                return;
            }

            lock (_messages)
            {
                _messages.Add($"{source?.GetType().Name}: {messageTemplate} [{string.Join(", ", propertyValues)}]");
            }
        }
    }
}
