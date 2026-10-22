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
