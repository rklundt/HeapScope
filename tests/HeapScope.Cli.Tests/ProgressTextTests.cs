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
/// Sprint 3.8 close: a worker call's progress can arrive after its result; once the command stops the reporter, a late
/// report must not land inside the output printed next (it broke `summary --format json` with stderr merged).
/// </summary>
public sealed class ProgressTextTests
{
    [Fact]
    public void A_report_after_stop_is_dropped()
    {
        using var error = new StringWriter();
        var progress = new CliCommands.StderrProgress(error);

        progress.Report(new JobProgress("Summing retained sizes by group", 1, 2));
        progress.Stop();
        progress.Report(new JobProgress("Opening the incoming references index", 0, null));

        error.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).ShouldBe(["Summing retained sizes by group  50.0%"]);
    }
}
