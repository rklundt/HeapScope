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
using HeapScope.Core.Synthetic;

namespace HeapScope.Benchmarks;

/// <summary>
/// Builds a synthetic random graph and reports build time, bytes per object and memory, against the budget in
/// ARCHITECTURE.md 6.2 and 10. Usage: graph-build [--objects 50000000] [--refs 3] [--seed 42] [--reverse 1] [--by-address 1].
/// </summary>
internal static class GraphBuildBenchmark
{
    private const double BudgetMinBytesPerObject = 30;
    private const double BudgetMaxBytesPerObject = 38;

    public static int Run(BenchmarkArgs args)
    {
        var objects = args.GetInt("objects", 50_000_000);
        var refs = args.GetDouble("refs", 3);
        var seed = (ulong)args.GetInt("seed", 42);
        var reverse = args.GetInt("reverse", 0) != 0;
        var byAddress = args.GetInt("by-address", 0) != 0;

        Console.WriteLine($"graph-build: {objects:N0} objects, {refs.ToString(CultureInfo.InvariantCulture)} references per object, seed {seed}, {(reverse ? "unsorted input" : "address-ordered input")}, references by {(byAddress ? "address" : "index")}");
        var before = Measurement.ManagedHeapAfterFullGc();
        var stopwatch = Stopwatch.StartNew();
        using var graph = SyntheticGraphs.Random(new RandomGraphOptions(objects, refs, Seed: seed, AddInReverseOrder: reverse, ReferencesByAddress: byAddress));
        stopwatch.Stop();
        var after = Measurement.ManagedHeapAfterFullGc();

        var bytesPerObject = (double)graph.ColumnBytes / Math.Max(graph.ObjectCount, 1);
        var heapPerObject = (double)(after - before) / Math.Max(graph.ObjectCount, 1);
        Measurement.Line("Build time", $"{stopwatch.Elapsed.TotalSeconds:N1} s");
        Measurement.Line("Objects / references", $"{graph.ObjectCount:N0} / {graph.EdgeCount:N0}");
        Measurement.Line("Column bytes", $"{Measurement.Mb(graph.ColumnBytes)} ({bytesPerObject:N1} bytes per object)");
        Measurement.Line("Managed heap growth", $"{Measurement.Mb(after - before)} ({heapPerObject:N1} bytes per object)");
        Measurement.Line("Peak working set", Measurement.Mb(Measurement.PeakWorkingSet()));
        Measurement.Line("Machine", $"{Environment.ProcessorCount} logical cores, {Measurement.Mb((long)GC.GetGCMemoryInfo().TotalAvailableMemoryBytes)} available to the GC");

        var withinBudget = bytesPerObject is >= BudgetMinBytesPerObject and <= BudgetMaxBytesPerObject;
        Measurement.Line("Budget 30 to 38 bytes per object", withinBudget ? "met" : "MISSED");
        GC.KeepAlive(graph);
        return withinBudget ? 0 : 1;
    }
}
