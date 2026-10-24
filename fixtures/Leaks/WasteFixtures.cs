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

using System.Runtime.InteropServices;

namespace Fixtures.Leaks;

/// <summary>
/// Common memory waste with known amounts, for the simple inspections (sprint 1.6, known answer
/// fixtures/KnownAnswers/Waste.json). Every finding uses fixture-owned types or fixture-made text, so runtime objects
/// of the same kind cannot blur the counts. Everything is rooted by <see cref="s_holder"/>.
/// </summary>
public static class WasteFixtures
{
    /// <summary>
    /// Copies of each duplicated string value, by the value's last letter. The values ("HeapScope waste duplicate A") are
    /// only ever built at run time: a literal of the full value would be one more (interned) copy.
    /// </summary>
    public static readonly IReadOnlyDictionary<char, int> DuplicateCopies = new Dictionary<char, int>
    {
        ['A'] = 50,
        ['B'] = 30,
        ['C'] = 20,
    };

    public const string DuplicatePrefix = "HeapScope waste duplicate ";

    public const int LargeArrays = 3;
    public const int LargeArrayLength = 10_000;
    public const int SparseArrays = 4;
    public const int SparseArrayLength = 1_000;
    public const int SparseArrayFilled = 10;
    public const int Finalizable = 25;
    public const int FinalizeSuppressed = 5;
    public const int LargeCopies = 3;
    public const int LargeCopyLength = 200_000;

    private static WasteHolder? s_holder;

    /// <summary>Objects created dead by <see cref="CreateFinalizerBacklog"/>; all but the one being finalized stay queued.</summary>
    public const int Doomed = 11;

    public static WasteHolder Create() => s_holder ??= new WasteHolder();

    /// <summary>
    /// A finalizer backlog (review H1 of 1.6): unreachable objects whose finalizers block on an event that is never set,
    /// so the finalizer thread hangs in the first one and the others stay queued (f-reachable). Call it after the last
    /// <see cref="GC.WaitForPendingFinalizers"/>, which would otherwise never return.
    /// </summary>
    public static void CreateFinalizerBacklog()
    {
        MakeDoomed();
        GC.Collect();
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void MakeDoomed()
    {
        for (var i = 0; i < Doomed; i++)
        {
            _ = new WasteDoomed();
        }
    }

    public static bool Created => s_holder is not null;
}

/// <summary>An element of the fixture's collections.</summary>
public sealed class WasteItem(int id)
{
    public int Id { get; } = id;
}

/// <summary>An element of the fixture's sparse arrays (only these arrays use it).</summary>
public sealed class WasteNode(int id)
{
    public int Id { get; } = id;
}

/// <summary>A 16-byte struct: arrays of it reach the large object heap at 10,000 elements.</summary>
public readonly record struct WasteRecord(long Key, long Value);

/// <summary>An object whose finalizer blocks forever: a stuck finalizer thread.</summary>
public sealed class WasteDoomed
{
    private static readonly ManualResetEventSlim s_never = new();

    ~WasteDoomed()
    {
        s_never.Wait();
    }
}

/// <summary>An object with a finalizer, kept alive.</summary>
public sealed class WasteFinalizable
{
    private static int s_finalized;

    public int Id { get; init; }

    public static int Finalized => s_finalized;

    ~WasteFinalizable()
    {
        Interlocked.Increment(ref s_finalized);
    }
}

public sealed class WasteHolder
{
    public WasteHolder()
    {
        // Duplicates: each copy is a separate string object built at run time (literals would be one interned object).
        var copies = new List<string>();
        foreach (var (letter, count) in WasteFixtures.DuplicateCopies)
        {
            for (var i = 0; i < count; i++)
            {
                copies.Add(string.Concat(WasteFixtures.DuplicatePrefix, letter.ToString()));
            }
        }

        Duplicates = [.. copies];

        // Large arrays: on the large object heap (85,000 bytes or more).
        LargeArrays = [.. Enumerable.Range(0, WasteFixtures.LargeArrays).Select(_ => new WasteRecord[WasteFixtures.LargeArrayLength])];

        // Sparse arrays: 1,000 slots, 10 of them filled.
        SparseArrays = [.. Enumerable.Range(0, WasteFixtures.SparseArrays).Select(a =>
        {
            var array = new WasteNode?[WasteFixtures.SparseArrayLength];
            for (var i = 0; i < WasteFixtures.SparseArrayFilled; i++)
            {
                array[i * 100] = new WasteNode(i);
            }

            return array;
        })];

        // A dense array of the same element type that must not be reported.
        DenseNodes = [.. Enumerable.Range(0, 32).Select(i => new WasteNode(i))];

        Finalizables = [.. Enumerable.Range(0, WasteFixtures.Finalizable + WasteFixtures.FinalizeSuppressed).Select(i => new WasteFinalizable { Id = i })];
        foreach (var suppressed in Finalizables.Take(WasteFixtures.FinalizeSuppressed))
        {
            GC.SuppressFinalize(suppressed);
        }

        for (var i = 0; i < 10; i++)
        {
            OversizedList.Add(new WasteItem(i));
            OversizedDictionary.Add(i, new WasteItem(i));
        }

        for (var i = 0; i < 5; i++)
        {
            OversizedSet.Add(i);
        }

        for (var i = 0; i < 3; i++)
        {
            OversizedQueue.Enqueue(new WasteItem(i));
            OversizedStack.Push(new WasteItem(i));
        }

        // Emptied by removals: _count stays, _freeCount catches up (the count is _count - _freeCount).
        for (var i = 0; i < 50; i++)
        {
            DrainedDictionary.Add(i, new WasteItem(i));
        }

        for (var i = 0; i < 50; i++)
        {
            DrainedDictionary.Remove(i);
        }

        // Large equal copies (review H2 of 1.6): 400 KB each, hashed without building the text.
        LargeCopies = [.. Enumerable.Range(0, WasteFixtures.LargeCopies).Select(_ => new string('y', WasteFixtures.LargeCopyLength))];

        // Pinned: a pinned handle and an array on the pinned object heap.
        PinnedBuffer = new byte[4_096];
        PinnedHandle = GCHandle.Alloc(PinnedBuffer, GCHandleType.Pinned);
        PinnedHeapBuffer = GC.AllocateArray<byte>(1_000, pinned: true);
    }

    public string[] Duplicates { get; }

    public WasteRecord[][] LargeArrays { get; }

    public WasteNode?[][] SparseArrays { get; }

    public WasteNode[] DenseNodes { get; }

    public WasteFinalizable[] Finalizables { get; }

    // Empty with a backing array: the capacity is allocated and unused.
    public List<WasteItem> EmptyList { get; } = new(100);

    public Dictionary<int, WasteItem> EmptyDictionary { get; } = new(100);

    public Dictionary<int, WasteItem> DrainedDictionary { get; } = new(100);

    public string[] LargeCopies { get; }

    // Oversized: capacity far above the count.
    public List<WasteItem> OversizedList { get; } = new(1_000);

    public Dictionary<int, WasteItem> OversizedDictionary { get; } = new(1_000);

    public HashSet<int> OversizedSet { get; } = new(1_000);

    public Queue<WasteItem> OversizedQueue { get; } = new(500);

    public Stack<WasteItem> OversizedStack { get; } = new(500);

    // Right-sized: must not be reported as empty or oversized.
    public List<WasteItem> FullList { get; } = [new WasteItem(1), new WasteItem(2), new WasteItem(3), new WasteItem(4)];

    public byte[] PinnedBuffer { get; }

    public GCHandle PinnedHandle { get; }

    public byte[] PinnedHeapBuffer { get; }
}
