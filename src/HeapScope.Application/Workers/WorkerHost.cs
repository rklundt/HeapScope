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

using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Security.Cryptography;
using HeapScope.Contracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StreamJsonRpc;
using HeapScope.Application.Events;

namespace HeapScope.Application.Workers;

/// <summary>
/// Launches and talks to the analysis worker (ARCHITECTURE.md 4.2 to 4.5): starts it lazily, performs a bounded
/// protocol handshake, forwards its stderr into the client log, detects crashes through process exit and pipe
/// disconnect, restarts it on the next call, and shuts it down in order when the host stops.
/// </summary>
/// <remarks>
/// Error contract: worker and transport failures surface only as <see cref="WorkerStartException"/>,
/// <see cref="WorkerCrashedException"/>, <see cref="WorkerOperationException"/> or <see cref="OperationCanceledException"/>.
/// Exceptions thrown by a caller's own delegate pass through unchanged.
/// </remarks>
public sealed class WorkerHost : IWorkerClient, IHostedService, IAsyncDisposable
{
    private static readonly TimeSpan s_shutdownCallTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan s_exitCodeWait = TimeSpan.FromSeconds(2);

    private readonly WorkerHostOptions _options;
    private readonly ILogger<WorkerHost> _logger;
    private readonly ILogger _workerLog;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly System.Collections.Concurrent.ConcurrentQueue<WorkerStateChangedEventArgs> _stateChanges = new();
    private readonly object _raising = new();
    private WorkerSession? _session;
    private volatile int _state = (int)WorkerState.NotStarted;
    private int _generation;

    // Set under the gate. Stopped: the host was stopped and refuses to start a worker until started again.
    // Disposed: permanent.
    private bool _stopped;
    private bool _disposed;

    public WorkerHost(IOptions<WorkerHostOptions> options, ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _options = options.Value;
        _logger = loggerFactory.CreateLogger<WorkerHost>();
        _workerLog = loggerFactory.CreateLogger("Worker");
    }

    public event EventHandler<WorkerStateChangedEventArgs>? StateChanged;

    public WorkerState State => (WorkerState)_state;

    public WorkerInfo? Info => Volatile.Read(ref _session)?.Info;

    public int Generation => Volatile.Read(ref _generation);

    public async Task<WorkerInfo> EnsureStartedAsync(CancellationToken cancellationToken)
    {
        var session = await GetSessionAsync(cancellationToken).ConfigureAwait(false);
        return session.Info;
    }

