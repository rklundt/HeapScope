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

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime;
using HeapScope.Contracts;
using HeapScope.Core;
using HeapScope.Core.Graph;
using HeapScope.Core.Loading;
using HeapScope.Storage;
using HeapScope.Worker.Rpc;
using Microsoft.Extensions.Logging;

namespace HeapScope.Worker.Snapshots;

/// <summary>
/// The snapshots loaded in this worker (ARCHITECTURE.md 4.1). Opening reuses a valid index cache or loads the dump with
/// ClrMD and writes the cache; the memory mode follows the budget (ADR-015). Loads are serialized: two first loads would
/// stack their peaks.
/// </summary>
internal sealed partial class SnapshotStore(WorkerOptions options, IndexCacheLocator locator, ILogger<SnapshotStore> logger) : IDisposable
{
    private readonly ConcurrentDictionary<Guid, Entry> _snapshots = new();
    private readonly SemaphoreSlim _loadGate = new(1, 1);
    private readonly SnapshotKinds _kinds = new(options);
    private readonly ReverseIndexBuilder _reverseBuilder = new(logger);

    /// <summary>The memory reserved by large derived computations of every snapshot (sprint 2.3 review M2).</summary>
    private readonly MemoryReservations _reservations = new();

    private DominatorIndexBuilder? _dominatorBuilder;

    private DominatorIndexBuilder DominatorBuilder => _dominatorBuilder ??= new(logger, _reservations);

    private bool _disposed;

    public int Count => _snapshots.Count;

    /// <summary>
    /// Looks at a file without loading it (sprint 1.2 review): size, index cache state (header and fingerprint, no
    /// section hashing) and whether it is open. Application uses it to decide the memory warning.
    /// </summary>
    public SnapshotProbe Probe(string requestedPath)
    {
        var path = ExistingFullPath(requestedPath);
        var cachePath = locator.GetCachePath(path);
        var failure = IndexCacheReader.Probe(cachePath, SnapshotFingerprint.Compute(path));
        var state = failure is null ? SnapshotCacheState.Valid
            : failure.Reason == IndexCacheInvalidReason.Missing ? SnapshotCacheState.Missing
            : SnapshotCacheState.Invalid;
        return new SnapshotProbe(path, new FileInfo(path).Length, state, cachePath, failure?.Detail, FindOpen(path) is not null);
    }

