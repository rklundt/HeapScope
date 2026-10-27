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
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;

namespace HeapScope.Loaders.Trace;

/// <summary>
/// Reads the sampled allocations of an EventPipe trace (sprint 2.5, ADR-028): the runtime's allocation ticks with their
/// types, kinds, bytes and call stacks. TraceEvent resolves stacks through an <c>.etlx</c> file, which is written to a
/// private temporary folder and deleted after reading; nothing is written next to the trace. Methods are named from the
/// trace's own events (JIT, loader and the rundown), never from a symbol server (ADR-017). EventPipe walks managed frames
/// only, so a frame without a name is a managed method the trace does not name, labeled so and counted (the 2.5 review);
/// whether the rundown arrived and what TraceEvent reported while converting are part of the result.
/// </summary>
public static class AllocationTraceReader
{
    /// <summary>
    /// Changes when how stacks, frames or samples are built changes (the worker's versions line, ARCHITECTURE.md 11), so a
    /// log line can be matched to the rules that produced it. 1: sprint 2.5.
    /// </summary>
    public const int Version = 1;

    /// <summary>TraceEvent's version, for the worker's versions line.</summary>
    public static string LibraryVersion { get; } = typeof(TraceLog).Assembly.GetName().Version?.ToString() ?? "unknown";

    /// <summary>The temporary folder the <c>.etlx</c> files are written to.</summary>
    public static string DefaultTemporaryFolder { get; } = Path.Combine(Path.GetTempPath(), "HeapScope", "etlx");

    private static readonly byte[] s_magic = Encoding.ASCII.GetBytes("Nettrace");

    /// <summary>The allocation tick's event id in the runtime provider.</summary>
    private const int AllocationTickEventId = 10;

    /// <summary>Conversion problems kept for the log.</summary>
    private const int KeptIssues = 3;

