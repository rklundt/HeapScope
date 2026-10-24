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

/// <summary>Counter events become one sample per interval (sprint 1.7).</summary>
public sealed class CounterSampleBuilderTests
{
    private static readonly DateTimeOffset s_start = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_counter_seen_again_completes_the_previous_interval_with_units_in_bytes()
    {
        var builder = new CounterSampleBuilder();

        builder.Add("gc-heap-size", 12.5, s_start).ShouldBeNull();
        builder.Add("gen-2-size", 4_000_000, s_start).ShouldBeNull();
        builder.Add("loh-size", 900_000, s_start).ShouldBeNull();
        builder.Add("gen-0-gc-count", 3, s_start).ShouldBeNull();
        builder.Add("alloc-rate", 65_536, s_start).ShouldBeNull();
        builder.Add("time-in-gc", 4.5, s_start).ShouldBeNull();
        builder.Add("working-set", 100, s_start).ShouldBeNull();
        var sample = builder.Add("gc-heap-size", 13, s_start.AddSeconds(1)).ShouldNotBeNull();

        sample.ShouldBe(new CounterSample(s_start, 12_500_000, 0, 0, 4_000_000, 900_000, 0, 3, 0, 0, 65_536, 4.5, 100_000_000), "the runtime's megabytes are decimal");
        builder.Flush().ShouldNotBeNull().At.ShouldBe(s_start.AddSeconds(1), "the next interval started with that value");
        builder.Flush().ShouldBeNull();
    }

    [Fact]
    public void Gauges_report_their_mean_and_rates_their_increment()
    {
        CounterMonitor.TryRead(new Dictionary<string, object> { ["Name"] = "gc-heap-size", ["Mean"] = 42.0 }, out var gauge, out var gaugeValue).ShouldBeTrue();
        CounterMonitor.TryRead(new Dictionary<string, object> { ["Name"] = "alloc-rate", ["Increment"] = 1024.0 }, out var rate, out var rateValue).ShouldBeTrue();
        CounterMonitor.TryRead(new Dictionary<string, object> { ["Name"] = "odd" }, out _, out _).ShouldBeFalse();
        CounterMonitor.TryRead(new Dictionary<string, object> { ["Mean"] = 1.0 }, out _, out _).ShouldBeFalse();

        (gauge, gaugeValue, rate, rateValue).ShouldBe(("gc-heap-size", 42.0, "alloc-rate", 1024.0));
    }
}
