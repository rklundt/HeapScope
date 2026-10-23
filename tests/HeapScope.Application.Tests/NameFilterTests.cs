using HeapScope.Contracts;

namespace HeapScope.Application.Tests;

/// <summary>The type-to-filter rules shared by worker and UI (sprint 1.3).</summary>
public sealed class NameFilterTests
{
    [Theory]
    [InlineData("Fixtures.Leaks.StaticCacheEntry", "cache", true)]
    [InlineData("Fixtures.Leaks.StaticCacheEntry", "CACHE", false)]
    [InlineData("Fixtures.Leaks.StaticCacheEntry", "Cache", true)]
    [InlineData("Fixtures.Leaks.StaticCacheEntry", "SCE", true)]
    [InlineData("Fixtures.Leaks.StaticCacheEntry", "StaCaEn", true)]
    [InlineData("Fixtures.Leaks.StaticCacheEntry", "SEC", false)]
    [InlineData("System.Collections.Generic.Dictionary<Microsoft.Extensions.ServiceKey, System.Object>", "SCE", false)]
    [InlineData("Fixtures.Leaks.StaticCacheEntry", "*leaks*entry", true)]
    [InlineData("Fixtures.Leaks.StaticCacheEntry", "*entry*leaks", false)]
    [InlineData("Fixtures.Leaks.StaticCacheEntry", "*CACHE*", true)]
    [InlineData("Fixtures.Leaks.StaticCacheEntry", "*", true)]
    [InlineData("Fixtures.Leaks.StaticCacheEntry", " SCE ", true)]
    [InlineData("System.Collections.Generic.Dictionary<System.Int32, Fixtures.Leaks.StaticCacheEntry>", "SCE", true)]
    [InlineData("Order", "ordersandmore", false)]
    [InlineData("anything", "", true)]
    [InlineData("anything", "   ", true)]
    public void Filters_match_as_documented(string name, string filter, bool expected)
    {
        NameFilter.IsMatch(name, filter).ShouldBe(expected);
    }

    [Fact]
    public void Ranges_mark_the_matched_characters_for_highlighting()
    {
        NameFilter.Match("Fixtures.Leaks.StaticCacheEntry", "cache").ShouldBe([(21, 5)]);
        NameFilter.Match("Fixtures.Leaks.StaticCacheEntry", "SCE").ShouldBe([(15, 1), (21, 1), (26, 1)]);
        NameFilter.Match("Fixtures.Leaks.StaticCacheEntry", "*leaks*entry").ShouldBe([(9, 5), (26, 5)]);
        NameFilter.Match("x", null).ShouldBeEmpty();
        NameFilter.Match("x", "y").ShouldBeNull();
    }
}
