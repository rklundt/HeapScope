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

namespace HeapScope.Application.Monitoring;

/// <summary>What a size rule does after it fired (sprint 1.7).</summary>
public enum RearmMode
{
    /// <summary>Never fires again.</summary>
    Once = 0,

    /// <summary>Fires again after the heap falls back under the threshold.</summary>
    Below = 1,

    /// <summary>The threshold rises by its own amount: one snapshot for every further step.</summary>
    Step = 2,
}

/// <summary>A rule that takes a snapshot while a process is monitored (sprint 1.7, dotMemory's "get snapshot on condition").</summary>
public abstract record SnapshotRule
{
    /// <summary>Names the rule in snapshot labels and the timeline ("above 500 MB").</summary>
    public abstract string Label { get; }

    protected static string Megabytes(long bytes) => string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024.0):0.#} MB");
}

/// <summary>A snapshot every <see cref="Interval"/>, counted from the first sample.</summary>
public sealed record EveryRule(TimeSpan Interval) : SnapshotRule
{
    public override string Label => Interval.TotalMinutes >= 1 && Interval.TotalMinutes == Math.Floor(Interval.TotalMinutes)
        ? string.Create(CultureInfo.InvariantCulture, $"every {Interval.TotalMinutes:0} min")
        : string.Create(CultureInfo.InvariantCulture, $"every {Interval.TotalSeconds:0} s");
}

/// <summary>A snapshot when the heap after a GC exceeds <see cref="Bytes"/>.</summary>
public sealed record AboveRule(long Bytes, RearmMode Rearm = RearmMode.Below) : SnapshotRule
{
    /// <summary>A zero threshold would capture back to back (review of 1.7).</summary>
    public long Bytes { get; } = Bytes > 0 ? Bytes : throw new ArgumentOutOfRangeException(nameof(Bytes), "The size must be more than zero.");

    public override string Label => "above " + Megabytes(Bytes);
}

/// <summary>A snapshot when the heap after a GC grew by <see cref="Bytes"/> or by <see cref="Percent"/> over the baseline.</summary>
public sealed record GrowthRule(long? Bytes = null, double? Percent = null, RearmMode Rearm = RearmMode.Below) : SnapshotRule
{
    /// <summary>A zero growth would capture back to back (review of 1.7).</summary>
    public long? Bytes { get; } = Bytes is null or > 0 ? Bytes : throw new ArgumentOutOfRangeException(nameof(Bytes), "The growth must be more than zero.");

    public double? Percent { get; } = Percent is null or > 0 ? Percent : throw new ArgumentOutOfRangeException(nameof(Percent), "The growth must be more than zero.");

    public override string Label => Percent is { } percent
        ? string.Create(CultureInfo.InvariantCulture, $"{percent:0.#}% over baseline")
        : Megabytes(Bytes ?? 0) + " over baseline";
}

/// <summary>The rules of one monitoring run and the most snapshots it may take (null: no limit).</summary>
public sealed record MonitorRules(IReadOnlyList<SnapshotRule> Rules, int? MaxSnapshots = null)
{
    public static MonitorRules None { get; } = new([]);
}

/// <summary>
/// Decides, sample by sample, whether a rule takes a snapshot (sprint 1.7, decisions 2 and 3). Times are the samples'
/// own, so the engine needs no clock. While a capture runs, rules wait and keep their state; one rule fires per sample,
/// the first in order. The rule metric is the heap after the last GC (<see cref="HeapSample.HeapAfterGcBytes"/>): the
/// heap now rises and falls with garbage between collections, which would fire and re-arm rules on garbage (review of
/// 1.7); before the first GC it is the heap now (<see cref="HeapSample.RuleBytes"/>). A rule re-arms below 90 percent of
/// its threshold. The baseline is the first sample's metric.
/// </summary>
public sealed class SnapshotRuleEngine
{
    /// <summary>Samples averaged for the GC pressure badge.</summary>
    public const int PressureWindow = 10;

    /// <summary>Average time in GC, in percent, that counts as high GC pressure.</summary>
    public const double PressurePercent = 10;

    /// <summary>A size rule re-arms once the metric is under this share of its threshold.</summary>
    public const double RearmShare = 0.9;

