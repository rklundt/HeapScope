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
using Microsoft.Extensions.Logging.Abstractions;

namespace HeapScope.Application.Tests;

/// <summary>A monitoring run's timeline CSV, written apart from the run (sprint 3.8, peer review P11).</summary>
public sealed class TimelineWriterTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("heapscope-timeline-").FullName;

    [Fact]
    public async Task Samples_events_and_captures_read_back_and_nothing_is_written_after_closing()
    {
        var at = new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);
        var timeline = TimelineWriter.Create(_folder, 42, at, NullLogger.Instance, Guid.NewGuid());
        timeline.WriteSample(new HeapSample(at, 1000, 100, 200, 300, 400, 0, 1, 2, 3, 5000, 1.5, 9000));
        timeline.WriteEvent(at.AddSeconds(1), MonitorRun.GcForcedEvent);
        timeline.WriteEvent(at.AddSeconds(2), "a \"quoted\" note");
        var path = timeline.Path.ShouldNotBeNull();
        await timeline.CloseAsync(timeline.Detach());
        timeline.WriteEvent(at.AddSeconds(3), "after closing");

        var read = await TimelineFile.ReadAsync(path, TestContext.Current.CancellationToken);
        read.Samples.ShouldHaveSingleItem().GcHeapBytes.ShouldBe(1000);
        read.Events.Select(e => e.Text).ShouldBe([MonitorRun.GcForcedEvent, "a \"quoted\" note"]);
        Path.GetFileName(path).ShouldBe("counters-42-20261030-120000.csv");
    }

    [Fact]
    public void A_taken_name_gets_a_number_and_no_folder_writes_nothing()
    {
        var at = new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);
        var first = TimelineWriter.Create(_folder, 42, at, NullLogger.Instance, Guid.NewGuid());
        var second = TimelineWriter.Create(_folder, 42, at, NullLogger.Instance, Guid.NewGuid());

        Path.GetFileName(second.Path).ShouldBe("counters-42-20261030-120000-2.csv");
        first.Detach()?.Dispose();
        second.Detach()?.Dispose();
        TimelineWriter.Create(null, 42, at, NullLogger.Instance, Guid.NewGuid()).Path.ShouldBeNull();
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);
}
