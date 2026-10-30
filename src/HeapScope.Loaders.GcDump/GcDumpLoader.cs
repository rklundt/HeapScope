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
using System.Runtime.InteropServices;
using Graphs;
using HeapScope.Core;
using HeapScope.Core.Graph;
using HeapScope.Core.Loading;

namespace HeapScope.Loaders.GcDump;

/// <summary>
/// Loads a gcdump (PerfView's format, written by HeapScope, dotnet-gcdump or PerfView) into a <see cref="HeapGraph"/>
/// (sprint 1.8, ARCHITECTURE.md 5.2). The file holds a graph of nodes: objects (with addresses, types, sizes and
/// references) below a tree of named pseudo nodes that stand for the roots ("[static vars]", "[local vars]", handles).
/// Objects become graph objects; the pseudo nodes become root kinds. A gcdump has no generations, field values or string
/// text, and records only objects that survived the collection it forced.
/// </summary>
public sealed class GcDumpLoader : ISnapshotLoader
{
    private const int CheckInterval = 1 << 16;

    /// <summary>The first gcdump semantics (sprint 1.8).</summary>
    public const int CurrentVersion = 1;

    public SnapshotKind Kind => SnapshotKind.GcDump;

    public int Version => CurrentVersion;

    public LoadResult Load(string path, IProgress<LoadProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"The gcdump {fullPath} does not exist.", fullPath);
        }

        if (SnapshotFileSignature.Detect(fullPath) != SnapshotKind.GcDump)
        {
            throw new SnapshotFormatException(SnapshotFileSignature.DescribeUnknown(fullPath));
        }

        var stopwatch = Stopwatch.StartNew();
        progress?.Report(new LoadProgress("Reading gcdump", 0, null));
        var dump = Read(fullPath, progress, cancellationToken);
        var source = dump.MemoryGraph;
        var open = stopwatch.Elapsed;
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return Build(fullPath, dump, source, stopwatch, open, progress, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException and not InsufficientExecutionStackException and not SnapshotFormatException)
        {
            // A file the reader accepted whose tables disagree (a child or type index past its table): crafted or damaged,
            // refused with a reason, never a crash (sprint 3.7, peer review P13; found by the mutation test).
            throw new SnapshotFormatException($"{fullPath} is not a consistent gcdump ({ex.Message}). The file may be damaged or crafted; capture another gcdump.", ex);
        }
    }

    private static LoadResult Build(string fullPath, GCHeapDump dump, MemoryGraph source, Stopwatch stopwatch, TimeSpan open, IProgress<LoadProgress>? progress, CancellationToken cancellationToken)
    {
        using var builder = new HeapGraphBuilder();
        var (nodesByObject, undefined, unknownTypes) = AddObjects(source, builder, progress, cancellationToken);
        builder.FreezeObjects(cancellationToken);
        var objects = stopwatch.Elapsed - open;

        AddReferences(source, builder, nodesByObject, new FileInfo(fullPath).Length, progress, cancellationToken);
        var references = stopwatch.Elapsed - open - objects;

        progress?.Report(new LoadProgress("Reading roots", 0, null));
        var staticRoots = AddRoots(source, builder, cancellationToken);
        var roots = stopwatch.Elapsed - open - objects - references;

        progress?.Report(new LoadProgress("Building graph", 0, null));
        var graph = builder.Build(cancellationToken);
        var build = stopwatch.Elapsed - open - objects - references - roots;

        // Sampling (PerfView can write sampled gcdumps) leaves out objects and scales counts; nodes that were referenced
        // but never described, or objects of types the writer never saw described ("UNKNOWN 0x..."), mean the runtime's
        // events were lost (review M13). Either way the graph is not complete.
        var capabilities = SnapshotCapabilities.ObjectGraph | SnapshotCapabilities.ObjectAddresses;
        if (!IsSampled(dump) && undefined == 0 && unknownTypes == 0)
        {
            capabilities |= SnapshotCapabilities.CompleteGraph | SnapshotCapabilities.ExactCounts;
        }

        var info = new SnapshotInfo(
            SnapshotKind.GcDump,
            fullPath,
            string.IsNullOrEmpty(dump.ProcessName) ? null : dump.ProcessName,
            dump.ProcessID > 0 ? dump.ProcessID : null,
            null,
            source.Is64Bit ? 8 : 4,
            dump.TimeCollected == default ? null : new DateTimeOffset(dump.TimeCollected.ToUniversalTime(), TimeSpan.Zero));
        return new LoadResult(
            graph,
            info,
            capabilities,
            new LoadDiagnostics([], undefined, staticRoots),
            new LoadTimings(open, objects, references, roots, build));
    }

    /// <summary>True when the gcdump was sampled: its counts are scaled estimates, not every object is present.</summary>
    internal static bool IsSampled(GCHeapDump dump) =>
        dump.AverageCountMultiplier is not 1 and not 0
        || dump.AverageSizeMultiplier is not 1 and not 0
        || (dump.CountMultipliersByType is { } multipliers && multipliers.Any(m => m is not 1 and not 0));

    private static GCHeapDump Read(string path, IProgress<LoadProgress>? progress, CancellationToken cancellationToken)
    {
        try
        {
            // The vendored reader reads the whole file at once; the stream lets that read report progress and stop on
            // cancellation (review M9).
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
            using var stream = new ObservedReadStream(file, read => progress?.Report(new LoadProgress("Reading gcdump", read, file.Length)), cancellationToken);
            return new GCHeapDump(stream, path);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not OperationCanceledException)
        {
            // The vendored reader fails with whatever its stream code hits on a damaged file.
            throw new SnapshotFormatException($"{path} could not be read as a gcdump ({ex.Message}). The file may be truncated or damaged; capture another gcdump.", ex);
        }
    }

    /// <summary>Adds every node with an address and a description. Returns the node of each object, in address order.</summary>
    private static (int[] NodesByObject, long Undefined, int UnknownTypes) AddObjects(MemoryGraph source, HeapGraphBuilder builder, IProgress<LoadProgress>? progress, CancellationToken cancellationToken)
    {
        var unknownTypes = 0;
        var limit = (int)source.NodeIndexLimit;
        var node = source.AllocNodeStorage();
        var type = source.AllocTypeNodeStorage();
        var typeIndices = new int[(int)source.NodeTypeIndexLimit];
        Array.Fill(typeIndices, -1);
        var addresses = new List<ulong>();
        var nodes = new List<int>();
        var comPointers = new HashSet<int>();
        long undefined = 0;
        for (var i = 0; i < limit; i++)
        {
            if (i % CheckInterval == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new LoadProgress("Reading objects", i, limit));
            }

            var index = (NodeIndex)i;
            var address = source.GetAddress(index);
            if (address == 0)
            {
                continue; // a pseudo node: the root tree, static variables, COM wrappers
            }

            if (!source.IsDefined(index))
            {
                undefined++; // referenced, never described: its events were lost (or a COM pointer, subtracted below)
                continue;
            }

            source.GetNode(index, node);
            source.GetType(node.TypeIndex, type);
            var rawName = type.Name ?? string.Empty;
            if (TypeNames.IsComCallableWrapper(rawName))
            {
                continue; // a pseudo node at the native IUnknown address (review H4); the root walk passes through it
            }

            // The writer stores sizes as int: objects of 2 to 4 GB come back negative (review M10).
            ulong size = (uint)node.Size;
            if (TypeNames.IsRuntimeCallableWrapper(rawName))
            {
                // The writer adds 1,000 bytes for the RCW's native part and a child at the COM object's IUnknown, which is
                // never described (review H4).
                size = size >= TypeNames.RcwOverhead ? size - TypeNames.RcwOverhead : size;
                var last = NodeIndex.Invalid;
                for (var child = node.GetFirstChildIndex(); child != NodeIndex.Invalid; child = node.GetNextChildIndex())
                {
                    last = child;
                }

                if (last != NodeIndex.Invalid && !source.IsDefined(last) && source.GetAddress(last) != 0)
                {
                    comPointers.Add((int)last);
                }
            }

            var typeIndex = (int)node.TypeIndex;
            if (typeIndices[typeIndex] < 0)
            {
                var name = TypeNames.Normalize(rawName.Length > 0 ? rawName : $"<unknown type {typeIndex}>");
                if (TypeNames.IsUnknown(rawName))
                {
                    unknownTypes++;
                }

                typeIndices[typeIndex] = builder.Types.GetOrAdd(
                    name,
                    module: string.IsNullOrEmpty(type.ModuleName) ? null : RecordedPath.FileName(type.ModuleName),
                    kind: TypeNames.KindOf(name));
            }

            builder.AddObject(address, typeIndices[typeIndex], size, HeapKind.Unknown);
            addresses.Add(address);
            nodes.Add(i);
        }

        undefined -= comPointers.Count;

        progress?.Report(new LoadProgress("Reading objects", limit, limit));

        // The builder orders objects by address when it freezes; the nodes are put in the same order.
        var sortedAddresses = addresses.ToArray();
        var nodesByObject = nodes.ToArray();
        Array.Sort(sortedAddresses, nodesByObject);
        return (nodesByObject, undefined, unknownTypes);
    }

    /// <param name="edgeBudget">The file's length: every reference takes at least a byte of it, so more references than
    /// bytes mean nodes share their child lists, which only a crafted file does (sprint 3.7, peer review P13: billions of
    /// references from a file of a few megabytes).</param>
    private static void AddReferences(MemoryGraph source, HeapGraphBuilder builder, int[] nodesByObject, long edgeBudget, IProgress<LoadProgress>? progress, CancellationToken cancellationToken)
    {
        var node = source.AllocNodeStorage();
        var targets = new List<ulong>();
        long edges = 0;
        for (var objectIndex = 0; objectIndex < nodesByObject.Length; objectIndex++)
        {
            if (objectIndex % CheckInterval == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new LoadProgress("Reading references", objectIndex, nodesByObject.Length));
            }

            source.GetNode((NodeIndex)nodesByObject[objectIndex], node);
            targets.Clear();
            for (var child = node.GetFirstChildIndex(); child != NodeIndex.Invalid; child = node.GetNextChildIndex())
            {
                if ((long)child < 0 || (long)child >= (long)source.NodeIndexLimit)
                {
                    throw new SnapshotFormatException($"The gcdump references node {(long)child}, past its {(long)source.NodeIndexLimit:N0} nodes. The file is damaged or crafted; capture another gcdump.");
                }

                if (++edges > edgeBudget)
                {
                    throw new SnapshotFormatException($"The gcdump states more references than its {edgeBudget:N0} bytes can hold. The file is damaged or crafted; capture another gcdump.");
                }

                if ((edges & 0xFFFF) == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                // Pseudo nodes have no address; undescribed nodes (COM pointers, lost events) are not objects.
                var address = source.GetAddress(child);
                if (address != 0 && source.IsDefined(child))
                {
                    targets.Add(address);
                }
            }

            if (targets.Count > 0)
            {
                builder.AddReferences(objectIndex, CollectionsMarshal.AsSpan(targets));
            }
        }

        progress?.Report(new LoadProgress("Reading references", nodesByObject.Length, nodesByObject.Length));
    }

    /// <summary>
    /// Walks the root tree: every object below a pseudo node is a root of the kind the nearest named pseudo node stands
    /// for. Returns the number of static field roots.
    /// </summary>
    private static int AddRoots(MemoryGraph source, HeapGraphBuilder builder, CancellationToken cancellationToken)
    {
        if (source.RootIndex == NodeIndex.Invalid)
        {
            return 0;
        }

        var type = source.AllocTypeNodeStorage();
        var visited = new HashSet<NodeIndex>();
        var pending = new Stack<(NodeIndex Node, RootClass Class)>();
        pending.Push((source.RootIndex, RootClass.Other));
        var statics = 0;
        var checks = 0;
        while (pending.Count > 0)
        {
            var (index, inherited) = pending.Pop();
            if (!visited.Add(index))
            {
                continue;
            }

            // Child storage is per pseudo node: the walk below pushes children before reading the next node.
            var node = source.GetNode(index, source.AllocNodeStorage());
            source.GetType(node.TypeIndex, type);
            var rootClass = RootClass.Of(type.Name, inherited);
            for (var child = node.GetFirstChildIndex(); child != NodeIndex.Invalid; child = node.GetNextChildIndex())
            {
                if (++checks % CheckInterval == 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                var address = source.GetAddress(child);
                if (address == 0 || IsComCallableWrapper(source, child, type))
                {
                    pending.Push((child, rootClass));
                    continue;
                }

                if (!source.IsDefined(child))
                {
                    continue;
                }

                builder.AddRoot(address, rootClass.Kind, rootClass.Flags, rootClass.Detail);
                if (rootClass.Kind == RootKind.StaticField)
                {
                    statics++;
                }
            }
        }

        return statics;
    }

    private static bool IsComCallableWrapper(MemoryGraph source, NodeIndex index, NodeType storage)
    {
        if (!source.IsDefined(index))
        {
            return false;
        }

        source.GetType(source.GetNode(index, source.AllocNodeStorage()).TypeIndex, storage);
        return TypeNames.IsComCallableWrapper(storage.Name ?? string.Empty);
    }
}