    public async Task<T> InvokeAsync<T>(Func<IWorkerService, CancellationToken, Task<T>> call, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);
        var session = await GetSessionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await call(session.Service, cancellationToken).ConfigureAwait(false);
        }
        catch (RemoteInvocationException ex)
        {
            throw Translate(ex);
        }
        catch (Exception ex) when (IsTransportFailure(ex, session))
        {
            throw await CrashedAsync(session, ex).ConfigureAwait(false);
        }
        catch (RemoteRpcException ex)
        {
            // Method not found or serialization failures: the two sides disagree about the contract.
            var code = ex is RemoteMethodNotFoundException ? HeapScopeErrorCode.ProtocolMismatch : HeapScopeErrorCode.Unknown;
            throw new WorkerOperationException(code, $"The analysis worker could not process the request: {ex.Message}", ex);
        }
    }

    public async Task InvokeAsync(Func<IWorkerService, CancellationToken, Task> call, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);
        await InvokeAsync(
            async (service, ct) =>
            {
                await call(service, ct).ConfigureAwait(false);
                return true;
            },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task RestartAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfUnavailable();
            if (_session is { } session)
            {
                Volatile.Write(ref _session, null);
                await StopSessionAsync(session, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
            RaiseStateChanges();
        }

        await GetSessionAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The worker starts lazily on first use; starting the host only allows it again after a stop.</summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _stopped = false;
        }
        finally
        {
            _gate.Release();
            RaiseStateChanges();
        }
    }

    /// <summary>
    /// Stops the worker in order: shutdown request, bounded wait, then kill. Honors <paramref name="cancellationToken"/>
    /// as the overall bound: when it fires, the worker is killed. No new worker starts until <see cref="StartAsync"/>.
    /// </summary>
    public Task StopAsync(CancellationToken cancellationToken) => StopCoreAsync(dispose: false, cancellationToken);

    public async ValueTask DisposeAsync()
    {
        using var bound = new CancellationTokenSource(_options.ShutdownTimeout + s_shutdownCallTimeout);
        await StopCoreAsync(dispose: true, bound.Token).ConfigureAwait(false);
    }

    private async Task StopCoreAsync(bool dispose, CancellationToken cancellationToken)
    {
        // The gate wait is not cancelable here: stopping must not give up while a start is in progress; a start is
        // itself bounded by the connect and handshake timeouts.
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            _stopped = true;
            _disposed |= dispose;
            if (_session is { } session)
            {
                Volatile.Write(ref _session, null);
                await StopSessionAsync(session, cancellationToken).ConfigureAwait(false);
                SetState(WorkerState.Stopped, null);
            }
        }
        finally
        {
            _gate.Release();
            RaiseStateChanges();
        }
    }

    private async Task<WorkerSession> GetSessionAsync(CancellationToken cancellationToken)
    {
        var current = Volatile.Read(ref _session);
        if (current is { IsAlive: true })
        {
            return current;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfUnavailable();
            if (_session is { IsAlive: true } alive)
            {
                return alive;
            }

            if (_session is { } dead)
            {
                Volatile.Write(ref _session, null);
                await dead.DisposeAsync().ConfigureAwait(false);
            }

            SetState(WorkerState.Starting, null);
            WorkerSession session;
            try
            {
                session = await StartSessionAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The caller gave up while the worker was starting: not a crash.
                SetState(WorkerState.NotStarted, null);
                throw;
            }
            catch (Exception ex) when (ex is WorkerException)
            {
                // The banner shows the message; the log keeps it too (sprint 2.3.7 review H4).
                _logger.LogWarning(ex, "The analysis worker could not be started: {Message}", ex.Message);
                SetState(WorkerState.Crashed, ex.Message);
                throw;
            }
            catch (Exception ex)
            {
                // Callers handle the worker exception types only; anything else (a bug in starting the process) must not
                // escape as an unexpected type, or a front end waits forever (sprint 1.3 smoke run).
                _logger.LogError(ex, "Starting the analysis worker failed unexpectedly");
                SetState(WorkerState.Crashed, ex.Message);
                throw new WorkerStartException($"The analysis worker could not be started: {ex.Message}", ex);
            }

            Interlocked.Increment(ref _generation);
            Volatile.Write(ref _session, session);
            SetState(WorkerState.Running, null);
            return session;
        }
        finally
        {
            _gate.Release();
            RaiseStateChanges();
        }
    }

    private void ThrowIfUnavailable()
    {
        if (_disposed || _stopped)
        {
            throw new WorkerStartException("HeapScope is shutting down; the analysis worker is stopped.");
        }
    }

    private async Task<WorkerSession> StartSessionAsync(CancellationToken cancellationToken)
    {
        Process? process = null;
        string pipeName;
        if (_options.AttachPipeName is { Length: > 0 } attach)
        {
            if (PipeNameProblem(attach, OperatingSystem.IsWindows(), Path.GetTempPath()) is { } problem)
            {
                throw new WorkerStartException(problem);
            }

            pipeName = attach;
            _logger.LogInformation("Attaching to the worker on pipe {Pipe}", pipeName);
        }
        else
        {
            var path = _options.ResolveWorkerPath();
            if (!File.Exists(path))
            {
                throw new WorkerStartException($"The analysis worker was not found at {path}. Rebuild HeapScope or set Worker:WorkerPath.");
            }

            pipeName = NewPipeName();
            process = StartProcess(path, pipeName);
        }

        var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        JsonRpc? rpc = null;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.ConnectTimeout);
            await ConnectAsync(pipe, process, timeout.Token, cancellationToken).ConfigureAwait(false);
            // Requests carry the current activity's trace context to the worker (sprint 2.3.7 review M1).
            rpc = new JsonRpc(new HeaderDelimitedMessageHandler(pipe, pipe, new JsonMessageFormatter()))
            {
                ActivityTracingStrategy = new ActivityTracingStrategy(),
            };
            var service = rpc.Attach<IWorkerService>();
            rpc.StartListening();

            var info = await HandshakeAsync(service, process, timeout.Token, cancellationToken).ConfigureAwait(false);
            var session = new WorkerSession(process, pipe, rpc, service, info);
            rpc.Disconnected += (_, e) => OnSessionLost(session, $"pipe disconnected ({e.Description})");
            if (process is not null)
            {
                process.Exited += (_, _) => OnSessionLost(session, "process exited");
                if (process.HasExited)
                {
                    OnSessionLost(session, "process exited");
                }
            }

            _logger.LogInformation(
                "Worker {Version} running: pid {Pid}, instance {Instance}, server GC {ServerGc}",
                info.ProductVersion,
                info.ProcessId,
                info.InstanceId,
                info.ServerGc);
            return session;
        }
        catch
        {
            rpc?.Dispose();
            await pipe.DisposeAsync().ConfigureAwait(false);
            KillQuietly(process);
            process?.Dispose();
            throw;
        }
    }

    /// <summary>A new worker pipe name: the prefix and 24 random hex digits (96 bits), short enough for macOS's socket paths.</summary>
    public static string NewPipeName() => WorkerProtocol.PipeNamePrefix + RandomNumberGenerator.GetHexString(24, lowercase: true);

    /// <summary>
    /// Why <paramref name="name"/> cannot name a pipe on this system, or null (2.3.5 review L16): off Windows a pipe is a
    /// Unix domain socket at <c>&lt;temp&gt;/CoreFxPipe_&lt;name&gt;</c>, whose path holds at most 103 characters on macOS.
    /// </summary>
    public static string? PipeNameProblem(string name, bool isWindows, string tempPath)
    {
        ArgumentNullException.ThrowIfNull(name);
        const int MaxSocketPath = 103;
        var socket = Path.Combine(tempPath ?? string.Empty, "CoreFxPipe_" + name);
        return isWindows || socket.Length <= MaxSocketPath
            ? null
            : string.Create(CultureInfo.InvariantCulture, $"The pipe name '{name}' is too long for this system: its socket path {socket} has {socket.Length} characters, and at most {MaxSocketPath} are allowed. Use a shorter name.");
    }

    /// <summary>Connects to the pipe within the connect timeout, failing fast when the worker exits during startup.</summary>
    private async Task ConnectAsync(NamedPipeClientStream pipe, Process? process, CancellationToken timeout, CancellationToken caller)
    {
        try
        {
            var connect = pipe.ConnectAsync(timeout);
            if (process is not null)
            {
                var exited = process.WaitForExitAsync(timeout);
                var first = await Task.WhenAny(connect, exited).ConfigureAwait(false);
                if (first == exited && exited.IsCompletedSuccessfully && !connect.IsCompletedSuccessfully)
                {
                    throw new WorkerStartException(
                        $"The analysis worker exited during startup with code {process.ExitCode.ToString(CultureInfo.InvariantCulture)}. See the log for its messages.");
                }
            }

            await connect.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!caller.IsCancellationRequested)
        {
            throw new WorkerStartException($"The analysis worker did not open its pipe within {_options.ConnectTimeout.TotalSeconds:N0} s.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new WorkerStartException($"Could not connect to the analysis worker's pipe: {ex.Message}", ex);
        }
    }

    /// <summary>Performs the protocol handshake within the connect timeout; any failure is a start failure.</summary>
    private async Task<WorkerInfo> HandshakeAsync(IWorkerService service, Process? process, CancellationToken timeout, CancellationToken caller)
    {
        var clientVersion = _options.ClientProtocolVersion;
        WorkerInfo info;
        try
        {
            info = await service.GetInfoAsync(clientVersion, timeout).ConfigureAwait(false);
        }
        catch (RemoteInvocationException ex)
        {
            throw Translate(ex);
        }
        catch (OperationCanceledException) when (!caller.IsCancellationRequested)
        {
            throw new WorkerStartException($"The analysis worker did not answer the handshake within {_options.ConnectTimeout.TotalSeconds:N0} s.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var exit = process is { HasExited: true } ? $" (exit code {process.ExitCode.ToString(CultureInfo.InvariantCulture)})" : string.Empty;
            throw new WorkerStartException($"The analysis worker stopped during the handshake{exit}. See the log for its messages.", ex);
        }

        if (info.ProtocolVersion != clientVersion)
        {
            throw new WorkerOperationException(
                HeapScopeErrorCode.ProtocolMismatch,
                $"The analysis worker speaks protocol {info.ProtocolVersion} but HeapScope expects {clientVersion}. Use a matching HeapScope build.",
                null);
        }

        return info;
    }

    private Process StartProcess(string path, string pipeName)
    {
        var info = new ProcessStartInfo(path)
        {
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(path)!,
        };
        if (!info.Environment.TryGetValue("DOTNET_GCHeapCount", out var heapCount) || string.IsNullOrEmpty(heapCount))
        {
            // Hexadecimal, as the runtime reads it.
            info.Environment["DOTNET_GCHeapCount"] = _options.ResolveGcHeapCount().ToString("x", CultureInfo.InvariantCulture);
        }

        info.ArgumentList.Add("--pipe");
        info.ArgumentList.Add(pipeName);
        info.ArgumentList.Add("--parent-pid");
        info.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        if (!string.IsNullOrWhiteSpace(_options.DacFolder))
        {
            info.ArgumentList.Add("--dac-folder");
            info.ArgumentList.Add(_options.DacFolder);
        }

        if (_options.AllowMicrosoftSymbolServer)
        {
            // The worker logs a warning too; this one reaches the CLI's stderr and the UI log (ADR-017 privacy note).
            _logger.LogWarning("Symbol server downloads are on: a missing DAC may be downloaded from Microsoft's symbol server (module identifiers only, never dump contents).");
            info.ArgumentList.Add("--allow-symbol-server");
            info.ArgumentList.Add("true");
        }

        if (_options.WorkerLogLevel != LogLevel.Information)
        {
            info.ArgumentList.Add("--log-level");
            info.ArgumentList.Add(_options.WorkerLogLevel.ToString());
        }

        if (_options.EnableTestHooks)
        {
            info.ArgumentList.Add("--enable-test-hooks");
        }

        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        var generation = Generation + 1;
        process.ErrorDataReceived += (_, e) => WorkerLogForwarder.Forward(_workerLog, e.Data, SafeId(process), generation);
        process.OutputDataReceived += (_, e) => WorkerLogForwarder.Forward(_workerLog, e.Data, SafeId(process), generation);
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            process.Dispose();
            throw new WorkerStartException($"The analysis worker at {path} could not be started: {ex.Message}", ex);
        }

        // The readers start without this context: a forwarded line carries the worker's own scopes (its trace id per
        // request), not the trace of whatever happened to start the worker.
        using (ExecutionContext.SuppressFlow())
        {
            process.BeginErrorReadLine();
            process.BeginOutputReadLine();
        }

        _logger.LogInformation("Started worker process {Pid} (generation {Generation}) from {Path} on pipe {Pipe}", process.Id, generation, path, pipeName);
        return process;
    }

    private static int SafeId(Process process)
    {
        try
        {
            return process.Id;
        }
        catch (InvalidOperationException)
        {
            return 0;
        }
    }

    private void OnSessionLost(WorkerSession session, string reason)
    {
        if (!session.MarkDead() || session.Stopping)
        {
            return;
        }

        var exitCode = session.TryGetExitCode();
        var meaning = exitCode is int known ? WorkerLogForwarder.ExplainExitCode(known) : null;
        var message = exitCode is int code
            ? $"The analysis worker stopped unexpectedly (exit code {code.ToString(CultureInfo.InvariantCulture)}{(meaning is null ? string.Empty : ", " + meaning)}): {reason}."
            : $"The connection to the analysis worker was lost: {reason}.";
        _logger.LogWarning(
            "Worker lost: exit code {ExitCode} ({Meaning}); {Reason}; generation {Generation}",
            exitCode?.ToString(CultureInfo.InvariantCulture) ?? "unknown",
            meaning ?? "no known cause",
            reason,
            Generation);
        if (ReferenceEquals(Volatile.Read(ref _session), session))
        {
            SetState(WorkerState.Crashed, message);
            RaiseStateChanges();
        }
    }

    private async Task<WorkerCrashedException> CrashedAsync(WorkerSession session, Exception cause)
    {
        if (session.Process is { } process)
        {
            using var wait = new CancellationTokenSource(s_exitCodeWait);
            try
            {
                await process.WaitForExitAsync(wait.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Still running although the pipe broke: report without an exit code.
            }
        }

        OnSessionLost(session, cause.GetType().Name);
        return new WorkerCrashedException(session.TryGetExitCode(), cause);
    }

    private async Task StopSessionAsync(WorkerSession session, CancellationToken cancellationToken)
    {
        session.Stopping = true;
        if (session.Process is { } process)
        {
            if (session.IsAlive)
            {
                try
                {
                    using var callTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    callTimeout.CancelAfter(s_shutdownCallTimeout);
                    await session.Service.ShutdownAsync(callTimeout.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    _logger.LogDebug(ex, "Shutdown request to the worker failed; waiting for exit anyway");
                }
            }

            try
            {
                using var exitTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                exitTimeout.CancelAfter(_options.ShutdownTimeout);
                await process.WaitForExitAsync(exitTimeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("Worker {Pid} did not exit in time; killing it", process.Id);
                KillQuietly(process);
            }
        }

        session.MarkDead();
        await session.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>Records a state change; <see cref="RaiseStateChanges"/> reports it once the gate is released.</summary>
    private void SetState(WorkerState state, string? message)
    {
        _state = (int)state;
        _stateChanges.Enqueue(new WorkerStateChangedEventArgs(state, message));
    }

    /// <summary>
    /// Raises the state changes recorded so far, in order, outside the gate (peer review P9): a listener that throws or
    /// calls back into the host cannot fail or block the call that changed the state.
    /// </summary>
    private void RaiseStateChanges()
    {
        lock (_raising)
        {
            while (_stateChanges.TryDequeue(out var change))
            {
                SafeEvents.Raise(StateChanged, this, change, _logger, nameof(StateChanged));
            }
        }
    }

    private static bool IsTransportFailure(Exception ex, WorkerSession session) =>
        ex is ConnectionLostException or ObjectDisposedException or IOException
        || (ex is not OperationCanceledException and not RemoteRpcException && !session.IsAlive);

    private static WorkerOperationException Translate(RemoteInvocationException ex)
    {
        var code = Enum.IsDefined(typeof(HeapScopeErrorCode), ex.ErrorCode) ? (HeapScopeErrorCode)ex.ErrorCode : HeapScopeErrorCode.Unknown;
        return new WorkerOperationException(code, ex.Message, ex) { DacTrust = code == HeapScopeErrorCode.DacNotTrusted ? DacTrustOf(ex.ErrorData) : null };
    }

    /// <summary>The DAC details of a <see cref="HeapScopeErrorCode.DacNotTrusted"/> refusal; null when absent or malformed.</summary>
    internal static DacTrustDetails? DacTrustOf(object? errorData)
    {
        // The formatter hands error data over as its own JSON object; its text is the record as the worker wrote it.
        if (errorData?.ToString() is not { Length: > 0 } json)
        {
            return null;
        }

        try
        {
            var details = System.Text.Json.JsonSerializer.Deserialize<DacTrustDetails>(json, s_errorJson);
            return details is { Path.Length: > 0, Sha256.Length: 64 } ? details : null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static readonly System.Text.Json.JsonSerializerOptions s_errorJson = new() { PropertyNameCaseInsensitive = true };

    private static void KillQuietly(Process? process)
    {
        try
        {
            if (process is { HasExited: false })
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException)
        {
            // Already gone or not ours to kill.
        }
    }

    private sealed class WorkerSession(Process? process, NamedPipeClientStream pipe, JsonRpc rpc, IWorkerService service, WorkerInfo info)
        : IAsyncDisposable
    {
        private int _dead;
        private volatile bool _stopping;

        public Process? Process { get; } = process;

        public IWorkerService Service { get; } = service;

        public WorkerInfo Info { get; } = info;

        public bool IsAlive => Volatile.Read(ref _dead) == 0;

        public bool Stopping
        {
            get => _stopping;
            set => _stopping = value;
        }

        /// <summary>Returns true only for the first caller.</summary>
        public bool MarkDead() => Interlocked.Exchange(ref _dead, 1) == 0;

        public int? TryGetExitCode()
        {
            try
            {
                return Process is { HasExited: true } p ? p.ExitCode : null;
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }

        public async ValueTask DisposeAsync()
        {
            rpc.Dispose();
            await pipe.DisposeAsync().ConfigureAwait(false);
            Process?.Dispose();
        }
    }
}
