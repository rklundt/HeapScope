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

using Microsoft.Diagnostics.Runtime;

namespace HeapScope.Benchmarks;

/// <summary>
/// Diagnostic: prints every managed thread's frames and the stack roots ClrMD reports for them.
/// Usage: stack-roots --dump &lt;path&gt;.
/// </summary>
internal static class StackRootsBenchmark
{
    public static int Run(BenchmarkArgs args)
    {
        var path = args.Get("dump") ?? throw new ArgumentException("--dump <path> is required.");
        using var target = DataTarget.LoadDump(path);
        using var runtime = target.ClrVersions[0].CreateRuntime();
        foreach (var thread in runtime.Threads)
        {
            Console.WriteLine($"thread {thread.ManagedThreadId}");
            foreach (var root in thread.EnumerateStackRoots())
            {
                Console.WriteLine($"  root {root.Object.Type?.Name} interior={root.IsInterior} frame={root.StackFrame?.Method?.Name ?? root.StackFrame?.Kind.ToString()}");
            }
        }

        return 0;
    }
}
