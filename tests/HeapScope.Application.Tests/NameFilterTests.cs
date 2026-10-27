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

    [Theory]
    [InlineData("Fixtures.Leaks.StaticCacheEntry", "leaks entry", true)]
    [InlineData("Fixtures.Leaks.StaticCacheEntry", "entry leaks", true)]
    [InlineData("Fixtures.Leaks.StaticCacheEntry", "leaks order", false)]
    [InlineData("Fixtures.Leaks.StaticCacheEntry", "leaks -cache", false)]
    [InlineData("Fixtures.Leaks.StaticCacheEntry", "leaks -order", true)]
    [InlineData("Fixtures.Leaks.StaticCacheEntry", "-SCE", false)]
    [InlineData("Fixtures.Leaks.StaticCacheEntry", "-", true)]
    [InlineData("Fixtures.Leaks.StaticCacheEntry", "cache module:leaky", true)]
    [InlineData("Fixtures.Leaks.StaticCacheEntry", "cache m:LeakyApi.dll", true)]
    [InlineData("Fixtures.Leaks.StaticCacheEntry", "cache module:system", false)]
    [InlineData("Fixtures.Leaks.StaticCacheEntry", "-module:system", true)]
    [InlineData("Fixtures.Leaks.StaticCacheEntry", "-module:leaky", false)]
    [InlineData("Fixtures.Leaks.StaticCacheEntry", "module:", true)]
    [InlineData("Fixtures.Leaks.StaticCacheEntry", "order -m:", false)]
    public void Terms_must_all_match_minus_leaves_out_and_module_tests_the_module(string name, string filter, bool expected)
    {
        NameFilter.IsMatch(name, "LeakyApi.dll", filter).ShouldBe(expected);
    }

    [Fact]
    public void A_row_without_a_module_never_matches_a_module_term()
    {
        NameFilter.IsMatch("Fixtures.Leaks", null, "module:leaky").ShouldBeFalse();
        NameFilter.IsMatch("Fixtures.Leaks", string.Empty, "-module:leaky").ShouldBeTrue();
        NameFilter.IsMatch("Fixtures.Leaks", "module:leaky").ShouldBeFalse("the name-only overload knows no module");
    }

    [Fact]
    public void Highlighting_marks_every_name_term_in_order_and_skips_module_terms()
    {
        NameFilter.Match("Fixtures.Leaks.StaticCacheEntry", "entry leaks m:app").ShouldBe([(9, 5), (26, 5)]);
        NameFilter.Match("Fixtures.Leaks.StaticCacheEntry", "leaks -cache").ShouldBeNull();
        NameFilter.Terms("-m:App.dll cache").ShouldBe([("App.dll", true, true), ("cache", false, false)]);
    }
}
