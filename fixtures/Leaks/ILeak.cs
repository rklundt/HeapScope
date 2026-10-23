namespace Fixtures.Leaks;

/// <summary>
/// A deliberate, scalable memory leak (ADR-013). Each leak is rooted by a static field of its own class, so the
/// known answers (fixtures/KnownAnswers) can name the root exactly. Leaks are thread safe.
/// </summary>
public interface ILeak
{
    /// <summary>Name used on command lines, in configuration and in the known-answers file name.</summary>
    string Name { get; }

    /// <summary>Items leaked so far.</summary>
    int ItemCount { get; }

    /// <summary>Leaks <paramref name="items"/> more items, each carrying a payload of <paramref name="payloadBytes"/>.</summary>
    void Grow(int items, int payloadBytes);
}

/// <summary>All leaks, by name.</summary>
public static class LeakCatalog
{
    public static IReadOnlyList<ILeak> All { get; } =
    [
        new StaticCacheLeak(),
        new EventHandlerLeak(),
        new ClosureLeak(),
        new UndisposedListLeak(),
        new UnboundedDictionaryLeak(),
    ];

    /// <summary>Resolves "all", "none" or a comma separated list of names (case insensitive).</summary>
    public static IReadOnlyList<ILeak> Resolve(string? names)
    {
        if (string.IsNullOrWhiteSpace(names) || names.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        if (names.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            return All;
        }

        var result = new List<ILeak>();
        foreach (var name in names.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            result.Add(All.FirstOrDefault(l => l.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                ?? throw new ArgumentException($"Unknown leak '{name}'. Known: {string.Join(", ", All.Select(l => l.Name))}.", nameof(names)));
        }

        return result;
    }
}
