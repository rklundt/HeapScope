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

using System.Globalization;
using Fixtures.Leaks;

// Usage: LeakyConsole [--leak all|none|Name1,Name2] [--count N] [--payload BYTES] [--publishers N]
//                     [--semantics N] [--runtime-semantics N] [--late-garbage N] [--inspection] [--waste]
//                     [--parent-pid PID]
// Leaks the requested items, prints "READY pid=<pid>" and waits until killed (or until the parent exits), so a test
// can capture a dump. Late garbage is created after the last full collection, so it is present but unreachable.
var leakNames = Arg("--leak") ?? "all";
var count = IntArg("--count", 1000);
var payload = IntArg("--payload", 64);
var semantics = IntArg("--semantics", 0);
var runtimeSemantics = IntArg("--runtime-semantics", 0);
var lateGarbage = IntArg("--late-garbage", 0);
ParentWatch.ExitWhenParentExits(Arg("--parent-pid") is { } parent ? int.Parse(parent, CultureInfo.InvariantCulture) : null);
if (Arg("--publishers") is { } publishers)
{
    EventHandlerLeak.PublisherCount = int.Parse(publishers, CultureInfo.InvariantCulture);
}

foreach (var leak in LeakCatalog.Resolve(leakNames))
{
    leak.Grow(count, payload);
}

if (semantics > 0)
{
    SemanticFixtures.Create(semantics);
}

if (runtimeSemantics > 0)
{
    RuntimeFixtures.Create(runtimeSemantics);
}

if (args.Contains("--inspection"))
{
    InspectionFixtures.Create();
}

if (args.Contains("--waste"))
{
    WasteFixtures.Create();
}

GC.Collect();
GC.WaitForPendingFinalizers();
GC.Collect();

// After the last wait for pending finalizers (it blocks the finalizer thread for good) and before the late garbage
// (its collection would take that too).
if (args.Contains("--waste"))
{
    WasteFixtures.CreateFinalizerBacklog();
}

if (lateGarbage > 0)
{
    LateGarbageFixtures.Create(lateGarbage);
}

Console.WriteLine($"READY pid={Environment.ProcessId}");
await Task.Delay(Timeout.Infinite).ConfigureAwait(false);

string? Arg(string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

int IntArg(string name, int fallback) =>
    Arg(name) is { } value ? int.Parse(value, CultureInfo.InvariantCulture) : fallback;
