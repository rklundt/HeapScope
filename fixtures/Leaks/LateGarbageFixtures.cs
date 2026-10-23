using System.Runtime.CompilerServices;

namespace Fixtures.Leaks;

/// <summary>
/// Garbage that is still on the heap when the dump is taken (sprint 0.4 review): dead conditional weak table
/// entries whose dependent handles are still registered, and objects reachable only through weak references.
/// The objects are promoted to gen2 first and dropped afterwards, and no gen2 collection runs before the dump,
/// so they are present but unreachable. Call after the process's last full collection.
/// </summary>
public static class LateGarbageFixtures
{
    private static readonly ConditionalWeakTable<DeadKey, DeadValue> s_table = new();
    private static readonly List<WeakReference> s_weakReferences = [];
    private static List<DeadKey>? s_keys;
    private static List<WeakOnlyTarget>? s_weakTargets;

    public static int WeakReferenceCount => s_weakReferences.Count;

    public static void Create(int count)
    {
        CreateRooted(count);

        // Promote everything to gen2 while still rooted, then drop the roots. Only a gen2 collection would
        // reclaim them, and none runs before the dump.
        GC.Collect();
        GC.Collect();
        s_keys = null;
        s_weakTargets = null;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CreateRooted(int count)
    {
        s_keys = new List<DeadKey>(count);
        s_weakTargets = new List<WeakOnlyTarget>(count);
        for (var i = 0; i < count; i++)
        {
            var key = new DeadKey(i);
            s_keys.Add(key);
            s_table.Add(key, new DeadValue(i));

            var target = new WeakOnlyTarget(i);
            s_weakTargets.Add(target);
            s_weakReferences.Add(new WeakReference(target));
        }
    }
}

public sealed class DeadKey(int id)
{
    public int Id { get; } = id;
}

public sealed class DeadValue(int id)
{
    public int Id { get; } = id;
}

public sealed class WeakOnlyTarget(int id)
{
    public int Id { get; } = id;
}
