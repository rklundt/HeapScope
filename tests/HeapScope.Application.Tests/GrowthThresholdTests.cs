using HeapScope.Application.Snapshots;

namespace HeapScope.Application.Tests;

/// <summary>The <c>diff --fail-on-growth</c> limit: the exit code CI relies on (sprint 1.4).</summary>
public sealed class GrowthThresholdTests
{
    [Theory]
    [InlineData("1048576", 1048576L)]
    [InlineData("200B", 200L)]
    [InlineData("512KB", 524288L)]
    [InlineData("512kb", 524288L)]
    [InlineData("50MB", 52428800L)]
    [InlineData(" 50 MB ", 52428800L)]
    [InlineData("1.5GB", 1610612736L)]
    [InlineData("0", 0L)]
    public void Sizes_parse_with_1024_based_units(string text, long expected)
    {
        GrowthThreshold.TryParse(text, out var threshold).ShouldBeTrue();
        threshold.ShouldBe(new GrowthThreshold(expected, null));
    }

    [Theory]
    [InlineData("10%", 10.0)]
    [InlineData("2.5 %", 2.5)]
    [InlineData("0%", 0.0)]
    public void Percents_parse(string text, double expected)
    {
        GrowthThreshold.TryParse(text, out var threshold).ShouldBeTrue();
        threshold.ShouldBe(new GrowthThreshold(null, expected));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("lots")]
    [InlineData("%")]
    [InlineData("MB")]
    [InlineData("-5MB")]
    [InlineData("-1%")]
    [InlineData("Infinity%")]
    [InlineData("NaN")]
    [InlineData("1e30MB")]
    [InlineData("10TB")]
    [InlineData("1,000")]
    public void Anything_else_is_refused(string? text)
    {
        GrowthThreshold.TryParse(text, out _).ShouldBeFalse();
    }

    [Fact]
    public void Growth_at_the_limit_passes_and_one_byte_more_fails()
    {
        var size = new GrowthThreshold(1000, null);
        size.IsExceededBy(1000, baselineBytes: 0).ShouldBeFalse();
        size.IsExceededBy(1001, baselineBytes: 0).ShouldBeTrue();
        size.IsExceededBy(-50_000, baselineBytes: 0).ShouldBeFalse("shrinking never fails");

        var percent = new GrowthThreshold(null, 10);
        percent.LimitFor(12_345).ShouldBe(1234);
        percent.IsExceededBy(1234, 12_345).ShouldBeFalse();
        percent.IsExceededBy(1235, 12_345).ShouldBeTrue();
    }

    [Fact]
    public void A_percent_of_a_zero_baseline_allows_no_growth()
    {
        var percent = new GrowthThreshold(null, 1000);

        percent.LimitFor(0).ShouldBe(0);
        percent.IsExceededBy(0, 0).ShouldBeFalse();
        percent.IsExceededBy(1, 0).ShouldBeTrue();
    }
}