    private readonly MonitorRules _rules;
    private readonly RuleState[] _states;
    private readonly Queue<double> _timeInGc = new();
    private DateTimeOffset? _start;

    public SnapshotRuleEngine(MonitorRules rules)
    {
        _rules = rules ?? throw new ArgumentNullException(nameof(rules));
        _states = [.. rules.Rules.Select(_ => new RuleState())];
    }

    /// <summary>The first after-GC heap size seen, the base of growth rules.</summary>
    public long? Baseline { get; private set; }

    /// <summary>Snapshots taken so far (counted by <see cref="Taken"/>).</summary>
    public int Count { get; private set; }

    /// <summary>
    /// Why the last rule fired: the metric and its source, the threshold actually used (after steps), the baseline and
    /// the rearm mode (sprint 2.3.7 review M4). "Above 500 MB" can fire at 1.5 GB after two steps, and the log says so.
    /// </summary>
    public RuleDecision? LastDecision { get; private set; }

    /// <summary>Whether the run stopped taking snapshots because it reached its maximum.</summary>
    public bool AtMaximum => _rules.MaxSnapshots is { } max && Count >= max;

    /// <summary>
    /// Whether time in GC averaged <see cref="PressurePercent"/> or more over the last intervals that had a GC (the
    /// counter is the last GC's share and stays put between collections, so idle intervals must not count).
    /// </summary>
    public bool HighGcPressure => _timeInGc.Count > 0 && _timeInGc.Average() >= PressurePercent;

    /// <summary>
    /// The rule that was due at the last <see cref="Evaluate"/> but did not fire because a capture of the process was
    /// running (sprint 3.4 review H5: logged, and it fires at the next sample after the capture ends).
    /// </summary>
    public SnapshotRule? Deferred { get; private set; }

    /// <summary>The metric the last <see cref="Evaluate"/> compared (for the log of a deferred rule).</summary>
    public long LastMetric { get; private set; }

    /// <summary>
    /// Undoes a rule's firing whose capture was refused because another capture of the process started first (sprint 3.4
    /// review H5): a once rule is not used up, a timed rule is due again at the next sample.
    /// </summary>
    public void Missed(SnapshotRule rule)
    {
        var i = Array.FindIndex(_rules.Rules.ToArray(), r => ReferenceEquals(r, rule));
        if (i < 0)
        {
            return;
        }

        var state = _states[i];
        switch (rule)
        {
            case EveryRule:
                state.NextDue = DateTimeOffset.MinValue;
                break;
            case AboveRule or GrowthRule:
                switch (RearmOf(rule))
                {
                    case RearmMode.Once:
                    case RearmMode.Below:
                        state.Armed = true;
                        state.Retired = false;
                        break;
                    case RearmMode.Step when state.Steps > 0:
                        state.Steps--;
                        break;
                }

                break;
        }
    }

    /// <summary>The rule that fires for <paramref name="sample"/>, or null. Call <see cref="Taken"/> once its snapshot is written.</summary>
    public SnapshotRule? Evaluate(HeapSample sample, bool captureRunning)
    {
        ArgumentNullException.ThrowIfNull(sample);
        _start ??= sample.At;
        var metric = sample.RuleBytes;
        LastMetric = metric;
        Baseline ??= metric;

        if (sample.HadGc)
        {
            _timeInGc.Enqueue(sample.TimeInGcPercent);
            if (_timeInGc.Count > PressureWindow)
            {
                _timeInGc.Dequeue();
            }
        }

        // Re-arm size rules whose heap fell back under their threshold, even while a capture runs.
        for (var i = 0; i < _states.Length; i++)
        {
            var state = _states[i];
            if (!state.Armed && !state.Retired && Threshold(_rules.Rules[i], state) is { } threshold && metric < threshold * RearmShare)
            {
                state.Armed = true;
            }
        }

        Deferred = null;
        if (_rules.MaxSnapshots is { } max && Count >= max)
        {
            return null;
        }

        if (captureRunning)
        {
            Deferred = Due(sample, metric);
            return null;
        }

        for (var i = 0; i < _states.Length; i++)
        {
            var rule = _rules.Rules[i];
            var state = _states[i];
            switch (rule)
            {
                case EveryRule every:
                    state.NextDue ??= _start.Value + every.Interval;
                    if (sample.At >= state.NextDue)
                    {
                        LastDecision = new RuleDecision(rule, metric, sample.HeapAfterGcBytes > 0, null, Baseline, 0, null, state.NextDue);

                        // The next one is due an interval after this one, not after a missed slot.
                        state.NextDue = sample.At + every.Interval;
                        return rule;
                    }

                    break;
                case AboveRule or GrowthRule when state.Armed && Threshold(rule, state) is { } threshold && metric >= threshold:
                    LastDecision = new RuleDecision(rule, metric, sample.HeapAfterGcBytes > 0, threshold, Baseline, state.Steps, RearmOf(rule), null);
                    Fire(rule, state);
                    return rule;
            }
        }

        return null;
    }

