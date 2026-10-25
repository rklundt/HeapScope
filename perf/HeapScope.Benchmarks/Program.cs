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

using HeapScope.Benchmarks;

// Benchmarks are added by the sprints that own the measured code (DEVELOPER.md section 6).
var benchmarks = new Dictionary<string, Func<BenchmarkArgs, int>>(StringComparer.OrdinalIgnoreCase)
{
    ["graph-build"] = GraphBuildBenchmark.Run,
    ["dump-stats"] = DumpStatsBenchmark.Run,
    ["stack-roots"] = StackRootsBenchmark.Run,
    ["index-cache"] = IndexCacheBenchmark.Run,
    ["load-dump"] = LoadDumpBenchmark.Run,
    ["trace-report"] = TraceReport.Run,
    ["phase2-indices"] = Phase2IndexBenchmark.Run,
    ["reverse-index"] = ReverseIndexBenchmark.Run,
    ["retention"] = RetentionBenchmark.Run,
};

if (args.Length == 0 || !benchmarks.TryGetValue(args[0], out var run))
{
    Console.WriteLine("Usage: HeapScope.Benchmarks <benchmark> [--name value ...]");
    Console.WriteLine("Benchmarks: " + string.Join(", ", benchmarks.Keys));
    return args.Length == 0 ? 0 : 2;
}

return run(new BenchmarkArgs(args[1..]));
