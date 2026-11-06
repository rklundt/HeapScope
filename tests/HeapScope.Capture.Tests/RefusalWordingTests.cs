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

using HeapScope.Capture;

namespace HeapScope.Capture.Tests;

/// <summary>A refusal says what the user was doing: the Live page's "cannot dump it" read as the wrong thing (3.10, the owner's own test).</summary>
public sealed class RefusalWordingTests
{
    [Fact]
    public void Monitoring_a_process_is_refused_in_words_about_monitoring()
    {
        var refusal = CounterMonitor.Translate(Environment.ProcessId, new UnauthorizedAccessException()).ShouldBeOfType<ElevationRequiredException>();

        refusal.Message.ShouldContain("cannot monitor it");
        refusal.Message.ShouldEndWith("and start again.");
        refusal.Message.ShouldNotContain("dump");
    }

    [Fact]
    public void A_trace_is_refused_in_words_about_a_trace()
    {
        var refusal = TraceRecorder.Translate(Environment.ProcessId, new UnauthorizedAccessException()).ShouldBeOfType<ElevationRequiredException>();

        refusal.Message.ShouldContain("cannot record its allocations");
        refusal.Message.ShouldNotContain("dump");
    }

    [Fact]
    public void A_dump_is_still_refused_in_words_about_a_dump()
    {
        ElevationRequiredException.For(1, "app").Message.ShouldContain("cannot dump it");
    }
}
