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
