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

using HeapScope.Application.Monitoring;
using HeapScope.Capture;

namespace HeapScope.Application.Tests;

/// <summary>Snapshot rules over sample sequences (sprint 1.7): firing, re-arming, steps, timing, limits, GC pressure.</summary>
public sealed class SnapshotRuleEngineTests
{
    private const long Mb = 1024 * 1024;
    private static readonly DateTimeOffset s_start = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_size_rule_fires_once_when_crossed_and_rearms_below_its_threshold()
    {
        var engine = new SnapshotRuleEngine(new MonitorRules([new AboveRule(500 * Mb)]));

        var fired = Run(engine, 100, 400, 520, 600, 700, 440, 510);

        fired.ShouldBe([null, null, "above 500 MB", null, null, null, "above 500 MB"]);
    }

    [Fact]
    public void A_rule_rearms_only_under_90_percent_of_its_threshold()
    {
        var engine = new SnapshotRuleEngine(new MonitorRules([new AboveRule(500 * Mb)]));

        Run(engine, 520, 460, 520, 449, 520).ShouldBe(["above 500 MB", null, null, null, "above 500 MB"]);
    }

    [Fact]
    public void Garbage_allocated_since_the_last_gc_never_fires_a_size_rule()
    {
        var engine = new SnapshotRuleEngine(new MonitorRules([new AboveRule(500 * Mb)]));

        // The heap now is 900 MB, but only 300 MB survived the last GC.
        engine.Evaluate(Sample(300) with { GcHeapBytes = 900 * Mb }, captureRunning: false).ShouldBeNull();
        engine.Evaluate(Sample(520) with { GcHeapBytes = 900 * Mb }, captureRunning: false).ShouldNotBeNull();
    }

    [Fact]
    public void Before_any_gc_the_metric_is_the_heap_now_and_pressure_counts_only_intervals_with_a_gc()
    {
        var engine = new SnapshotRuleEngine(new MonitorRules([new GrowthRule(Bytes: 100 * Mb)]));

        // An idle process: no GC yet, so every generation size is still zero; the heap now is 2 MB.
        engine.Evaluate(new HeapSample(DateTimeOffset.UnixEpoch, 2 * Mb, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0), false).ShouldBeNull();
        engine.Baseline.ShouldBe(2 * Mb);
        engine.Evaluate(Sample(150), false).ShouldNotBeNull("150 MB survived the first GC: 148 MB over the 2 MB baseline");

        var idle = new SnapshotRuleEngine(MonitorRules.None);
        idle.Evaluate(Sample(100) with { TimeInGcPercent = 40 }, false);
        for (var i = 0; i < 10; i++)
        {
            idle.Evaluate(Sample(100) with { TimeInGcPercent = 40, Gen0Collections = 0 }, false);
        }

        idle.HighGcPressure.ShouldBeTrue("one costly GC");
        for (var i = 0; i < 3; i++)
        {
            idle.Evaluate(Sample(100) with { TimeInGcPercent = 1 }, false);
        }

        idle.HighGcPressure.ShouldBeTrue("(40 + 1 + 1 + 1) / 4 is still over 10: idle intervals did not dilute or repeat it");
    }

    [Fact]
    public void Once_never_fires_again_and_step_fires_for_each_further_step()
    {
        var once = new SnapshotRuleEngine(new MonitorRules([new AboveRule(500 * Mb, RearmMode.Once)]));
        var step = new SnapshotRuleEngine(new MonitorRules([new AboveRule(500 * Mb, RearmMode.Step)]));

        Run(once, 520, 400, 600).Count(f => f is not null).ShouldBe(1);
        Run(step, 520, 700, 990, 1010, 1600).ShouldBe(["above 500 MB", null, null, "above 500 MB", "above 500 MB"]);
    }

