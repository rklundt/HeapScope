using HeapScope.Core.Graph;

namespace HeapScope.Core.Synthetic;

/// <summary>Options for <see cref="SyntheticGraphs.Random"/>.</summary>
/// <param name="ObjectCount">Number of objects.</param>
/// <param name="AverageReferences">Average outgoing references per object (0 to 16).</param>
/// <param name="TypeCount">Number of distinct types.</param>
/// <param name="RootCount">Number of retaining roots (the first objects are rooted).</param>
/// <param name="Seed">Random seed; the same options always produce the same graph.</param>
/// <param name="AddInReverseOrder">Adds objects in descending address order, exercising the builder's sort path.</param>
/// <param name="ReferencesByAddress">Adds references by address through <see cref="HeapGraphBuilder.AddReferences"/>
/// (the loader path, one binary search per reference) instead of by index.</param>
public sealed record RandomGraphOptions(
    int ObjectCount,
    double AverageReferences = 3,
    int TypeCount = 1000,
    int RootCount = 1000,
    ulong Seed = 42,
    bool AddInReverseOrder = false,
    bool ReferencesByAddress = false);

/// <summary>
/// Deterministic graph shapes with known answers, from tiny to 50M objects (ARCHITECTURE.md 6, DEVELOPER.md 5).
/// </summary>
public static class SyntheticGraphs
{
    /// <summary>A rooted chain: object i references object i + 1.</summary>
    public static HeapGraph Chain(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        var builder = new SyntheticGraphBuilder();
        var objects = new SyntheticObject[length];
        for (var i = 0; i < length; i++)
        {
            objects[i] = builder.Object("Synthetic.ChainNode");
        }

        for (var i = 0; i + 1 < length; i++)
        {
            builder.Reference(objects[i], objects[i + 1]);
        }

        if (length > 0)
        {
            builder.Root(objects[0], RootKind.StaticField, "Synthetic.Chain.s_head");
        }

        return builder.Build();
    }

    /// <summary>A rooted cycle: object i references object (i + 1) mod length.</summary>
    public static HeapGraph Cycle(int length)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(length, 1);
        var builder = new SyntheticGraphBuilder();
        var objects = new SyntheticObject[length];
        for (var i = 0; i < length; i++)
        {
            objects[i] = builder.Object("Synthetic.CycleNode");
        }

        for (var i = 0; i < length; i++)
        {
            builder.Reference(objects[i], objects[(i + 1) % length]);
        }

        builder.Root(objects[0], RootKind.StrongHandle);
        return builder.Build();
    }

    /// <summary>A rooted complete tree with the given depth (root only = depth 1) and fan-out, in breadth-first order.</summary>
    public static HeapGraph Tree(int depth, int fanOut)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(depth, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(fanOut, 1);
        var builder = new SyntheticGraphBuilder();
        var current = new List<SyntheticObject> { builder.Object("Synthetic.TreeNode") };
        builder.Root(current[0], RootKind.StaticField, "Synthetic.Tree.s_root");
        for (var level = 1; level < depth; level++)
        {
            var next = new List<SyntheticObject>(current.Count * fanOut);
            foreach (var parent in current)
            {
                for (var c = 0; c < fanOut; c++)
                {
                    var child = builder.Object("Synthetic.TreeNode");
                    builder.Reference(parent, child);
                    next.Add(child);
                }
            }

            current = next;
        }

        return builder.Build();
    }

    /// <summary>
    /// A diamond: root A references B and C, both reference D. D is retained through two paths (its immediate
    /// dominator is A).
    /// </summary>
    public static HeapGraph Diamond()
    {
        var builder = new SyntheticGraphBuilder();
        var a = builder.Object("Synthetic.A");
        var b = builder.Object("Synthetic.B");
        var c = builder.Object("Synthetic.C");
        var d = builder.Object("Synthetic.D", size: 1000);
        builder.Reference(a, b).Reference(a, c).Reference(b, d).Reference(c, d);
        builder.Root(a, RootKind.StaticField, "Synthetic.Diamond.s_a");
        return builder.Build();
    }

    /// <summary>
    /// A large random graph built straight through <see cref="HeapGraphBuilder"/> (no per-object managed objects),
    /// suitable for 50M-object benchmarks. Deterministic for a given <see cref="RandomGraphOptions.Seed"/>.
    /// </summary>
    public static HeapGraph Random(RandomGraphOptions options, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfNegative(options.ObjectCount);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.TypeCount, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(options.AverageReferences);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.AverageReferences, 16);

        using var builder = new HeapGraphBuilder();
        for (var t = 0; t < options.TypeCount; t++)
        {
            builder.Types.GetOrAdd($"Synthetic.Type{t:D4}", baseSize: 24);
        }

        var random = new XorShift(options.Seed);
        // Sizes are 24 to 88 bytes; in reverse mode addresses count down from an upper bound with the same spacing.
        const ulong MaxObjectSize = 88;
        var address = options.AddInReverseOrder
            ? SyntheticGraphBuilder.BaseAddress + ((ulong)options.ObjectCount * MaxObjectSize)
            : SyntheticGraphBuilder.BaseAddress;
        const int ReportInterval = 1 << 20;
        for (var i = 0; i < options.ObjectCount; i++)
        {
            var size = 24UL + (random.Next() % 9 * 8);
            if (options.AddInReverseOrder)
            {
                address -= size;
            }

            builder.AddObject(address, (int)(random.Next() % (ulong)options.TypeCount), size, HeapKind.Gen2);
            if (!options.AddInReverseOrder)
            {
                address += size;
            }
            if ((i & (ReportInterval - 1)) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(i);
            }
        }

        builder.FreezeObjects(cancellationToken);

        // Each object gets floor(avg) or floor(avg) + 1 references so the mean equals AverageReferences.
        var whole = (int)Math.Floor(options.AverageReferences);
        var fractionThreshold = (ulong)((options.AverageReferences - whole) * ulong.MaxValue);
        Span<int> targets = stackalloc int[17];
        Span<ulong> targetAddresses = stackalloc ulong[17];
        for (var i = 0; i < options.ObjectCount; i++)
        {
            var count = whole + (random.Next() < fractionThreshold ? 1 : 0);
            for (var r = 0; r < count; r++)
            {
                targets[r] = (int)(random.Next() % (ulong)options.ObjectCount);
            }

            if (options.ReferencesByAddress)
            {
                var frozen = builder.FrozenAddresses;
                for (var r = 0; r < count; r++)
                {
                    targetAddresses[r] = frozen[targets[r]];
                }

                builder.AddReferences(i, targetAddresses[..count]);
            }
            else
            {
                builder.AddReferenceIndices(i, targets[..count]);
            }
            if ((i & (ReportInterval - 1)) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(options.ObjectCount + (long)i);
            }
        }

        var rootCount = Math.Min(options.RootCount, options.ObjectCount);
        for (var i = 0; i < rootCount; i++)
        {
            builder.AddRootIndex(i, RootKind.StrongHandle, RootFlags.Retaining);
        }

        return builder.Build(cancellationToken);
    }

    /// <summary>xorshift64*: fast, deterministic, allocation free.</summary>
    private struct XorShift(ulong seed)
    {
        private ulong _state = seed == 0 ? 0x9E3779B97F4A7C15UL : seed;

        public ulong Next()
        {
            _state ^= _state >> 12;
            _state ^= _state << 25;
            _state ^= _state >> 27;
            return _state * 2685821657736338717UL;
        }
    }
}
