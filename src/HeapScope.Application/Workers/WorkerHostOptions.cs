using HeapScope.Contracts;

namespace HeapScope.Application.Workers;

/// <summary>Configuration of <see cref="WorkerHost"/> (configuration section "Worker").</summary>
public sealed class WorkerHostOptions
{
    public const string SectionName = "Worker";

    /// <summary>Full path of HeapScope.Worker.exe; defaults to the worker/ folder next to the executable.</summary>
    public string? WorkerPath { get; set; }

    /// <summary>When set, connects to an already running worker on this pipe instead of launching one.</summary>
    public string? AttachPipeName { get; set; }

    /// <summary>How long to wait for the worker's pipe.</summary>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>How long an orderly shutdown may take before the worker is killed.</summary>
    public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Starts the worker with its crash and hang test hooks enabled (tests only).</summary>
    public bool EnableTestHooks { get; set; }

    /// <summary>Protocol version the client announces in the handshake. Tests override it to exercise mismatches.</summary>
    internal int ClientProtocolVersion { get; set; } = WorkerProtocol.Version;

    /// <summary>The worker path to launch.</summary>
    public string ResolveWorkerPath() =>
        WorkerPath ?? Path.Combine(AppContext.BaseDirectory, "worker", "HeapScope.Worker.exe");
}
