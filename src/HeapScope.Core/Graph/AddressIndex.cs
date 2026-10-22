namespace HeapScope.Core.Graph;

/// <summary>
/// Fast exact address -> object index lookup over a sorted address column (ARCHITECTURE.md 6.3). A plain binary
/// search over 50M addresses costs about 26 steps with most of them cache misses; this index first maps the address
/// to a bucket of the address range, then binary searches only that bucket's short slice.
/// </summary>
/// <remarks>
/// Buckets are power-of-two address ranges, sized so there are about <see cref="TargetObjectsPerBucket"/> objects
/// per bucket on average, capped at <see cref="MaxBuckets"/> buckets (256 MB of table at most; about 25 MB for 50M
/// objects). Sparse address ranges (regions in .NET 7+) only produce empty buckets, which cost 4 bytes each.
/// </remarks>
internal sealed class AddressIndex
{
    public const int TargetObjectsPerBucket = 8;
    public const int MaxBuckets = 1 << 26;

    private readonly ulong _min;
    private readonly ulong _max;
    private readonly int _shift;
    private readonly int[] _bucketStarts;

    private AddressIndex(ulong min, ulong max, int shift, int[] bucketStarts)
    {
        _min = min;
        _max = max;
        _shift = shift;
        _bucketStarts = bucketStarts;
    }

    /// <summary>Bytes held by the bucket table.</summary>
    public long ByteLength => (long)_bucketStarts.Length * sizeof(int);

    public static AddressIndex Build(ReadOnlySpan<ulong> sortedAddresses)
    {
        if (sortedAddresses.IsEmpty)
        {
            return new AddressIndex(1, 0, 0, [0, 0]);
        }

        var min = sortedAddresses[0];
        var max = sortedAddresses[^1];
        var span = max - min;
        var desiredBuckets = (ulong)Math.Clamp(sortedAddresses.Length / TargetObjectsPerBucket, 1, MaxBuckets);
        var shift = 0;
        while ((span >> shift) + 1 > desiredBuckets)
        {
            shift++;
        }

        var bucketCount = (int)((span >> shift) + 1);
        var starts = GC.AllocateUninitializedArray<int>(bucketCount + 1);
        var bucket = 0;
        for (var i = 0; i < sortedAddresses.Length; i++)
        {
            var target = (int)((sortedAddresses[i] - min) >> shift);
            while (bucket <= target)
            {
                starts[bucket++] = i;
            }
        }

        while (bucket <= bucketCount)
        {
            starts[bucket++] = sortedAddresses.Length;
        }

        return new AddressIndex(min, max, shift, starts);
    }

    /// <summary>Index of the object at exactly <paramref name="address"/>, or -1.</summary>
    public int IndexOf(ReadOnlySpan<ulong> sortedAddresses, ulong address)
    {
        if (address < _min || address > _max)
        {
            return -1;
        }

        var bucket = (int)((address - _min) >> _shift);
        var start = _bucketStarts[bucket];
        var length = _bucketStarts[bucket + 1] - start;
        if (length == 0)
        {
            return -1;
        }

        var found = sortedAddresses.Slice(start, length).BinarySearch(address);
        return found >= 0 ? start + found : -1;
    }

    /// <summary>Number of address bits covered by one bucket (diagnostics and tests).</summary>
    public int BucketBits => _shift;

    /// <summary>Bucket count (diagnostics and tests).</summary>
    public int BucketCount => _bucketStarts.Length - 1;
}