    public async Task<SnapshotDescriptor> OpenAsync(OpenSnapshotRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var path = ExistingFullPath(request.Path);
        await _loadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (FindOpen(path) is { } open)
            {
                if (!request.Rebuild)
                {
                    return open.Descriptor;
                }

                throw new ArgumentException($"{path} is open. Close it before rebuilding its index cache.", nameof(request));
            }

            var entry = await Task.Run(() => Open(path, request, progress, cancellationToken), cancellationToken).ConfigureAwait(false);
            if (_disposed)
            {
                entry.Snapshot.Dispose();
                throw new ObjectDisposedException(nameof(SnapshotStore));
            }

            _snapshots[entry.Descriptor.SnapshotId] = entry;
            return entry.Descriptor;
        }
        finally
        {
            _loadGate.Release();
        }
    }

    public bool Close(Guid snapshotId)
    {
        if (!_snapshots.TryRemove(snapshotId, out var entry))
        {
            return false;
        }

        // Never blocks: outstanding leases (result sets, running queries) keep the graph until they are released.
        entry.Derived.Close();
        entry.Snapshot.Dispose();
        logger.LogInformation("Snapshot {Id} closed", snapshotId);
        return true;
    }

    /// <summary>
    /// The type aggregation of a loaded snapshot, computed once on first use (one parallel pass over the objects) and
    /// kept with the snapshot until it is closed (sprint 1.3). The computation is shared: it holds its own lease and runs
    /// until the snapshot closes, so a caller that cancels (a superseded query) only stops waiting. Returns null when the
    /// snapshot is not loaded or closes meanwhile.
    /// </summary>
    public async Task<Analysis.TypeAggregation?> GetAggregationAsync(Guid snapshotId, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        if (!_snapshots.TryGetValue(snapshotId, out var entry))
        {
            return null;
        }

        using var waiter = entry.Derived.JoinAggregation(lifetime => Task.Run(
            () =>
            {
                using var lease = entry.Snapshot.AcquireLease();
                var stopwatch = Stopwatch.StartNew();
                var aggregation = Analysis.TypeAggregation.Compute(entry.Snapshot.Graph, progress, lifetime);
                logger.LogInformation("Snapshot {Id} aggregated by type in {Seconds:N2} s", snapshotId, stopwatch.Elapsed.TotalSeconds);
                return aggregation;
            },
            lifetime));
        try
        {
            return await waiter.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ObjectDisposedException || (ex is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            // Closed before or during the computation.
            return null;
        }
    }

    /// <summary>The snapshot, or null when it is not loaded in this worker.</summary>
    public ISnapshot? TryGet(Guid snapshotId) => _snapshots.TryGetValue(snapshotId, out var entry) ? entry.Snapshot : null;

    public void Dispose()
    {
        // The load gate is not disposed: a load in flight still releases it, then sees the flag and drops its result.
        _disposed = true;
        foreach (var id in _snapshots.Keys)
        {
            Close(id);
        }
    }

    private static string ExistingFullPath(string? requested)
    {
        if (string.IsNullOrWhiteSpace(requested))
        {
            throw new ArgumentException("A snapshot path is required.", nameof(requested));
        }

        var path = Path.GetFullPath(requested);
        return File.Exists(path) ? path : throw new ArgumentException($"The file {path} does not exist.", nameof(requested));
    }

    private Entry? FindOpen(string path)
    {
        foreach (var entry in _snapshots.Values)
        {
            if (string.Equals(entry.Descriptor.Path, path, StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }

        return null;
    }

    private Entry Open(string path, OpenSnapshotRequest request, IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var throttled = new Rpc.ThrottledProgress<JobProgress>(progress);
        var notes = new List<string>();
        var forced = request.Mode switch
        {
            SnapshotMemoryMode.InMemory => MemoryMode.InMemory,
            SnapshotMemoryMode.Mapped => MemoryMode.Mapped,
            _ => (MemoryMode?)null,
        };

        // Only a dump load (or a failed one) leaves large garbage behind; a cache hit allocates exactly its graph, so
        // the clean-up collection, which pauses queries on other snapshots, is skipped then (sprint 1.2 review).
        var collect = true;
        try
        {
            throttled.Report(new JobProgress("Checking index cache", 0, null));
            var fingerprint = SnapshotFingerprint.Compute(path);
            var cachePath = locator.GetCachePath(path);
            IndexCacheOpenResult? cached = null;
            if (!request.Rebuild)
            {
                cached = TryOpenCache(cachePath, fingerprint, forced, notes, cancellationToken);
            }

            if (cached is null && request.RequireCache)
            {
                collect = false;
                throw new SnapshotChangedException(
                    $"{path} changed since it was opened, or its index cache is gone, so it was not reopened automatically. Open it again to load the dump.");
            }

            collect = cached is null;

            string? writtenCache = cachePath;
            HeapGraph graph;
            IndexCacheMetadata metadata;
            MemoryMode mode;
            ulong identity;
            var fromCache = cached is not null;
            if (cached is not null)
            {
                graph = cached.Graph!;
                metadata = cached.Metadata!;
                mode = cached.Mode;
                identity = cached.Identity;
            }
            else
            {
                (graph, metadata, mode, writtenCache, identity) = LoadDump(path, cachePath, fingerprint, forced, notes, throttled, cancellationToken);

                // LoadDump collected before choosing the mode (and again in the mapped transition).
                collect = false;
            }

            ISnapshot snapshot;
            SnapshotDescriptor descriptor;
            try
            {
                AddDataQualityNotes(graph, metadata, notes);

                // The snapshot is the file that was opened: a dump moved or copied together with its index cache keeps a
                // valid fingerprint, but the cache records the path of its first load (sprint 1.2 review).
                var info = metadata.Info with { SourcePath = path };
                snapshot = _kinds.For(info.Kind).Open(info, metadata.Capabilities, graph, path, () => EnsureUnchanged(path, fingerprint));
                descriptor = Describe(snapshot, mode, fromCache, writtenCache, identity, stopwatch.Elapsed, notes);
            }
            catch
            {
                graph.Dispose();
                throw;
            }

            throttled.Flush();
            logger.LogInformation(
                "Snapshot {Id} opened in {Seconds:N1} s ({Source}, {Mode}): {Objects} objects, {References} references",
                descriptor.SnapshotId,
                descriptor.OpenSeconds,
                fromCache ? "index cache" : "dump",
                mode,
                descriptor.ObjectCount,
                descriptor.ReferenceCount);
            return new Entry(snapshot, descriptor, new IndexState(writtenCache, identity, fingerprint, mode, forced));
        }
        finally
        {
            if (collect)
            {
                // A failed or canceled load leaves builder buffers, ClrMD caches and arrays behind; return them to the
                // OS (ARCHITECTURE.md 9). A successful load already collected inside LoadDump.
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            }
        }
    }

    /// <summary>
    /// Field values and strings are read at the graph's addresses, so they must come from the dump the graph was built
    /// from: the reader opens the dump lazily, possibly hours after the snapshot opened (sprint 1.5 review H5).
    /// </summary>
    private static void EnsureUnchanged(string path, SnapshotFingerprint opened)
    {
        SnapshotFingerprint now;
        try
        {
            now = SnapshotFingerprint.Compute(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SnapshotChangedException($"The dump {path} cannot be read any more ({ex.Message}), so field values are not available. Put the dump back, or close the snapshot.", ex);
        }

        if (now != opened)
        {
            throw new SnapshotChangedException($"The dump {path} changed since this snapshot was opened, so its field values would not match. Close the snapshot and open the dump again.");
        }
    }

    private IndexCacheOpenResult? TryOpenCache(string cachePath, SnapshotFingerprint fingerprint, MemoryMode? forced, List<string> notes, CancellationToken cancellationToken)
    {
        var required = IndexCacheReader.GetInMemoryBytes(cachePath);
        if (required is null)
        {
            return null;
        }

        var mode = MemoryModeSelector.Select(required.Value, MemoryBudget.Current(forced: forced));
        var result = IndexCacheReader.Open(cachePath, fingerprint, mode, cancellationToken);
        if (result.Success && result.Metadata!.LoaderVersion < _kinds.For(result.Metadata.Info.Kind).LoaderVersion)
        {
            // Built by an older loader whose answers are now known to be wrong in some cases (review M7).
            result.Graph!.Dispose();
            logger.LogInformation("Index cache {Path} not used: loader version {Old}, current {New}", cachePath, result.Metadata.LoaderVersion, _kinds.For(result.Metadata.Info.Kind).LoaderVersion);
            notes.Add("The index cache was rebuilt: it was written by an older version of HeapScope, whose results for this snapshot could differ.");
            return null;
        }

        if (result.Success)
        {
            return result;
        }

        if (result.Reason != IndexCacheInvalidReason.Missing)
        {
            logger.LogInformation("Index cache {Path} not used: {Reason} ({Detail})", cachePath, result.Reason, result.Detail);
            notes.Add($"The index cache was rebuilt: {result.Detail}");
        }

        return null;
    }

    private (HeapGraph Graph, IndexCacheMetadata Metadata, MemoryMode Mode, string? CachePath, ulong Identity) LoadDump(
        string path,
        string cachePath,
        SnapshotFingerprint fingerprint,
        MemoryMode? forced,
        List<string> notes,
        Rpc.ThrottledProgress<JobProgress> progress,
        CancellationToken cancellationToken)
    {
        var loader = _kinds.ForFile(path).CreateLoader();
        LoadResult? loaded = loader.Load(path, new LoadProgressAdapter(progress), cancellationToken);
        HeapGraph? graph = loaded.Graph;
        var metadata = new IndexCacheMetadata(
            loaded.Info,
            loaded.Capabilities,
            IndexCacheMetadata.CurrentWriterVersion,
            loaded.Diagnostics.DebuggableModules,
            loaded.Diagnostics.UnreadableObjects,
            loader.Version);

        // Nothing else may reference the built graph, or the mapped transition below cannot free it.
        loaded = null;
        try
        {
            progress.Report(new JobProgress("Writing index cache", 0, null));
            var identity = TryWriteCache(cachePath, graph, metadata, fingerprint, notes, cancellationToken);
            var written = identity != 0;

            // Decide the mode after the load's garbage is gone, counting the graph's own memory as available: if the
            // snapshot moved to mapped mode, that memory would be freed (sprint 1.2 review). No LOH compaction here: it
            // would copy the live graph's arrays; an aggressive collection already returns the freed regions.
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            var budget = MemoryBudget.Current(forced: forced);
            budget = budget with { AvailableBytes = budget.AvailableBytes + graph.ColumnBytes };
            var mode = MemoryModeSelector.Select(graph.ColumnBytes, budget);
            logger.LogInformation(
                "Memory mode {Mode}: graph {GraphMb:N0} MB, budget {BudgetMb:N0} MB of {TotalMb:N0} MB physical",
                mode,
                graph.ColumnBytes / (1024 * 1024),
                budget.BudgetBytes / (1024 * 1024),
                budget.TotalPhysicalBytes / (1024 * 1024));
            if (mode == MemoryMode.InMemory || !written)
            {
                if (mode == MemoryMode.Mapped)
                {
                    notes.Add("The snapshot is held in memory because its index cache could not be written; memory use is higher than usual.");
                }

                return (graph, metadata, MemoryMode.InMemory, written ? cachePath : null, identity);
            }

            // Switch to mapped mode (ADR-015): drop the built arrays, compact once so the memory really returns to the
            // OS, then reopen the cache as views.
            progress.Report(new JobProgress("Mapping index cache", 0, null));
            graph.Dispose();
            graph = null;
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            var mapped = IndexCacheReader.Open(cachePath, fingerprint, MemoryMode.Mapped, cancellationToken);
            if (!mapped.Success)
            {
                throw new SnapshotFormatException($"The index cache just written could not be reopened: {mapped.Detail}");
            }

            return (mapped.Graph!, mapped.Metadata!, MemoryMode.Mapped, cachePath, mapped.Identity);
        }
        catch
        {
            graph?.Dispose();
            throw;
        }
    }

    /// <summary>Writes the index cache and returns its identity, or 0 (with a note) when it could not be written.</summary>
    private ulong TryWriteCache(string cachePath, HeapGraph graph, IndexCacheMetadata metadata, SnapshotFingerprint fingerprint, List<string> notes, CancellationToken cancellationToken)
    {
        try
        {
            return IndexCacheWriter.Write(cachePath, graph, metadata, fingerprint, cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Index cache {Path} could not be written", cachePath);
            notes.Add($"The index cache could not be written ({ex.Message}); the next open will load the dump again.");
            return 0;
        }
    }

    private void AddDataQualityNotes(HeapGraph graph, IndexCacheMetadata metadata, List<string> notes)
    {
        if (metadata.DebuggableModules is { Count: > 0 } modules)
        {
            var names = string.Join(", ", modules.Take(5)) + (modules.Count > 5 ? $" and {modules.Count - 5} more" : string.Empty);
            notes.Add($"{modules.Count} module(s) were compiled as debuggable code ({names}). Objects held only by local variables in their methods can look unreachable.");
        }

        if (graph.DroppedReferenceCount > 0 || graph.DroppedRootCount > 0)
        {
            notes.Add(string.Create(CultureInfo.InvariantCulture, $"{graph.DroppedReferenceCount:N0} references and {graph.DroppedRootCount:N0} roots did not point to an object and were left out."));
        }

        _kinds.For(metadata.Info.Kind).AddNotes(metadata, notes);

        if (metadata.UnreadableObjects > 0)
        {
            notes.Add(string.Create(CultureInfo.InvariantCulture, $"{metadata.UnreadableObjects:N0} heap entries could not be read and were left out."));
        }
    }

    private static SnapshotDescriptor Describe(ISnapshot snapshot, MemoryMode mode, bool fromCache, string? cachePath, ulong identity, TimeSpan elapsed, List<string> notes)
    {
        var info = snapshot.Info;
        var graph = snapshot.Graph;
        return new SnapshotDescriptor(
            Guid.NewGuid(),
            info.SourcePath,
            info.Kind.ToDto(),
            snapshot.Capabilities.ToDto(),
            info.ProcessName,
            info.ProcessId,
            info.RuntimeVersion,
            info.CapturedAtUtc,
            graph.ObjectCount,
            graph.EdgeCount,
            graph.Types.Count,
            graph.Roots.Count,
            graph.TotalSize,
            mode == MemoryMode.Mapped ? SnapshotMemoryMode.Mapped : SnapshotMemoryMode.InMemory,
            fromCache,
            cachePath,
            elapsed.TotalSeconds,
            [.. notes],
            identity);
    }

    private sealed record Entry(ISnapshot Snapshot, SnapshotDescriptor Descriptor, IndexState Index)
    {
        public DerivedData Derived { get; } = new();
    }

    private sealed class LoadProgressAdapter(Rpc.ThrottledProgress<JobProgress> target) : IProgress<LoadProgress>
    {
        public void Report(LoadProgress value) => target.Report(new JobProgress(value.Phase, value.Completed, value.Total));
    }
}
