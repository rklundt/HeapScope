using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Diagnostics.NETCore.Client;

namespace HeapScope.Capture;

/// <summary>Free disk space for a folder. Faked in tests.</summary>
public interface IDiskSpace
{
    /// <summary>Bytes available to this user on the volume holding <paramref name="directory"/>; null when unknown.</summary>
    long? AvailableBytes(string directory);
}

/// <summary>
/// Reads free space with <c>GetDiskFreeSpaceEx</c> on the folder itself, so UNC paths and volumes mounted into folders
/// report the right volume (sprint 1.1 review).
/// </summary>
public sealed partial class VolumeDiskSpace : IDiskSpace
{
    public static VolumeDiskSpace Instance { get; } = new();

    public long? AvailableBytes(string directory) =>
        GetDiskFreeSpaceEx(Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar, out var available, out _, out _) ? (long)available : null;

    [LibraryImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetDiskFreeSpaceEx(string directory, out ulong freeBytesAvailable, out ulong totalBytes, out ulong totalFreeBytes);
}

/// <summary>The disk holding the dump folder has less free space than the dump will need.</summary>
public sealed class InsufficientDiskSpaceException : Exception
{
    public InsufficientDiskSpaceException()
    {
    }

    public InsufficientDiskSpaceException(string message)
        : base(message)
    {
    }

    public InsufficientDiskSpaceException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public InsufficientDiskSpaceException(string directory, long neededBytes, long availableBytes, long reserveBytes)
        : base(string.Create(
            CultureInfo.InvariantCulture,
            $"The dump needs about {Gb(neededBytes)} and HeapScope keeps {Gb(reserveBytes)} free, but only {Gb(availableBytes)} is free on the drive of {directory}. Free some space or choose another folder."))
    {
        NeededBytes = neededBytes;
        AvailableBytes = availableBytes;
    }

    public long NeededBytes { get; }

    public long AvailableBytes { get; }

    private static string Gb(long bytes) => (bytes / (1024.0 * 1024 * 1024)).ToString("N1", CultureInfo.InvariantCulture) + " GB";
}

/// <summary>Writes a full dump of a .NET process through its diagnostics port.</summary>
public interface IDumpWriter
{
    /// <summary>
    /// Asks the runtime to write a full dump and waits until it has. It is never abandoned halfway: the runtime's
    /// createdump keeps writing whatever the caller does (sprint 1.1 review).
    /// </summary>
    Task WriteAsync(int processId, string path);
}

/// <summary>The runtime's own dump writer (createdump), started through the diagnostics IPC.</summary>
public sealed class DiagnosticsDumpWriter : IDumpWriter
{
    public static DiagnosticsDumpWriter Instance { get; } = new();

    public Task WriteAsync(int processId, string path) =>
        new DiagnosticsClient(processId).WriteDumpAsync(DumpType.Full, path, logDumpGeneration: false, CancellationToken.None);
}

/// <summary>Progress of a capture.</summary>
public sealed record CaptureProgress(string Phase, long? EstimatedBytes = null, string? Message = null);

/// <summary>
/// Captures a full dump of a .NET process (sprint 1.1): checks elevation (ADR-014) and free disk space before asking
/// the runtime to write the dump, writes to a temporary name and renames it only on success, and removes the file
/// when the capture fails or is canceled. A canceled capture waits for the runtime to finish writing (the target's
/// createdump cannot be stopped safely) and then deletes the file, so no dump is left behind.
/// </summary>
public sealed class DumpCapturer(
    IElevationProbe? elevation = null,
    IDiskSpace? disk = null,
    IDumpWriter? writer = null,
    Func<DotNetProcess, long>? estimator = null)
{
    /// <summary>Free space HeapScope always leaves on the drive (the index cache and the system need room too).</summary>
    public const long ReserveBytes = 1L * 1024 * 1024 * 1024;

    /// <summary>Suffix of the file while the runtime writes it.</summary>
    public const string PartialSuffix = ".partial";

    private readonly IElevationProbe _elevation = elevation ?? WindowsElevationProbe.Instance;
    private readonly IDiskSpace _disk = disk ?? VolumeDiskSpace.Instance;
    private readonly IDumpWriter _writer = writer ?? DiagnosticsDumpWriter.Instance;
    private readonly Func<DotNetProcess, long> _estimate = estimator ?? DumpSizeEstimator.Estimate;

    /// <summary>The estimated dump size of <paramref name="process"/>.</summary>
    public long Estimate(DotNetProcess process) => _estimate(process);

    /// <summary>Checks elevation and disk space; throws the specific exception with a next step when one fails.</summary>
    public long Preflight(DotNetProcess process, string path)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (process.Elevation == ElevationState.Elevated && !_elevation.IsCurrentProcessElevated)
        {
            throw ElevationRequiredException.For(process.ProcessId, process.Name);
        }

        var directory = DirectoryOf(path);
        var needed = _estimate(process);
        if (_disk.AvailableBytes(directory) is { } available && available < needed + ReserveBytes)
        {
            throw new InsufficientDiskSpaceException(directory, needed, available, ReserveBytes);
        }

        return needed;
    }

