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

using HeapScope.Worker.Rpc;

namespace HeapScope.Worker.Tests;

public class ThrottledProgressTests
{
    [Fact]
    public void Reports_inside_the_interval_are_suppressed_and_flush_sends_the_latest()
    {
        var sent = new List<string>();
        var throttled = new ThrottledProgress<string>(new ListProgress(sent), TimeSpan.FromHours(1));

        throttled.Report("first");
        throttled.Report("second");
        throttled.Report("third");
        throttled.Flush();
        throttled.Flush();

        sent.ShouldBe(["first", "third"]);
    }

    [Fact]
    public void Null_inner_progress_is_ignored()
    {
        var throttled = new ThrottledProgress<string>(null);

        Should.NotThrow(() =>
        {
            throttled.Report("x");
            throttled.Flush();
        });
    }

    private sealed class ListProgress(List<string> sent) : IProgress<string>
    {
        public void Report(string value) => sent.Add(value);
    }
}
