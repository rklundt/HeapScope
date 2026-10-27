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

using System.Text;
using HeapScope.Core;
using HeapScope.Core.Allocations;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;

namespace HeapScope.Loaders.Trace;

/// <summary>
/// Reads the sampled allocations of an EventPipe trace (sprint 2.5, ADR-028): the runtime's allocation ticks with their
/// types, bytes and call stacks. TraceEvent resolves stacks through an <c>.etlx</c> file, which is written to a private
/// temporary folder and deleted after reading; nothing is written next to the trace. Methods are named from the trace's
/// own rundown, never from a symbol server (ADR-017); runs of native frames become one <see cref="AllocationTrace.NativeFrame"/>.
/// </summary>
public static class AllocationTraceReader
{
    private static readonly byte[] s_magic = Encoding.ASCII.GetBytes("Nettrace");

    /// <summary>Reads <paramref name="path"/>.</summary>
    /// <param name="progress">Phases, in words, for the job's status line.</param>
    /// <exception cref="SnapshotFormatException">The file is not an EventPipe trace or cannot be read.</exception>
    public static AllocationTrace Read(string path, IProgress<string>? progress, CancellationToken cancellationToken) =>
        Read(path, progress, Path.Combine(Path.GetTempPath(), "HeapScope", "etlx"), cancellationToken);

    /// <summary>Reads <paramref name="path"/>, writing the temporary <c>.etlx</c> into <paramref name="temporaryFolder"/> (tests).</summary>
    internal static AllocationTrace Read(string path, IProgress<string>? progress, string temporaryFolder, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var fullPath = Path.GetFullPath(path);
        CheckSignature(fullPath);
        cancellationToken.ThrowIfCancellationRequested();

        var folder = PrivateFiles.CreateDirectory(temporaryFolder);
        var etlx = Path.Combine(folder.FullName, Guid.NewGuid().ToString("N") + ".etlx");
        try
        {
            progress?.Report("Resolving the trace's call stacks");
            try
            {
                TraceLog.CreateFromEventPipeDataFile(fullPath, etlx, new TraceLogOptions { ContinueOnError = true, ConversionLog = TextWriter.Null });
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                throw new SnapshotFormatException($"{fullPath} could not be read as an EventPipe trace: {ex.Message}", ex);
            }

            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report("Reading allocation samples");
            using var log = new TraceLog(etlx);
            return new Builder(log, cancellationToken).Build();
        }
        finally
        {
            TryDelete(etlx);
            TryDelete(Path.ChangeExtension(etlx, ".etlx.log"));
        }
    }

    private static void CheckSignature(string path)
    {
        Span<byte> head = stackalloc byte[32];
        int read;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        }
        catch (FileNotFoundException ex)
        {
            throw new SnapshotFormatException($"{path} does not exist.", ex);
        }