    [Fact]
    public void Growth_counts_from_the_first_sample_in_megabytes_or_percent()
    {
        var bytes = new SnapshotRuleEngine(new MonitorRules([new GrowthRule(Bytes: 100 * Mb)]));
        var percent = new SnapshotRuleEngine(new MonitorRules([new GrowthRule(Percent: 50)]));

        Run(bytes, 200, 290, 300).ShouldBe([null, null, "100 MB over baseline"]);
        bytes.Baseline.ShouldBe(200 * Mb);
        Run(percent, 200, 299, 300).ShouldBe([null, null, "50% over baseline"]);
    }

    [Fact]
    public void Every_rule_fires_an_interval_after_the_first_sample_then_every_interval()
    {
        var engine = new SnapshotRuleEngine(new MonitorRules([new EveryRule(TimeSpan.FromMinutes(5))]));
        var fired = new List<string?>();
        foreach (var minutes in new[] { 0, 4, 5, 6, 9, 10, 17 })
        {
            fired.Add(engine.Evaluate(Sample(100, s_start.AddMinutes(minutes)), captureRunning: false)?.Label);
        }

        fired.ShouldBe([null, null, "every 5 min", null, null, "every 5 min", "every 5 min"]);
    }

    [Fact]
    public void Rules_wait_while_a_capture_runs_and_stop_at_the_maximum()
    {
        var engine = new SnapshotRuleEngine(new MonitorRules([new AboveRule(500 * Mb, RearmMode.Step)], MaxSnapshots: 2));

        engine.Evaluate(Sample(600), captureRunning: true).ShouldBeNull("the target is paused while a capture is written");
        engine.Evaluate(Sample(600), captureRunning: false).ShouldNotBeNull();
        engine.Taken();
        engine.Evaluate(Sample(1100), captureRunning: false).ShouldNotBeNull();
        engine.Taken();
        engine.Evaluate(Sample(1600), captureRunning: false).ShouldBeNull("two snapshots is the maximum");
        engine.Count.ShouldBe(2);
    }

    [Fact]
    public void Only_the_first_rule_that_triggers_fires_and_the_others_stay_armed()
    {
        var engine = new SnapshotRuleEngine(new MonitorRules([new AboveRule(500 * Mb), new GrowthRule(Bytes: 100 * Mb)]));

        Run(engine, 100, 600, 600).ShouldBe([null, "above 500 MB", "100 MB over baseline"]);
    }

    [Fact]
    public void High_gc_pressure_is_an_average_of_10_percent_over_the_last_10_samples()
    {
        var engine = new SnapshotRuleEngine(MonitorRules.None);
        for (var i = 0; i < 10; i++)
        {
            engine.Evaluate(Sample(100) with { TimeInGcPercent = 5 }, false);
        }

        engine.HighGcPressure.ShouldBeFalse();
        for (var i = 0; i < 5; i++)
        {
            engine.Evaluate(Sample(100) with { TimeInGcPercent = 16 }, false);
        }

        engine.HighGcPressure.ShouldBeTrue("(5 x 5 + 5 x 16) / 10 = 10.5");
    }

    [Theory]
    [InlineData(90, "every 90 s")]
    [InlineData(300, "every 5 min")]
    public void Labels_name_the_rule(int seconds, string label)
    {
        new EveryRule(TimeSpan.FromSeconds(seconds)).Label.ShouldBe(label);
    }

    private static List<string?> Run(SnapshotRuleEngine engine, params long[] heapMegabytes)
    {
        var fired = new List<string?>();
        for (var i = 0; i < heapMegabytes.Length; i++)
        {
            var rule = engine.Evaluate(Sample(heapMegabytes[i], s_start.AddSeconds(i)), captureRunning: false);
            if (rule is not null)
            {
                engine.Taken();
            }

            fired.Add(rule?.Label);
        }

        return fired;
    }

    /// <summary>A sample whose heap after the last GC is <paramref name="heapMegabytes"/> (all in gen2), after a GC.</summary>
    private static HeapSample Sample(long heapMegabytes, DateTimeOffset? at = null) =>
        new(at ?? s_start, heapMegabytes * Mb, 0, 0, heapMegabytes * Mb, 0, 0, 1, 0, 0, 0, 0, 0);
}
