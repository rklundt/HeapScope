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