    /// <summary>Reads <paramref name="path"/>.</summary>
    /// <param name="progress">Phases, in words, for the job's status line.</param>
    /// <exception cref="SnapshotFormatException">The file is not an EventPipe trace or cannot be read.</exception>
    public static AllocationTrace Read(string path, IProgress<string>? progress, CancellationToken cancellationToken) =>
        Read(path, progress, DefaultTemporaryFolder, cancellationToken);

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
            // TraceEvent's conversion cannot be canceled: a cancel takes effect when it returns (ADR-028).
            progress?.Report("Resolving the trace's call stacks (a cancel takes effect after this step)");
            var conversion = new IssueWriter(KeptIssues);
            try
            {
                TraceLog.CreateFromEventPipeDataFile(fullPath, etlx, new TraceLogOptions { ContinueOnError = true, ConversionLog = conversion });
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
            {
                conversion.EndLine();
                throw new SnapshotFormatException($"{fullPath} could not be read as an EventPipe trace: {ex.Message} The recording may have been cut short; record it again.", ex);
            }

            conversion.EndLine();
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report("Reading allocation samples");
            using var log = new TraceLog(etlx);
            return new Builder(log, conversion.Count, conversion.First, cancellationToken).Build();
        }
        finally
        {
            TryDelete(etlx);
            TryDelete(etlx + ".log");
        }
    }

    /// <summary>
    /// Deletes <c>.etlx</c> files a read left behind (a worker killed mid-read, the 2.5 review): those not written to for
    /// <paramref name="minimumAge"/> and not open elsewhere. Returns the files and bytes removed.
    /// </summary>
    public static (int Files, long Bytes) SweepTemporaryFiles(TimeSpan minimumAge, string? temporaryFolder = null)
    {
        var folder = temporaryFolder ?? DefaultTemporaryFolder;
        if (!Directory.Exists(folder))
        {
            return (0, 0);
        }

        var (files, bytes) = (0, 0L);
        foreach (var file in Directory.EnumerateFiles(folder, "*.etlx*"))
        {
            try
            {
                var info = new FileInfo(file);
                if (DateTime.UtcNow - info.LastWriteTimeUtc < minimumAge)
                {
                    continue;
                }

                // A file another worker is reading cannot be opened exclusively.
                using (new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                }

                var length = info.Length;
                File.Delete(file);
                (files, bytes) = (files + 1, bytes + length);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // In use or already gone.
            }
        }

        return (files, bytes);
    }

    /// <summary>
    /// TraceEvent's conversion log, keeping only what reports a problem: how many lines, and the first few (its own words;
    /// it writes no event payloads). Nothing else is held, however long a damaged trace's log grows (the 2.5 re-review).
    /// </summary>
    private sealed class IssueWriter(int kept) : TextWriter
    {
        private readonly StringBuilder _line = new();

        public override Encoding Encoding => Encoding.UTF8;

        public int Count { get; private set; }

        public List<string> First { get; } = [];

        public override void Write(char value)
        {
            if (value == '\n')
            {
                EndLine();
            }
            else if (_line.Length < 1000)
            {
                _line.Append(value);
            }
        }

        /// <summary>Ends the line in progress (TraceEvent may flush in the middle of one, so a flush does not).</summary>
        public void EndLine()
        {
            var line = _line.ToString().Trim();
            _line.Clear();
            if (line.Contains("error", StringComparison.OrdinalIgnoreCase)
                || line.Contains("warning", StringComparison.OrdinalIgnoreCase)
                || line.Contains("exception", StringComparison.OrdinalIgnoreCase))
            {
                Count++;
                if (First.Count < kept)
                {
                    First.Add(line);
                }
            }
        }
    }

    private static void CheckSignature(string path)
    {
        Span<byte> head = stackalloc byte[32];
        int read;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            read = stream.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new SnapshotFormatException($"{path} does not exist.", ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SnapshotFormatException($"{path} could not be read: {ex.Message} If it is still being recorded, wait until the recording ends.", ex);
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
            // The worker's start sweeps what is left (SweepTemporaryFiles).
        }
    }

    private sealed class Builder(TraceLog log, int issues, List<string> firstIssues, CancellationToken cancellationToken)
    {
        private readonly List<string> _frames = [];
        private readonly Dictionary<string, int> _frameIds = new(StringComparer.Ordinal);
        private readonly List<string> _types = [];
        private readonly Dictionary<string, int> _typeIds = new(StringComparer.Ordinal);
        private readonly List<int> _stackFrames = [];
        private readonly List<int> _stackParents = [];
        private readonly Dictionary<(int Parent, int Frame), int> _nodes = [];
        private readonly Dictionary<CallStackIndex, (int Node, bool Unresolved)> _leaves = [];
        private readonly List<int> _scratch = [];
        private double[] _times = [];
        private int[] _stacks = [];
        private int[] _sampleTypes = [];
        private long[] _bytes = [];
        private AllocationKind[] _kinds = [];
        private int _count;
        private bool _ordered = true;
        private long _withoutStack;
        private long _unresolvedSamples;
        private long _rundownEvents;
        private long _events;
        private int _processId;
        private string? _processName;

        public AllocationTrace Build()
        {
            // Exact arrays from the trace's event counts: no list doubling over millions of samples (the 2.5 review).
            Allocate(ExpectedTicks());
            var source = log.Events.GetSource();
            var canceled = false;
            var rundownProvider = ClrRundownTraceEventParser.ProviderGuid;
            source.AllEvents += e =>
            {
                if (e.ProviderGuid == rundownProvider)
                {
                    _rundownEvents++;
                }

                if ((++_events & 0xFFF) == 0 && cancellationToken.IsCancellationRequested)
                {
                    canceled = true;
                    source.StopProcessing();
                }
            };
            source.Clr.GCAllocationTick += data =>
            {
                if (!canceled)
                {
                    Add(data);
                }
            };
            source.Process();
            if (canceled)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (_count != _times.Length)
            {
                Allocate(_count);
            }

            if (!_ordered)
            {
                SortByTime();
            }

            var info = new AllocationTraceInfo(
                _processId,
                _processName,
                log.SessionStartTime.ToUniversalTime(),
                log.SessionDuration.TotalMilliseconds,
                log.EventsLost,
                _withoutStack,
                _unresolvedSamples,
                _rundownEvents > 0,
                issues,
                firstIssues);
            return new AllocationTrace(_frames, _types, [.. _stackFrames], [.. _stackParents], _times, _stacks, _sampleTypes, _bytes, _kinds, info);
        }

        private int ExpectedTicks()
        {
            long ticks = 0;
            foreach (var counts in log.Stats)
            {
                if (counts.ProviderGuid == ClrTraceEventParser.ProviderGuid && (int)counts.EventID == AllocationTickEventId)
                {
                    ticks += counts.Count;
                }
            }

            return (int)Math.Min(ticks, Array.MaxLength);
        }

        private void Allocate(int length)
        {
            Array.Resize(ref _times, length);
            Array.Resize(ref _stacks, length);
            Array.Resize(ref _sampleTypes, length);
            Array.Resize(ref _bytes, length);
            Array.Resize(ref _kinds, length);
        }

        private void Add(GCAllocationTickTraceData data)
        {
            if (_count == _times.Length)
            {
                // More ticks than the counts said: grow by half (rare).
                Allocate(Math.Max(1024, _count + (_count / 2)));
            }

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

            var (stack, unresolved) = Leaf(data.CallStackIndex());
            if (unresolved)
            {
                _unresolvedSamples++;
            }

            var time = data.TimeStampRelativeMSec;
            if (_count > 0 && time < _times[_count - 1])
            {
                _ordered = false;
            }

            _times[_count] = time;
            _stacks[_count] = stack;
            _sampleTypes[_count] = type;
            _bytes[_count] = data.AllocationAmount64;
            _kinds[_count] = data.AllocationKind switch
            {
                GCAllocationKind.Large => AllocationKind.Large,
                (GCAllocationKind)2 => AllocationKind.Pinned,
                _ => AllocationKind.Small,
            };
            _count++;
        }

        // EventPipe buffers are per thread: a sample can arrive after a later one. The queries binary-search the times.
        private void SortByTime()
        {
            var order = new int[_count];
            for (var i = 0; i < order.Length; i++)
            {
                order[i] = i;
            }

            var keys = (double[])_times.Clone();
            Array.Sort(keys, order);
            _times = keys;
            _stacks = Permute(_stacks, order);
            _sampleTypes = Permute(_sampleTypes, order);
            _bytes = Permute(_bytes, order);
            _kinds = Permute(_kinds, order);
        }

        private static T[] Permute<T>(T[] values, int[] order)
        {
            var sorted = new T[values.Length];
            for (var i = 0; i < order.Length; i++)
            {
                sorted[i] = values[order[i]];
            }

            return sorted;
        }

        /// <summary>The innermost node of a TraceLog stack, adding its frames to the tree the first time; whether it has an unresolved frame.</summary>
        private (int Node, bool Unresolved) Leaf(CallStackIndex index)
        {
            if (index == CallStackIndex.Invalid)
            {
                // Kept in the totals under a frame of its own, so every view adds up to the same bytes.
                _withoutStack++;
                return (Node(-1, FrameId(AllocationTrace.NoStackFrame)), false);
            }

            if (_leaves.TryGetValue(index, out var known))
            {
                return known;
            }

            // Innermost first; every frame kept, named or not (merging unnamed frames would hide different methods).
            _scratch.Clear();
            var unresolved = false;
            for (var at = index; at != CallStackIndex.Invalid; at = log.CallStacks.Caller(at))
            {
                var (frame, named) = Frame(log.CallStacks.CodeAddressIndex(at));
                unresolved |= !named;
                _scratch.Add(frame);
            }

            var node = -1;
            for (var i = _scratch.Count - 1; i >= 0; i--)
            {
                node = Node(node, _scratch[i]);
            }

            _leaves.Add(index, (node, unresolved));
            return (node, unresolved);
        }

        private int Node(int parent, int frame)
        {
            if (!_nodes.TryGetValue((parent, frame), out var node))
            {
                node = _stackFrames.Count;
                _stackFrames.Add(frame);
                _stackParents.Add(parent);
                _nodes.Add((parent, frame), node);
            }

            return node;
        }

        private (int Frame, bool Named) Frame(CodeAddressIndex address)
        {
            var method = log.CodeAddresses.MethodIndex(address);
            var name = method == MethodIndex.Invalid ? null : log.CodeAddresses.Methods.FullMethodName(method);
            return string.IsNullOrEmpty(name)
                ? (FrameId(AllocationTrace.UnresolvedFrame(log.CodeAddresses.ModuleFile(address)?.Name)), false)
                : (FrameId(name), true);
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
