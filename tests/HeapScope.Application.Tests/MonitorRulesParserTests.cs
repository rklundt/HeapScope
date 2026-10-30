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
using HeapScope.Application.Presentation;

namespace HeapScope.Application.Tests;

/// <summary>
/// One monitor rules parser for the app's form and the CLI's options (sprint 3.8, P18): the CLI writes intervals with a
/// unit in the invariant culture, the app bare minutes in the current culture; each keeps its own messages.
/// </summary>
public sealed class MonitorRulesParserTests
{
    private static readonly MonitorRuleGrammar s_cli = MonitorRuleGrammar.Suffixed;
    private static readonly MonitorRuleGrammar s_app = MonitorRuleGrammar.Minutes(CultureInfo.InvariantCulture);

    [Theory]
    [InlineData("90s", 90)]
    [InlineData("5m", 300)]
    [InlineData("1h", 3600)]
    [InlineData(" 1.5M ", 90)]
    public void The_CLI_reads_intervals_with_a_unit(string text, int seconds)
    {
        var parse = MonitorRulesParser.Parse(new MonitorRulesInput(Every: text), s_cli);

        parse.Rules.ShouldNotBeNull().Rules.ShouldBe([new EveryRule(TimeSpan.FromSeconds(seconds))]);
        parse.FirstError.ShouldBeNull();
    }

    [Theory]
    [InlineData("0.5")]
    [InlineData("soon")]
    [InlineData("0.5s")]
    [InlineData("-5m")]
    public void The_CLI_refuses_an_interval_without_a_unit_or_under_a_second(string text)
    {
        var parse = MonitorRulesParser.Parse(new MonitorRulesInput(Every: text), s_cli);

        parse.Rules.ShouldBeNull();
        parse.EveryError.ShouldBe($"--every: '{text}' is not an interval such as 90s, 5m or 1h.");
    }

    [Fact]
    public void The_app_reads_bare_minutes()
    {
        MonitorRulesParser.Parse(new MonitorRulesInput(Every: "0.5"), s_app).Rules.ShouldNotBeNull().Rules.ShouldBe([new EveryRule(TimeSpan.FromSeconds(30))]);
        MonitorRulesParser.Parse(new MonitorRulesInput(Every: "5m"), s_app).EveryError.ShouldBe("Every: enter minutes, such as 5 or 0.5.");
    }

    [Fact]
    public void The_app_reads_minutes_in_the_current_culture()
    {
        var german = MonitorRuleGrammar.Minutes(CultureInfo.GetCultureInfo("de-DE"));

        MonitorRulesParser.Parse(new MonitorRulesInput(Every: "1,5"), german).Rules.ShouldNotBeNull().Rules.ShouldBe([new EveryRule(TimeSpan.FromSeconds(90))]);
    }

    [Fact]
    public void A_zero_size_is_refused_by_both_grammars()
    {
        MonitorRulesParser.Parse(new MonitorRulesInput(Above: "0MB"), s_cli).AboveError.ShouldBe("--above: '0MB' is not a size above zero such as 500MB.");
        MonitorRulesParser.Parse(new MonitorRulesInput(Above: "0MB"), s_app).AboveError.ShouldBe("Above: enter a size above zero, such as 500MB or 2GB.");
        MonitorRulesParser.Parse(new MonitorRulesInput(Growth: "0"), s_cli).GrowthError.ShouldBe("--growth: '0' is not a size above zero such as 500MB or a percent such as 50%.");
        MonitorRulesParser.Parse(new MonitorRulesInput(Growth: "0%"), s_app).GrowthError.ShouldBe("Growth: enter a size or a percent above zero, such as 100MB or 50%.");
    }

    [Fact]
    public void A_percent_is_refused_for_above_but_taken_for_growth()
    {
        MonitorRulesParser.Parse(new MonitorRulesInput(Above: "50%"), s_cli).AboveError.ShouldBe("--above: '50%' is not a size above zero such as 500MB.");
        MonitorRulesParser.Parse(new MonitorRulesInput(Above: "50%"), s_app).AboveError.ShouldBe("Above: enter a size above zero, such as 500MB or 2GB.");
        MonitorRulesParser.Parse(new MonitorRulesInput(Growth: "50%"), s_cli).Rules.ShouldNotBeNull().Rules.ShouldBe([new GrowthRule(null, 50)]);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("many")]
    public void A_snapshot_limit_below_1_is_refused(string text)
    {
        MonitorRulesParser.Parse(new MonitorRulesInput(Max: text), s_cli).MaxError.ShouldBe("--max must be 1 or more.");
        MonitorRulesParser.Parse(new MonitorRulesInput(Max: text), s_app).MaxError.ShouldBe("Max snapshots: enter a whole number of 1 or more.");
    }

    [Fact]
    public void Every_rule_parses_with_its_rearm_mode_and_limit()
    {
        var parse = MonitorRulesParser.Parse(new MonitorRulesInput("90s", "500MB", "100MB", "3", "step"), s_cli);

        parse.Rules.ShouldNotBeNull().MaxSnapshots.ShouldBe(3);
        parse.Rules.Rules.ShouldBe([new EveryRule(TimeSpan.FromSeconds(90)), new AboveRule(500L << 20, RearmMode.Step), new GrowthRule(100L << 20, null, RearmMode.Step)]);
        var none = MonitorRulesParser.Parse(new MonitorRulesInput(), s_cli).Rules.ShouldNotBeNull();
        (none.Rules.Count, none.MaxSnapshots).ShouldBe((0, (int?)null), "no field sets no rule");
    }

    [Fact]
    public void Each_refused_field_says_so_and_the_first_comes_in_the_form_order()
    {
        var parse = MonitorRulesParser.Parse(new MonitorRulesInput("soon", "50%", "much", "0"), s_app);

        parse.Rules.ShouldBeNull();
        new[] { parse.EveryError, parse.AboveError, parse.GrowthError, parse.MaxError }.ShouldAllBe(e => e != null);
        parse.FirstError.ShouldBe(parse.EveryError);
    }
}
