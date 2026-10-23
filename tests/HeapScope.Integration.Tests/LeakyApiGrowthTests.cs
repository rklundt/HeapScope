using Fixtures.Leaks;
using HeapScope.Integration.Tests.Infrastructure;

namespace HeapScope.Integration.Tests;

/// <summary>
/// Two snapshots of one live LeakyApi under load: every leak grows by exactly the census per item (the basis of
/// snapshot diffs in 1.4 and growth detectors in 2.6).
/// </summary>
[Trait("Category", "Integration")]
public sealed class LeakyApiGrowthTests
{
    private const int ItemsPerRequest = 100;

    [Fact]
    public async Task Census_types_grow_by_items_times_per_item_between_two_snapshots()
    {
        var ct = TestContext.Current.CancellationToken;
        using var folder = new TempFolder();
        using var api = await FixtureProcess.StartApiAsync(
            "--Leaks:Enabled=all", $"--Leaks:ItemsPerRequest={ItemsPerRequest}", "--Leaks:PayloadBytes=32", "--Leaks:Publishers=64");

        await api.PostWorkAsync(5, ct);
        api.CaptureDump(folder.File("first.dmp"));
        await api.PostWorkAsync(10, ct);
        api.CaptureDump(folder.File("second.dmp"));

        var census = LeakCatalog.All.SelectMany(l => KnownAnswer.Load(l.Name).Census!.Select(c => (Leak: l.Name, Entry: c))).ToList();
        var typeNames = census.Select(c => c.Entry.Type).Distinct().ToList();
        Dictionary<string, long> first, second;
        using (var dump = DumpAnalysis.Open(folder.File("first.dmp")))
        {
            first = dump.CountTypes(typeNames);
        }

        using (var dump = DumpAnalysis.Open(folder.File("second.dmp")))
        {
            second = dump.CountTypes(typeNames);
        }

        // Types shared by several leaks (byte[], string) grow by the sum of their per-item counts.
        var expectedGrowth = census
            .GroupBy(c => c.Entry.Type)
            .ToDictionary(g => g.Key, g => (Growth: 10L * ItemsPerRequest * g.Sum(c => c.Entry.PerItem), Exact: g.All(c => c.Entry.Exact)));
        var mismatches = new List<string>();
        foreach (var (type, (growth, exact)) in expectedGrowth)
        {
            var actual = second[type] - first[type];
            var ok = exact ? actual == growth && first[type] == growth / 2 : actual >= growth;
            if (!ok)
            {
                mismatches.Add($"{type}: first {first[type]}, growth {actual}, expected growth {growth}{(exact ? " exactly" : " or more")}");
            }
        }

        mismatches.ShouldBeEmpty();
    }
}
