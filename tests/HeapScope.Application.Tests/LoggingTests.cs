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

using HeapScope.Application.Logging;
using HeapScope.Application.Workers;
using Microsoft.Extensions.Logging;

namespace HeapScope.Application.Tests;

public sealed class LoggingTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "HeapScopeTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void File_logger_writes_a_daily_file_with_level_category_and_exception()
    {
        using (var provider = new FileLoggerProvider(_directory, LogLevel.Information))
        {
            var logger = provider.CreateLogger("Test.Category");
            logger.LogDebug("hidden");
            logger.LogWarning("visible {Value}", 7);
            logger.LogError(new InvalidOperationException("broken"), "failed");
        }

        var file = Directory.GetFiles(_directory, "heapscope-*.log").ShouldHaveSingleItem();
        var text = File.ReadAllText(file);
        text.ShouldContain("[WRN] Test.Category: visible 7");
        text.ShouldContain("[ERR] Test.Category: failed");
        text.ShouldContain("InvalidOperationException: broken");
        text.ShouldNotContain("hidden");
    }

    [Fact]
    public void Two_processes_on_one_folder_keep_every_line_in_files_of_their_own()
    {
        // Sprint 2.3.7 review H1: one shared daily file let a second process write over the first one's lines.
        using (var ui = new FileLoggerProvider(_directory, app: "ui", processId: 100))
        using (var cli = new FileLoggerProvider(_directory, app: "cli", processId: 200))
        {
            var a = ui.CreateLogger("A");
            var b = cli.CreateLogger("B");
            for (var i = 0; i < 50; i++)
            {
                a.LogInformation("ui line {Index}", i);
                b.LogInformation("cli line {Index}", i);
            }
        }

        var files = Directory.GetFiles(_directory, "heapscope-*.log").Select(Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
        files.Length.ShouldBe(2);
        files[0].ShouldEndWith("-cli-200.log");
        files[1].ShouldEndWith("-ui-100.log");
        var uiText = File.ReadAllText(Path.Combine(_directory, files[1]!));
        uiText.ShouldStartWith("# HeapScope ");
        uiText.ShouldContain("ui pid 100");
        Enumerable.Range(0, 50).ShouldAllBe(i => uiText.Contains($"ui line {i}" + Environment.NewLine, StringComparison.Ordinal));
        uiText.ShouldNotContain("cli line");
    }

    [Fact]
    public void A_full_file_rolls_over_and_old_files_are_removed()
    {
        Directory.CreateDirectory(_directory);
        var old = Path.Combine(_directory, "heapscope-20200101-ui-1.log");
        File.WriteAllText(old, "old");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-30));
        var recent = Path.Combine(_directory, "heapscope-recent-ui-2.log");
        File.WriteAllText(recent, "recent");

        using (var provider = new FileLoggerProvider(_directory, app: "ui", processId: 7, maxFileBytes: 2048, keepDays: 14))
        {
            var logger = provider.CreateLogger("C");
            for (var i = 0; i < 100; i++)
            {
                logger.LogInformation("line {Index} {Padding}", i, new string('x', 40));
            }
        }

        File.Exists(old).ShouldBeFalse("files older than the kept days are removed");
        File.Exists(recent).ShouldBeTrue();
        var parts = Directory.GetFiles(_directory, "heapscope-*-ui-7*.log");
        parts.Length.ShouldBeGreaterThan(1, "the file rolled over at its size limit");
        parts.ShouldContain(p => p.EndsWith("-ui-7-2.log", StringComparison.Ordinal));
        parts.ShouldAllBe(p => File.ReadAllText(p).StartsWith("# HeapScope ", StringComparison.Ordinal));
    }

    [Fact]
    public void Lines_carry_an_offset_and_their_scopes()
    {
        using (var provider = new FileLoggerProvider(_directory, app: "ui", processId: 9))
        {
            provider.SetScopeProvider(new LoggerExternalScopeProvider());
            var logger = provider.CreateLogger("C");
            using (logger.BeginScope(new Dictionary<string, object?> { ["TraceId"] = "abc123" }))
            {
                logger.LogInformation("inside");
            }

            logger.LogInformation("outside");
        }

        var lines = File.ReadAllLines(Directory.GetFiles(_directory, "heapscope-*.log").ShouldHaveSingleItem());
        lines.ShouldContain(l => l.Contains("C: inside => TraceId:abc123", StringComparison.Ordinal));
        lines.ShouldContain(l => l.EndsWith("C: outside", StringComparison.Ordinal));

        // ISO 8601 with the offset: 2026-10-26T10:15:08.123+02:00.
        lines[1].ShouldMatch(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}[+-]\d{2}:\d{2} \[INF\] ");
    }

    [Theory]
    [InlineData(null, LogLevel.Information)]
    [InlineData("debug", LogLevel.Debug)]
    [InlineData("Warning", LogLevel.Warning)]
    [InlineData("None", LogLevel.Information)]
    [InlineData("loud", LogLevel.Information)]
    public void The_file_level_is_read_from_configuration(string? value, LogLevel expected) =>
        FileLoggerProvider.ParseLevel(value).ShouldBe(expected);

    [Fact]
    public void Forwarded_worker_lines_name_the_worker_and_its_generation()
    {
        var logger = new RecordingLogger();
        WorkerLogForwarder.Forward(logger, "12:00:00.000 info: HeapScope.Worker[0] started", workerPid: 4321, generation: 2);
        logger.Lines.ShouldHaveSingleItem().ShouldBe("w4321 g2 12:00:00.000 info: HeapScope.Worker[0] started");
    }

    [Theory]
    [InlineData(137, "out-of-memory")]
    [InlineData(unchecked((int)0xE0434352), "unhandled .NET exception")]
    [InlineData(1, null)]
    public void Worker_exit_codes_are_explained(int code, string? expected)
    {
        if (expected is null)
        {
            WorkerLogForwarder.ExplainExitCode(code).ShouldBeNull();
        }
        else
        {
            WorkerLogForwarder.ExplainExitCode(code).ShouldNotBeNull().ShouldContain(expected);
        }
    }

    [Fact]
    public void File_logger_ignores_writes_after_dispose()
    {
        var provider = new FileLoggerProvider(_directory);
        var logger = provider.CreateLogger("C");
        provider.Dispose();

        Should.NotThrow(() => logger.LogInformation("after dispose"));
    }

    [Theory]
    [InlineData("12:00:00.000 info: HeapScope.Worker[0] started", LogLevel.Information)]
    [InlineData("12:00:00.000 warn: X[0] careful", LogLevel.Warning)]
    [InlineData("12:00:00.000 fail: X[0] broken", LogLevel.Error)]
    [InlineData("12:00:00.000 crit: X[0] dead", LogLevel.Critical)]
    [InlineData("12:00:00.000 dbug: X[0] detail", LogLevel.Debug)]
    [InlineData("   at System.Something.Method()", LogLevel.Warning)]
    [InlineData("Unhandled exception. System.Exception: info: in message", LogLevel.Warning)]
    public void Worker_log_lines_keep_their_level(string line, LogLevel expected)
    {
        WorkerLogForwarder.LevelOf(line).ShouldBe(expected);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}
