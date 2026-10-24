using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using HeapScope.Core;
using HeapScope.Core.Graph;
using Microsoft.Diagnostics.Runtime;

namespace HeapScope.Loaders.ClrMd;

/// <summary>
/// Loads a full dump into the compact graph with ClrMD (ARCHITECTURE.md 6.1 and 6.3): an object pass per segment
/// (types, sizes, heap kinds, finalizer flags), dependent handles as edges, a reference pass in address order,
/// then roots (GC roots, weak handles and static fields). Synchronous and CPU bound; callers run it on a worker
/// thread. Cancellation is checked at least every 64K objects.
/// </summary>
public sealed class ClrMdDumpLoader(ClrMdLoaderOptions? options = null)
{
    private const int CheckInterval = 1 << 16;

    private readonly ClrMdLoaderOptions _options = options ?? new ClrMdLoaderOptions();

    /// <summary>Loads <paramref name="dumpPath"/>; the caller owns the returned graph.</summary>
    /// <exception cref="RuntimeComponentMissingException">The matching DAC is not on this machine.</exception>
    /// <exception cref="SnapshotFormatException">The file is not a readable .NET dump.</exception>
    public DumpLoadResult Load(string dumpPath, IProgress<LoadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(dumpPath);
        var fullPath = Path.GetFullPath(dumpPath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"The dump {fullPath} does not exist.", fullPath);
        }

        var stopwatch = Stopwatch.StartNew();
        progress?.Report(new LoadProgress("Opening dump", 0, null));
        var header = MinidumpHeader.Read(fullPath);
        using var target = ClrMdDump.OpenDataTarget(fullPath, _options);
        var clr = ClrMdDump.FirstRuntime(target, fullPath);
        using var runtime = ClrMdDump.CreateRuntime(clr, fullPath, _options);
        var heap = runtime.Heap;
        if (!heap.CanWalkHeap)
        {
            throw new SnapshotFormatException("The dump was taken while a garbage collection was running, so its heap cannot be walked. Capture another dump.");
        }

        var open = stopwatch.Elapsed;
        cancellationToken.ThrowIfCancellationRequested();

        using var builder = new HeapGraphBuilder();
        var (unreadable, incompleteSegments) = AddObjects(heap, builder, progress, cancellationToken);
        var objects = stopwatch.Elapsed - open;

        builder.FreezeObjects(cancellationToken);

        // Freezing copied the chunked object buffers into the final columns; about 20 bytes per object of chunks are
        // garbage now. Without an aggressive collection the GC keeps that memory committed through the reference pass,
        // which is where the load peaks (measured in 1.2: about 1.2 GB on the Medium dump).
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        var weakHandles = AddDependentHandlesAndCollectWeak(runtime, builder, cancellationToken);
        AddReferences(heap, builder, progress, cancellationToken);
        var references = stopwatch.Elapsed - open - objects;

        progress?.Report(new LoadProgress("Reading roots", 0, null));
        AddGcRoots(heap, target.DataReader, builder, cancellationToken);
        foreach (var address in weakHandles)
        {
            builder.AddRoot(address, RootKind.WeakHandle, RootFlags.None);
        }

        var staticRoots = AddStaticFieldRoots(runtime, builder, cancellationToken);
        var debuggable = FindDebuggableModules(runtime);
        var roots = stopwatch.Elapsed - open - objects - references;

        progress?.Report(new LoadProgress("Building graph", 0, null));
        var graph = builder.Build(cancellationToken);
        var build = stopwatch.Elapsed - open - objects - references - roots;

        var info = new SnapshotInfo(
            SnapshotKind.FullDump,
            fullPath,
            ProcessName(target),
            ProcessId(target),
            clr.Version.ToString(),
            target.DataReader.PointerSize,
            header.CapturedAtUtc);

