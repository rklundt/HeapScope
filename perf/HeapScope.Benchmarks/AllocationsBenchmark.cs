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
using HeapScope.Analysis.Allocations;
using HeapScope.Core.Allocations;
using HeapScope.Loaders.Trace;

namespace HeapScope.Benchmarks;

/// <summary>
/// Reading an allocations trace and answering its queries (sprint 2.5): the read's time and peak private bytes against
/// the file's size (the worker reserves <c>TraceStore.ReadBytesPerFileByte</c> per byte of the file), what the samples
/// keep, and the whole-trace types, top stacks and both trees.
/// Usage: allocations --trace &lt;file.nettrace&gt;
/// </summary>
internal static class AllocationsBenchmark
{
    public static int Run(BenchmarkArgs args)
    {
        var path = args.Get("trace") ?? throw new ArgumentException("--trace <file.nettrace> is required.");
        var fileBytes = new FileInfo(path).Length;
        Console.WriteLine($"{path}: {Measurement.Mb(fileBytes)}");

        AllocationTrace trace;
        var stopwatch = Stopwatch.StartNew();
        using (var peak = new PrivateBytesSampler())
        {
            var before = peak.Peak;
            trace = AllocationTraceReader.Read(path, null, CancellationToken.None);
            stopwatch.Stop();
            Measurement.Line("Read", $"{stopwatch.Elapsed.TotalSeconds:N1} s, peak {Measurement.Mb(peak.Peak)} ({peak.Peak / (double)Math.Max(1, fileBytes):N1} x the file; {Measurement.Mb(before)} before)");
        }

        GC.Collect();
        Measurement.Line("Kept", $"{trace.SampleCount:N0} samples ({Measurement.Mb((long)trace.SampleCount * AllocationTrace.BytesPerSample)}), {trace.StackFrames.Length:N0} stack nodes, {trace.Frames.Count:N0} frames, {trace.Types.Count:N0} types; managed heap {Measurement.Mb(Measurement.ManagedHeapAfterFullGc())}");
        Time("Types", () => AllocationProfile.Types(trace, AllocationFilter.All).Length);
        Time("Top 20 stacks", () => AllocationProfile.TopStacks(trace, AllocationFilter.All, 20).Length);
        Time("Back traces (top level)", () => AllocationTreeRows.Build(trace, AllocationFilter.All, AllocationTreeKind.BackTraces, new HashSet<string>(), 100, CancellationToken.None).Rows.Count);
        Time("Call tree (top level)", () => AllocationTreeRows.Build(trace, AllocationFilter.All, AllocationTreeKind.CallTree, new HashSet<string>(), 100, CancellationToken.None).Rows.Count);
        return 0;
    }

    private static void Time(string label, Func<int> query)
    {
        var stopwatch = Stopwatch.StartNew();
        var rows = query();
        Measurement.Line(label, $"{stopwatch.Elapsed.TotalMilliseconds:N0} ms, {rows:N0} rows");
    }
}
