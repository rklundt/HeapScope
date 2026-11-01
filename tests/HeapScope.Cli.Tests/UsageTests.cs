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

using HeapScope.Contracts;

namespace HeapScope.Cli.Tests;

/// <summary>
/// Usage errors exit 64 (EX_USAGE) before a host or a worker exists, with the error and the attribution notice; the
/// version prints the attribution too (ADDITIONAL-TERMS.md 1.4).
/// </summary>
public sealed class UsageTests
{
    public static TheoryData<string[], string?> UsageErrors => new()
    {
        { ["diff", "a.dmp", "b.dmp", "--sort", "size"], null },
        { ["diff", "a.dmp", "b.dmp", "--fail-on-growth", "lots"], "--fail-on-growth: 'lots' is not a size" },
        { ["diff", "a.dmp", "b.dmp", "--top", "0"], "--top must be 1 or more." },
        { ["diff", "a.dmp", "b.dmp", "--top", "3", "--all"], "Use either --top or --all, not both." },
        { ["diff", "a.dmp"], null },
        { ["set", "a.dmp", "--namespace", "A", "--assembly", "B"], null },
        { ["set", "a.dmp", "--unreachable", "--namespace", "A"], null },
        { ["monitor", "--pid", "1", "--every", "soon"], "--every: 'soon' is not an interval such as 90s, 5m or 1h." },
        { ["monitor", "--pid", "1", "--every", "0.5"], "--every: '0.5' is not an interval such as 90s, 5m or 1h." },
        { ["monitor", "--pid", "1", "--above", "50%"], "--above: '50%' is not a size above zero such as 500MB." },
        { ["monitor", "--pid", "1", "--above", "0MB"], "--above: '0MB' is not a size above zero such as 500MB." },
        { ["monitor", "--pid", "1", "--growth", "0%"], "--growth: '0%' is not a size above zero such as 500MB or a percent such as 50%." },
        { ["monitor", "--pid", "1", "--max", "0"], "--max must be 1 or more." },
        { ["--trust-dac", "abc", "worker", "ping"], "--trust-dac takes a SHA-256 of 64 hex digits; abc is not one." },
    };

    [Theory]
    [MemberData(nameof(UsageErrors))]
    public async Task A_usage_error_exits_64_without_a_host(string[] args, string? message)
    {
        var (code, output, error) = await InProcessCli.RunAsync(InProcessCli.NoHost, args);

        code.ShouldBe(64, output + error);
        if (message is not null)
        {
            (output + error).ShouldContain(message);
        }

        output.ShouldEndWith(ProductInfo.AttributionWithUrl + Environment.NewLine);
    }

    [Fact]
    public async Task Version_prints_the_product_version_and_the_attribution()
    {
        var (code, output, _) = await InProcessCli.RunAsync(CliHost.Build, "--version");

        code.ShouldBe(0);
        output.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).ShouldBe([ProductInfo.Version, ProductInfo.AttributionWithUrl]);
    }
}