        // A heap that could not be read completely must not claim complete, exact answers (ARCHITECTURE.md 5.2).
        var capabilities = unreadable > 0 || incompleteSegments > 0
            ? SnapshotCapabilities.FullDump & ~(SnapshotCapabilities.CompleteGraph | SnapshotCapabilities.ExactCounts)
            : SnapshotCapabilities.FullDump;
        return new DumpLoadResult(
            graph,
            info,
            capabilities,
            new LoadDiagnostics(debuggable, unreadable, staticRoots, incompleteSegments),
            new LoadTimings(open, objects, references, roots, build));
    }

    /// <summary>
    /// A segment walk is incomplete when the objects walked (free objects included) miss more than this share of its
    /// object range and more than <see cref="IncompleteSegmentBytes"/>. Unfilled allocation contexts, which ClrMD skips,
    /// make up small gaps (measured on the Roslyn dump: 15 KB of a 24 KB gen0 region, 1 to 3 percent elsewhere).
    /// </summary>
    internal const double IncompleteSegmentShare = 0.05;

    /// <summary>The absolute part of the incomplete-segment test.</summary>
    internal const ulong IncompleteSegmentBytes = 1024 * 1024;

    private static (long Unreadable, int IncompleteSegments) AddObjects(ClrHeap heap, HeapGraphBuilder builder, IProgress<LoadProgress>? progress, CancellationToken cancellationToken)
    {
        var finalizable = FinalizableObjects(heap, cancellationToken);
        var typeIndices = new Dictionary<ulong, int>();
        long total = 0;
        foreach (var segment in heap.Segments)
        {
            total += (long)segment.ObjectRange.Length;
        }

        long done = 0, unreadable = 0, count = 0;
        var incomplete = 0;
        foreach (var segment in heap.Segments)
        {
            var fixedKind = FixedHeapKind(segment.Kind);
            ulong walked = 0;
            foreach (var obj in segment.EnumerateObjects(carefully: false))
            {
                if (++count % CheckInterval == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    progress?.Report(new LoadProgress("Reading objects", done + (long)(obj.Address - segment.ObjectRange.Start), total));
                }

                walked += obj.Size;
                if (obj.IsFree)
                {
                    continue;
                }

                var type = obj.Type;
                if (type is null)
                {
                    unreadable++;
                    continue;
                }

                if (!typeIndices.TryGetValue(type.MethodTable, out var typeIndex))
                {
                    typeIndex = builder.Types.GetOrAdd(
                        type.Name ?? $"<unknown type 0x{type.MethodTable:x}>",
                        type.MethodTable,
                        (uint)type.StaticSize,
                        (uint)type.ComponentSize,
                        type.Module?.Name is { } module ? Path.GetFileName(module) : null,
                        KindOf(type));
                    typeIndices.Add(type.MethodTable, typeIndex);
                }

                var kind = fixedKind ?? GenerationKind(segment.GetGeneration(obj.Address));
                var flags = finalizable.Contains(obj.Address) && !type.IsFinalizeSuppressed(obj.Address) ? ObjectFlags.HasFinalizer : ObjectFlags.None;
                builder.AddObject(obj.Address, typeIndex, obj.Size, kind, flags);
            }

            done += (long)segment.ObjectRange.Length;
            var missing = segment.ObjectRange.Length > walked ? segment.ObjectRange.Length - walked : 0;
            if (missing > IncompleteSegmentBytes && missing > segment.ObjectRange.Length * IncompleteSegmentShare)
            {
                incomplete++;
            }
        }

        progress?.Report(new LoadProgress("Reading objects", total, total));
        return (unreadable, incomplete);
    }

    private static HashSet<ulong> FinalizableObjects(ClrHeap heap, CancellationToken cancellationToken)
    {
        var result = new HashSet<ulong>();
        foreach (var obj in heap.EnumerateFinalizableObjects())
        {
            if (result.Count % CheckInterval == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            result.Add(obj.Address);
        }

        return result;
    }

    private static List<ulong> AddDependentHandlesAndCollectWeak(ClrRuntime runtime, HeapGraphBuilder builder, CancellationToken cancellationToken)
    {
        var weak = new List<ulong>();
        var count = 0;
        foreach (var handle in runtime.EnumerateHandles())
        {
            if (++count % CheckInterval == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            switch (handle.HandleKind)
            {
                case ClrHandleKind.Dependent:
                    if (!handle.Object.IsNull)
                    {
                        builder.AddDependentHandle(handle.Object.Address, handle.Dependent.Address);
                    }

                    break;
                case ClrHandleKind.WeakShort or ClrHandleKind.WeakLong or ClrHandleKind.WeakWinRT:
                    if (!handle.Object.IsNull)
                    {
                        weak.Add(handle.Object.Address);
                    }

                    break;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return weak;
    }

    private static void AddReferences(ClrHeap heap, HeapGraphBuilder builder, IProgress<LoadProgress>? progress, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<ulong>.Shared.Rent(1024);
        try
        {
            var count = builder.ObjectCount;
            var addresses = builder.FrozenAddresses;
            for (var i = 0; i < count; i++)
            {
                if (i % CheckInterval == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    progress?.Report(new LoadProgress("Reading references", i, count));
                }

                var used = 0;
                foreach (var target in heap.GetObject(addresses[i]).EnumerateReferenceAddresses(carefully: false, considerDependantHandles: false))
                {
                    if (used == buffer.Length)
                    {
                        var larger = ArrayPool<ulong>.Shared.Rent(buffer.Length * 2);
                        buffer.AsSpan().CopyTo(larger);
                        ArrayPool<ulong>.Shared.Return(buffer);
                        buffer = larger;
                    }

                    buffer[used++] = target;
                }

                if (used > 0)
                {
                    builder.AddReferences(i, buffer.AsSpan(0, used));
                }
            }

            progress?.Report(new LoadProgress("Reading references", count, count));
        }
        finally
        {
            ArrayPool<ulong>.Shared.Return(buffer);
        }
    }

    private static void AddGcRoots(ClrHeap heap, IDataReader reader, HeapGraphBuilder builder, CancellationToken cancellationToken)
    {
        // The static storage bucket is reported both as a strong handle and (on .NET 9 and 10) as a StaticVar root;
        // it is kept once (decision 3).
        var strongHandleObjects = new HashSet<ulong>();
        var staticStorage = new List<ulong>();
        var count = 0;
        foreach (var root in heap.EnumerateRoots())
        {
            if (++count % CheckInterval == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (root.Object.IsNull)
            {
                continue;
            }

            var address = root.Object.Address;
            var flags = RootFlags.Retaining;
            if (root.IsPinned)
            {
                flags |= RootFlags.Pinned;
            }

            if (root.IsInterior)
            {
                // The raw pointer in the stack slot, resolved by the builder's floor search with its bounds check, so a
                // byref into a free gap or outside the heap is dropped instead of rooting the object before it.
                flags |= RootFlags.Interior;
                if (root is ClrStackRoot && root.Address != 0 && reader.ReadPointer(root.Address, out var slot) && slot != 0)
                {
                    address = slot;
                }
            }

            switch (root.RootKind)
            {
                case ClrRootKind.StaticVar:
                    staticStorage.Add(address);
                    break;
                case ClrRootKind.StrongHandle:
                    strongHandleObjects.Add(address);
                    builder.AddRoot(address, RootKind.StrongHandle, flags);
                    break;
                case ClrRootKind.PinnedHandle:
                    builder.AddRoot(address, RootKind.PinnedHandle, flags | RootFlags.Pinned);
                    break;
                case ClrRootKind.AsyncPinnedHandle:
                    builder.AddRoot(address, RootKind.AsyncPinnedHandle, flags | RootFlags.Pinned);
                    break;
                case ClrRootKind.RefCountedHandle:
                    builder.AddRoot(address, RootKind.RefCountedHandle, flags);
                    break;
                case ClrRootKind.FinalizerQueue:
                    builder.AddRoot(address, RootKind.FinalizerQueue, flags);
                    break;
                case ClrRootKind.Stack:
                    builder.AddRoot(address, RootKind.ThreadStack, flags, ThreadDetail(root));
                    break;
                case ClrRootKind.ThreadStaticVar:
                    builder.AddRoot(address, RootKind.Other, flags, "thread static" + ThreadSuffix(root));
                    break;
                default:
                    builder.AddRoot(address, RootKind.Other, flags, root.RootKind.ToString());
                    break;
            }
        }

        foreach (var address in staticStorage)
        {
            if (strongHandleObjects.Add(address))
            {
                builder.AddRoot(address, RootKind.StrongHandle, RootFlags.Retaining, "static storage");
            }
        }
    }

    private static string? ThreadDetail(ClrRoot root) =>
        root is ClrStackRoot { StackFrame.Thread: { } thread }
            ? string.Create(CultureInfo.InvariantCulture, $"thread {thread.ManagedThreadId} (OS 0x{thread.OSThreadId:x})")
            : null;

    private static string ThreadSuffix(ClrRoot root) => ThreadDetail(root) is { } detail ? ", " + detail : string.Empty;

    private static int AddStaticFieldRoots(ClrRuntime runtime, HeapGraphBuilder builder, CancellationToken cancellationToken)
    {
        var added = 0;
        var checks = 0;
        var seen = new HashSet<(ulong Module, ulong Domain)>();
        foreach (var domain in runtime.AppDomains)
        {
            foreach (var module in domain.Modules)
            {
                if (!seen.Add((module.Address, domain.Address)))
                {
                    continue;
                }

                cancellationToken.ThrowIfCancellationRequested();
                foreach (var type in module.EnumerateTypesWithStaticFields())
                {
                    if (++checks % 1024 == 0)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                    }

                    // Statics of collectible types live as long as their loader allocator, not forever: the root is
                    // shown, but does not retain (ARCHITECTURE.md 6.1).
                    var flags = type.IsCollectible ? RootFlags.None : RootFlags.Retaining;
                    foreach (var field in type.StaticFields)
                    {
                        if (!field.IsObjectReference || !field.IsInitialized(domain))
                        {
                            continue;
                        }

                        var value = field.ReadObject(domain);
                        if (value.IsNull || !value.IsValid)
                        {
                            continue;
                        }

                        var before = builder.DroppedRootCount;
                        builder.AddRoot(value.Address, RootKind.StaticField, flags, $"{type.Name}.{field.Name}");
                        if (builder.DroppedRootCount == before)
                        {
                            added++;
                        }
                    }
                }
            }
        }

        return added;
    }

    private static List<string> FindDebuggableModules(ClrRuntime runtime)
    {
        var result = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var module in runtime.EnumerateModules())
        {
            if ((module.DebuggingMode & DebuggableAttribute.DebuggingModes.DisableOptimizations) != 0 && !module.IsDynamic)
            {
                result.Add(Path.GetFileName(module.Name ?? module.AssemblyName ?? "?"));
            }
        }

        return [.. result];
    }

    private static HeapKind? FixedHeapKind(GCSegmentKind kind) => kind switch
    {
        GCSegmentKind.Generation0 => HeapKind.Gen0,
        GCSegmentKind.Generation1 => HeapKind.Gen1,
        GCSegmentKind.Generation2 => HeapKind.Gen2,
        GCSegmentKind.Large => HeapKind.LargeObjectHeap,
        GCSegmentKind.Pinned => HeapKind.PinnedObjectHeap,
        GCSegmentKind.Frozen => HeapKind.Frozen,
        _ => null,
    };

    private static HeapKind GenerationKind(Generation generation) => generation switch
    {
        Generation.Generation0 => HeapKind.Gen0,
        Generation.Generation1 => HeapKind.Gen1,
        Generation.Generation2 => HeapKind.Gen2,
        Generation.Large => HeapKind.LargeObjectHeap,
        Generation.Pinned => HeapKind.PinnedObjectHeap,
        Generation.Frozen => HeapKind.Frozen,
        _ => HeapKind.Unknown,
    };

    private static TypeKind KindOf(ClrType type)
    {
        var kind = TypeKind.None;
        if (type.IsArray)
        {
            kind |= TypeKind.Array;
        }

        if (type.IsString)
        {
            kind |= TypeKind.String;
        }

        if (type.IsValueType)
        {
            kind |= TypeKind.ValueTypeBox;
        }

        if (type.IsException)
        {
            kind |= TypeKind.Exception;
        }

        if (type.BaseType?.Name is "System.MulticastDelegate" or "System.Delegate")
        {
            kind |= TypeKind.Delegate;
        }

        return kind;
    }

    private static string? ProcessName(DataTarget target)
    {
        foreach (var module in target.EnumerateModules())
        {
            // The first module of a process dump is the executable.
            return Path.GetFileNameWithoutExtension(module.FileName);
        }

        return null;
    }

    private static int? ProcessId(DataTarget target) =>
        target.DataReader.ProcessId is var id and > 0 ? id : null;
}
