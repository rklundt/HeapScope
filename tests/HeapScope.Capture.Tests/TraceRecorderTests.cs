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

using System.Diagnostics.Tracing;
using HeapScope.Capture;

namespace HeapScope.Capture.Tests;

/// <summary>What each trace profile asks the runtime for.</summary>
public sealed class TraceRecorderTests
{
    [Fact]
    public void The_allocations_profile_asks_for_verbose_gc_events_and_the_events_that_name_methods()
    {
        var provider = TraceRecorder.Providers(TraceProfile.Allocations).ShouldHaveSingleItem();

        provider.Name.ShouldBe("Microsoft-Windows-DotNETRuntime");
        provider.EventLevel.ShouldBe(EventLevel.Verbose, "allocation ticks are verbose events");
        (provider.Keywords & 0x1).ShouldBe(0x1, "GC");
        (provider.Keywords & 0x8).ShouldBe(0x8, "loader: module names");
        (provider.Keywords & 0x10).ShouldBe(0x10, "JIT: names of methods compiled while recording");
    }

    [Fact]
    public void The_cpu_profile_samples_stacks()
    {
        TraceRecorder.Providers(TraceProfile.Cpu).ShouldContain(p => p.Name == "Microsoft-DotNETCore-SampleProfiler");
    }
}