    /// <summary>Records that a rule's snapshot was written (the maximum count includes it).</summary>
    public void Taken() => Count++;

    /// <summary>The rule that would fire for the sample, changing nothing (a capture is running).</summary>
    private SnapshotRule? Due(HeapSample sample, long metric)
    {
        for (var i = 0; i < _states.Length; i++)
        {
            var rule = _rules.Rules[i];
            var state = _states[i];
            var due = rule switch
            {
                EveryRule every => sample.At >= (state.NextDue ?? (_start!.Value + every.Interval)),
                AboveRule or GrowthRule => state.Armed && Threshold(rule, state) is { } threshold && metric >= threshold,
                _ => false,
            };
            if (due)
            {
                return rule;
            }
        }

        return null;
    }

    private void Fire(SnapshotRule rule, RuleState state)
    {
        switch (RearmOf(rule))
        {
            case RearmMode.Once:
                state.Armed = false;
                state.Retired = true;
                break;
            case RearmMode.Below:
                state.Armed = false;
                break;
            case RearmMode.Step:
                state.Steps++;
                break;
        }
    }

    /// <summary>The GC heap size a size rule fires at now (after its steps); null for other rules or before a baseline.</summary>
    private long? Threshold(SnapshotRule rule, RuleState state) => rule switch
    {
        AboveRule above => above.Bytes * (state.Steps + 1),
        GrowthRule growth when Baseline is { } baseline => baseline + (Delta(growth, baseline) * (state.Steps + 1)),
        _ => null,
    };

    private static long Delta(GrowthRule growth, long baseline) =>
        growth.Percent is { } percent ? (long)(baseline * percent / 100) : growth.Bytes ?? 0;

    private static RearmMode RearmOf(SnapshotRule rule) => rule switch
    {
        AboveRule above => above.Rearm,
        GrowthRule growth => growth.Rearm,
        _ => RearmMode.Below,
    };

    private sealed class RuleState
    {
        public bool Armed { get; set; } = true;

        public bool Retired { get; set; }

        public int Steps { get; set; }

        public DateTimeOffset? NextDue { get; set; }
    }
}

/// <summary>
/// Why a rule fired (sprint 2.3.7 review M4): the rule, the metric and whether it was the heap after the last GC or the
/// heap now (no GC yet), the threshold it fired at after <paramref name="Step"/> steps, the growth baseline, the rearm
/// mode, and for a timed rule when it was due.
/// </summary>
public sealed record RuleDecision(SnapshotRule Rule, long Metric, bool MetricAfterGc, long? Threshold, long? Baseline, int Step, RearmMode? Rearm, DateTimeOffset? Due)
{
    public override string ToString()
    {
        static string Mb(long bytes) => string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024.0):0.#} MB");
        var source = MetricAfterGc ? "heap after the last GC" : "heap now, no GC yet";
        var baseline = Baseline is { } b ? Mb(b) : "none";
        return Threshold is { } threshold
            ? string.Create(CultureInfo.InvariantCulture, $"{Rule.Label}: metric {Mb(Metric)} ({source}) reached threshold {Mb(threshold)} at step {Step}; baseline {baseline}; rearm {Rearm}")
            : string.Create(CultureInfo.InvariantCulture, $"{Rule.Label}: due at {Due:O}; metric {Mb(Metric)} ({source}); baseline {baseline}");
    }
}
