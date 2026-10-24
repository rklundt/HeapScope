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

using HeapScope.Application.Workers;

namespace HeapScope.Application.Tests;

/// <summary>The worker's GC heap count leaves cores for the UI (sprint 1.3).</summary>
public sealed class WorkerHostOptionsTests
{
    [Fact]
    public void The_worker_gets_half_the_logical_processors_as_gc_heaps_unless_configured()
    {
        new WorkerHostOptions().ResolveGcHeapCount().ShouldBe(Math.Max(1, Environment.ProcessorCount / 2));
        new WorkerHostOptions { GcHeapCount = 3 }.ResolveGcHeapCount().ShouldBe(3);
        new WorkerHostOptions { GcHeapCount = 0 }.ResolveGcHeapCount().ShouldBe(1);
    }
}
