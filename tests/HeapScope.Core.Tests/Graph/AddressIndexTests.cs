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

using HeapScope.Core.Graph;

namespace HeapScope.Core.Tests.Graph;

public class AddressIndexTests
{
    [Fact]
    public void Empty_index_finds_nothing()
    {
        var index = AddressIndex.Build([]);

        index.IndexOf([], 0x1000).ShouldBe(-1);
    }

    [Fact]
    public void Finds_every_address_and_nothing_between_them()
    {
        ulong[] addresses = [0x1000, 0x1018, 0x1030, 0x5000, 0x5008, 0xFFFF_0000];
        var index = AddressIndex.Build(addresses);

        for (var i = 0; i < addresses.Length; i++)
        {
            index.IndexOf(addresses, addresses[i]).ShouldBe(i);
            index.IndexOf(addresses, addresses[i] + 1).ShouldBe(-1);
        }

        index.IndexOf(addresses, 0xFFF).ShouldBe(-1);
        index.IndexOf(addresses, 0xFFFF_0001).ShouldBe(-1);
        index.IndexOf(addresses, ulong.MaxValue).ShouldBe(-1);
    }

    [Fact]
    public void Sparse_regions_spread_over_a_huge_range_are_found()
    {
        // Two dense clusters 256 GB apart, like .NET 7+ regions.
        var addresses = new List<ulong>();
        for (var i = 0; i < 50_000; i++)
        {
            addresses.Add(0x0000_0200_0000_0000UL + ((ulong)i * 48));
        }

        for (var i = 0; i < 50_000; i++)
        {
            addresses.Add(0x0000_4200_0000_0000UL + ((ulong)i * 48));
        }

        var sorted = addresses.ToArray();
        var index = AddressIndex.Build(sorted);

        index.BucketCount.ShouldBeLessThanOrEqualTo(AddressIndex.MaxBuckets);
        for (var i = 0; i < sorted.Length; i += 997)
        {
            index.IndexOf(sorted, sorted[i]).ShouldBe(i);
        }

        index.IndexOf(sorted, 0x0000_3000_0000_0000UL).ShouldBe(-1);
    }

    [Fact]
    public void Single_address_index_works()
    {
        ulong[] addresses = [0x4242];
        var index = AddressIndex.Build(addresses);

        index.IndexOf(addresses, 0x4242).ShouldBe(0);
        index.IndexOf(addresses, 0x4243).ShouldBe(-1);
    }
}
