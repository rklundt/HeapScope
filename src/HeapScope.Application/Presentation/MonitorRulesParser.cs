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
using HeapScope.Application.Monitoring;
using HeapScope.Application.Snapshots;

namespace HeapScope.Application.Presentation;

/// <summary>
/// Reads monitor rules for the app's form and the CLI's options alike: an interval in the front end's grammar, a size
/// limit above zero, a growth limit as a size or a percent above zero, and a snapshot limit of 1 or more. Sizes and
/// percents read as <see cref="GrowthThreshold"/> does, whatever the grammar.
/// </summary>
public static class MonitorRulesParser
{
    public static MonitorRulesParse Parse(MonitorRulesInput input, MonitorRuleGrammar grammar)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(grammar);
        var mode = input.Rearm switch { "once" => RearmMode.Once, "step" => RearmMode.Step, _ => RearmMode.Below };
        var rules = new List<SnapshotRule>();
        string? everyError = null, aboveError = null, growthError = null, maxError = null;
        if (input.Every is { } every)
        {
            if (grammar.Interval(every) is { } interval)
            {
                rules.Add(new EveryRule(interval));
            }
            else
            {
                everyError = grammar.EveryRefused(every);
            }
        }

        if (input.Above is { } above)
        {
            if (GrowthThreshold.TryParse(above, out var size) && size.Bytes is { } bytes and not 0)
            {
                rules.Add(new AboveRule(bytes, mode));
            }
            else
            {
                aboveError = grammar.AboveRefused(above);
            }
        }

        if (input.Growth is { } growth)
        {
            if (GrowthThreshold.TryParse(growth, out var limit) && limit.Bytes is not 0 && limit.Percent is not 0)
            {
                rules.Add(new GrowthRule(limit.Bytes, limit.Percent, mode));
            }
            else
            {
                growthError = grammar.GrowthRefused(growth);
            }
        }

        int? max = null;
        if (input.Max is { } maxText)
        {
            if (int.TryParse(maxText, NumberStyles.Integer, grammar.Provider, out var count) && count >= 1)
            {
                max = count;
            }
            else
            {
                maxError = grammar.MaxRefused;
            }
        }

        var failed = everyError is not null || aboveError is not null || growthError is not null || maxError is not null;
        return new MonitorRulesParse(failed ? null : new MonitorRules(rules, max), everyError, aboveError, growthError, maxError);
    }
}