        // The nettrace format starts with its magic, then the FastSerialization header.
        if (read < head.Length || !head[..s_magic.Length].SequenceEqual(s_magic))
        {
            throw new SnapshotFormatException($"{path} is not an EventPipe trace (.nettrace). Record one with HeapScope's Record allocations, or 'heapscope-cli trace --profile allocations'.");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: the temporary folder is the user's alone.
        }
    }

    private sealed class Builder(TraceLog log, CancellationToken cancellationToken)
    {
        private readonly List<string> _frames = [];
        private readonly Dictionary<string, int> _frameIds = new(StringComparer.Ordinal);
        private readonly List<string> _types = [];
        private readonly Dictionary<string, int> _typeIds = new(StringComparer.Ordinal);
        private readonly List<int> _stackFrames = [];
        private readonly List<int> _stackParents = [];
        private readonly Dictionary<(int Parent, int Frame), int> _nodes = [];
        private readonly Dictionary<CallStackIndex, int> _leaves = [];
        private readonly List<double> _times = [];
        private readonly List<int> _stacks = [];
        private readonly List<int> _sampleTypes = [];
        private readonly List<long> _bytes = [];
        private readonly List<int> _scratch = [];
        private long _withoutStack;
        private int _processId;
        private string? _processName;

        public AllocationTrace Build()
        {
            var source = log.Events.GetSource();
            var canceled = false;
            source.Clr.GCAllocationTick += data =>
            {
                if ((_times.Count & 0x3FF) == 0x3FF && cancellationToken.IsCancellationRequested)
                {
                    canceled = true;
                    source.StopProcessing();
                    return;
                }

                Add(data);
            };
            source.Process();
            if (canceled)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var info = new AllocationTraceInfo(
                _processId,
                _processName,
                log.SessionStartTime.ToUniversalTime(),
                log.SessionDuration.TotalMilliseconds,
                log.EventsLost,
                _withoutStack);
            return new AllocationTrace(
                _frames,
                _types,
                [.. _stackFrames],
                [.. _stackParents],
                [.. _times],
                [.. _stacks],
                [.. _sampleTypes],
                [.. _bytes],
                info);
        }

        private void Add(GCAllocationTickTraceData data)
        {
            if (_processId == 0 && data.ProcessID > 0)
            {
                _processId = data.ProcessID;
                _processName = string.IsNullOrEmpty(data.ProcessName) ? null : data.ProcessName;
            }

            var typeName = string.IsNullOrEmpty(data.TypeName) ? "?" : data.TypeName;
            if (!_typeIds.TryGetValue(typeName, out var type))
            {
                type = _types.Count;
                _types.Add(typeName);
                _typeIds.Add(typeName, type);
            }

            var stack = Leaf(data.CallStackIndex());

            _times.Add(data.TimeStampRelativeMSec);
            _stacks.Add(stack);
            _sampleTypes.Add(type);
            _bytes.Add(data.AllocationAmount64);
        }

        /// <summary>The innermost node of a TraceLog stack, adding its frames to the tree the first time.</summary>
        private int Leaf(CallStackIndex index)
        {
            if (index == CallStackIndex.Invalid)
            {
                // Kept in the totals under a frame of its own, so every view adds up to the same bytes.
                _withoutStack++;
                var frame = FrameId(AllocationTrace.NoStackFrame);
                if (!_nodes.TryGetValue((-1, frame), out var none))
                {
                    none = _stackFrames.Count;
                    _stackFrames.Add(frame);
                    _stackParents.Add(-1);
                    _nodes.Add((-1, frame), none);
                }

                return none;
            }

            if (_leaves.TryGetValue(index, out var known))
            {
                return known;
            }

            // Innermost first, with runs of native frames merged into one.
            _scratch.Clear();
            for (var at = index; at != CallStackIndex.Invalid; at = log.CallStacks.Caller(at))
            {
                var frame = Frame(log.CallStacks.CodeAddressIndex(at));
                if (frame == NativeId && _scratch.Count > 0 && _scratch[^1] == NativeId)
                {
                    continue;
                }

                _scratch.Add(frame);
            }

            var node = -1;
            for (var i = _scratch.Count - 1; i >= 0; i--)
            {
                if (!_nodes.TryGetValue((node, _scratch[i]), out var child))
                {
                    child = _stackFrames.Count;
                    _stackFrames.Add(_scratch[i]);
                    _stackParents.Add(node);
                    _nodes.Add((node, _scratch[i]), child);
                }

                node = child;
            }

            _leaves.Add(index, node);
            return node;
        }

        private int NativeId => FrameId(AllocationTrace.NativeFrame);

        private int Frame(CodeAddressIndex address)
        {
            var method = log.CodeAddresses.MethodIndex(address);
            var name = method == MethodIndex.Invalid ? null : log.CodeAddresses.Methods.FullMethodName(method);
            return FrameId(string.IsNullOrEmpty(name) ? AllocationTrace.NativeFrame : name);
        }

        private int FrameId(string name)
        {
            if (!_frameIds.TryGetValue(name, out var id))
            {
                id = _frames.Count;
                _frames.Add(name);
                _frameIds.Add(name, id);
            }

            return id;
        }
    }
}