    /// <summary>
    /// Captures a full dump of <paramref name="process"/> to <paramref name="path"/>. Throws
    /// <see cref="ElevationRequiredException"/>, <see cref="InsufficientDiskSpaceException"/>,
    /// <see cref="DumpFileExistsException"/>, <see cref="TargetUnavailableException"/>,
    /// <see cref="DumpWriteFailedException"/> or <see cref="OperationCanceledException"/>.
    /// </summary>
    public async Task CaptureAsync(DotNetProcess process, string path, IProgress<CaptureProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(process);
        var fullPath = Path.GetFullPath(path);
        var directory = DirectoryOf(fullPath);
        progress?.Report(new CaptureProgress("Checking"));
        Directory.CreateDirectory(directory);
        var estimate = Preflight(process, fullPath);
        var partial = fullPath + PartialSuffix;
        if (File.Exists(fullPath) || File.Exists(partial))
        {
            throw new DumpFileExistsException($"{fullPath} already exists; HeapScope never overwrites a dump. Choose another file name.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        progress?.Report(new CaptureProgress(
            "Writing dump",
            estimate,
            $"{process.Name} is paused while about {estimate / (1024.0 * 1024):N0} MB are written"));

        // Cancellation cannot stop the runtime's createdump; it is honored once the runtime has finished, by deleting
        // the file (sprint 1.1 review H1).
        using var canceling = cancellationToken.Register(() => progress?.Report(new CaptureProgress(
            "Canceling",
            estimate,
            "The runtime finishes writing the dump, then HeapScope deletes it")));
        try
        {
            await _writer.WriteAsync(process.ProcessId, partial).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            DeleteWhenReleased(partial);
            throw Translate(process, directory, ex);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            DeleteWhenReleased(partial);
            cancellationToken.ThrowIfCancellationRequested();
        }

        if (!File.Exists(partial))
        {
            throw new DumpWriteFailedException($"The runtime reported success but no dump was written to {fullPath}.");
        }

        try
        {
            File.Move(partial, fullPath, overwrite: false);
        }
        catch (IOException ex)
        {
            DeleteWhenReleased(partial);
            throw new DumpFileExistsException($"{fullPath} appeared while the dump was written; the new dump was removed. Capture again with another name.", ex);
        }
    }

    private static string DirectoryOf(string path) =>
        Path.GetDirectoryName(Path.GetFullPath(path))
        ?? throw new ArgumentException($"{path} is not a file path; give a file name for the dump.", nameof(path));

    private Exception Translate(DotNetProcess process, string directory, Exception ex)
    {
        if (ex is UnauthorizedAccessException || (ex is ServerNotAvailableException && process.Elevation == ElevationState.Elevated))
        {
            return ElevationRequiredException.For(process.ProcessId, process.Name, ex);
        }

        if (ex is ServerNotAvailableException)
        {
            return new TargetUnavailableException($"{process.Name} (pid {process.ProcessId}) did not answer on its diagnostics port; it may have exited.", ex);
        }

        if (ex is DiagnosticsClientException)
        {
            // The dump is written by createdump under the target's own account, not HeapScope's.
            var reason = ex.Message.Contains("0x80070070", StringComparison.OrdinalIgnoreCase) ? "the disk is full"
                : ex.Message.Contains("0x80070005", StringComparison.OrdinalIgnoreCase) ? $"the account {process.Name} runs as cannot write to {directory}"
                : "the runtime refused the request";
            return new DumpWriteFailedException(
                $"The dump of {process.Name} (pid {process.ProcessId}) could not be written: {reason}. The target process writes the dump itself, under its own account; choose a folder it can write to (--out), and check free space. Details: {ex.Message}",
                ex);
        }

        return ex is IOException
            ? new DumpWriteFailedException($"The dump could not be written to {directory}: {ex.Message}", ex)
            : ex;
    }

    /// <summary>Deletes a dump file; if the runtime still holds it, retries for up to 30 seconds in the background.</summary>
    private static void DeleteWhenReleased(string path)
    {
        if (TryDelete(path))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(250).ConfigureAwait(false);
                if (TryDelete(path))
                {
                    return;
                }
            }
        });
    }

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return !File.Exists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
