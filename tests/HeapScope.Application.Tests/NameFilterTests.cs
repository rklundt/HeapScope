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
}
