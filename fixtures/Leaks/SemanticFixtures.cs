using System.Runtime.CompilerServices;

namespace Fixtures.Leaks;

/// <summary>
/// CLR semantics that analysis must get right (sprint 0.1 review): dependent handles behind a
/// <see cref="ConditionalWeakTable{TKey,TValue}"/>, and references inside value types embedded in objects and arrays.
/// </summary>
public static class SemanticFixtures
{
    private static readonly ConditionalWeakTable<CwtKey, CwtValue> s_table = new();
    private static readonly List<CwtKey> s_liveKeys = [];
    private static readonly List<StructHolder> s_holders = [];
    private static KeyValuePair<string, KvpTarget>[] s_pairs = [];

    /// <summary>
    /// Creates <paramref name="count"/> live table entries (keys rooted), <paramref name="count"/> dead ones (keys
    /// unreferenced, collected before return), <paramref name="count"/> struct holders and a key-value pair array of
    /// <paramref name="count"/> elements.
    /// </summary>
    public static void Create(int count)
    {
        AddEntries(count, keepKeys: true);
        AddEntries(count, keepKeys: false);

        for (var i = 0; i < count; i++)
        {
            s_holders.Add(new StructHolder { Inner = new InnerStruct(i, new EmbeddedTarget(i)) });
        }

        var pairs = new KeyValuePair<string, KvpTarget>[count];
        for (var i = 0; i < count; i++)
        {
            pairs[i] = new KeyValuePair<string, KvpTarget>("k" + i, new KvpTarget(i));
        }

        s_pairs = pairs;

        // Dead keys and their values become garbage; collect them so a dump shows only live entries.
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    public static int LiveKeyCount => s_liveKeys.Count;

    public static int PairCount => s_pairs.Length;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void AddEntries(int count, bool keepKeys)
    {
        for (var i = 0; i < count; i++)
        {
            var key = new CwtKey(i);
            s_table.Add(key, new CwtValue(i, new byte[64]));
            if (keepKeys)
            {
                s_liveKeys.Add(key);
            }
        }
    }
}

public sealed class CwtKey(int id)
{
    public int Id { get; } = id;
}

public sealed class CwtValue(int id, byte[] data)
{
    public int Id { get; } = id;

    public byte[] Data { get; } = data;
}

public sealed class StructHolder
{
    public InnerStruct Inner;
}

public readonly struct InnerStruct(int number, EmbeddedTarget target)
{
    public int Number { get; } = number;

    public EmbeddedTarget Target { get; } = target;
}

public sealed class EmbeddedTarget(int id)
{
    public int Id { get; } = id;
}

public sealed class KvpTarget(int id)
{
    public int Id { get; } = id;
}
