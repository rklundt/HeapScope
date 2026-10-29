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

using System.Globalization;
using Fixtures.Leaks;
using HeapScope.Application.Jobs;
using HeapScope.Application.Snapshots;
using HeapScope.Application.Workers;
using HeapScope.Contracts;
using HeapScope.Integration.Tests.Infrastructure;
using Microsoft.Extensions.Options;

namespace HeapScope.Integration.Tests;

/// <summary>
/// Sprint 3.2 (ADR-031): Similar Retention, a set's key paths and interface grouping through the real worker, on the
/// RetentionPatterns fixture of the shared leaks dump, checked against its known answer and ClrMD.
/// </summary>
[Trait("Category", "Integration")]
public sealed class RetentionViewsTests : IClassFixture<AllLeaksDump>, IAsyncDisposable
{
    private const string Module = "Leaks.dll";

    private readonly AllLeaksDump _shared;
    private readonly TempFolder _folder = new();
    private readonly WorkerHost _host = new(Options.Create(new WorkerHostOptions { WorkerPath = TestPaths.WorkerExe }), new ListLoggerProvider());
    private readonly SnapshotSession _session;
    private readonly string _dump;

    public RetentionViewsTests(AllLeaksDump shared)
    {
        _shared = shared;
        _session = new SnapshotSession(_host, new JobTracker(TimeProvider.System));
        _dump = _folder.File("retention.dmp");
        File.Copy(shared.DumpPath, _dump);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ObjectSetQuery Items => new(ObjectSetSpec.OfType(typeof(PatternItem).FullName!, Module));

    [Fact]
    public async Task Similar_retention_finds_the_fixtures_three_patterns_and_a_group_opens_as_exactly_its_objects()
    {
        var answer = KnownAnswer.Load("RetentionPatterns");
        var handle = await _session.StartOpen(_dump).Result;

        var result = await _session.QuerySimilarRetentionAsync(handle, Items, SimilarRetentionLimits.DefaultGroups, null, Ct);

        result.Groups.Select(g => g.Objects).ShouldBe(answer.RetentionGroups!.Select(g => (long)AllLeaksDump.RetentionItems * g.PerItem));
        (result.GroupCount, result.Unreachable, result.LimitedMembers).ShouldBe((3, 0L, 0L));
        result.Set.Objects.ShouldBe(AllLeaksDump.RetentionItems * 6L);
        for (var rank = 0; rank < result.Groups.Count; rank++)
        {
            var group = result.Groups[rank];
            var fields = answer.RetentionGroups![rank].Paths;
            (group.PathCount, group.Paths.Count).ShouldBe((fields.Count, fields.Count), $"group {rank}");
            group.Paths.Select(p => fields.Single(f => p.Root.Description.Contains(f, StringComparison.Ordinal))).Order().ShouldBe(fields.Order(), $"group {rank}");
            group.FirstType.ShouldBe(typeof(PatternItem).FullName);
        }

        // The middle group (list and dictionary) as a set: exactly its objects.
        var group1 = new ObjectSetQuery(ObjectSetSpec.RetentionGroupOf(Items.Spec, 1));
        var types = await _session.QueryTypeSummaryAsync(handle, TypeGrouping.Type, null, null, true, false, null, Ct, group1);
        types.Result.Set.ShouldNotBeNull().Objects.ShouldBe(AllLeaksDump.RetentionItems * 2L);
        var indexed = _shared.Dump.ReadStatic(typeof(RetentionPatternFixtures).FullName!, "s_indexed");
        _shared.Dump.CountReachable(indexed, typeof(PatternItem).FullName!, followDependentHandles: false).ShouldBe(AllLeaksDump.RetentionItems * 2);
        (await Should.ThrowAsync<WorkerOperationException>(() => _session.QueryTypeSummaryAsync(handle, TypeGrouping.Type, null, null, true, false, null, Ct, new ObjectSetQuery(ObjectSetSpec.RetentionGroupOf(Items.Spec, 3)))))
            .Message.ShouldContain("no retention group 3");
    }

    [Fact]
    public async Task A_sets_key_paths_equal_its_types_key_paths()
    {
        var handle = await _session.StartOpen(_dump).Result;

        var bySet = await _session.QuerySetPathsAsync(handle, Items, 20, null, Ct);
        var byType = await _session.QueryTypePathsAsync(handle, typeof(PatternItem).FullName!, Module, 20, null, Ct);

        bySet.Paths.Select(p => (p.Objects, p.Bytes, p.Root.Description, Steps: string.Join(">", p.Steps.Select(s => s.TypeName))))
            .ShouldBe(byType.Paths.Select(p => (p.Objects, p.Bytes, p.Root.Description, Steps: string.Join(">", p.Steps.Select(s => s.TypeName)))));
        bySet.Objects.ShouldBe(AllLeaksDump.RetentionItems * 6L);
    }

    [Fact]
    public async Task Interface_rows_count_every_implementing_type_and_open_as_sets()
    {
        var handle = await _session.StartOpen(_dump).Result;
        var implementers = typeof(ILeak).Assembly.GetTypes().Where(t => typeof(ILeak).IsAssignableFrom(t) && !t.IsInterface).Select(t => t.FullName!).ToList();
        var expected = _shared.Dump.CountTypes(implementers).Values.Sum();
        expected.ShouldBeGreaterThan(0);

        var summary = await _session.QueryTypeSummaryAsync(handle, TypeGrouping.Interface, null, null, true, null, Ct);
        var rows = (await RetainedSizesTests.AllRowsAsync(summary.Rows)).ToDictionary(r => r[TypeSummaryColumns.Name]!);

        Long(rows[typeof(ILeak).FullName!][TypeSummaryColumns.Objects]).ShouldBe(expected);
        Long(rows["System.IDisposable"][TypeSummaryColumns.Objects]).ShouldBeGreaterThanOrEqualTo((long)AllLeaksDump.Items, "every UndisposedResource is disposable");
        rows.ShouldContainKey(TypeSummaryColumns.NoInterface);
        var set = await _session.QueryTypeSummaryAsync(handle, TypeGrouping.Type, null, null, true, false, null, Ct, new ObjectSetQuery(ObjectSetSpec.OfInterface(typeof(ILeak).FullName!)));
        set.Result.Set.ShouldNotBeNull().Objects.ShouldBe(expected);
        (await Should.ThrowAsync<WorkerOperationException>(() => _session.QueryTypeSummaryAsync(handle, TypeGrouping.Interface, null, null, true, includeRetained: true, null, Ct)))
            .Message.ShouldContain("no retained column");

        var second = _folder.File("retention-2.dmp");
        File.Copy(_dump, second);
        var other = await _session.StartOpen(second).Result;
        var diff = await _session.QueryTypeDiffAsync(handle, other, TypeGrouping.Interface, null, null, true, null, Ct);
        var leakRow = (await RetainedSizesTests.AllRowsAsync(diff.Rows)).Single(r => r[TypeDiffColumns.Name] == typeof(ILeak).FullName);
        (Long(leakRow[TypeDiffColumns.BaselineObjects]), Long(leakRow[TypeDiffColumns.CurrentObjects])).ShouldBe((expected, expected));
    }

    [Fact]
    public async Task The_cli_shows_similar_retention_a_group_key_paths_and_interface_rows()
    {
        var item = typeof(PatternItem).FullName!;
        using var similar = ChildProcess.Start(TestPaths.CliExe, "set", _dump, "--type", item, "--module", Module, "--view", "similar");
        (await similar.WaitForExitAsync(TimeSpan.FromSeconds(300))).ShouldBeTrue(similar.Output);
        similar.Process.ExitCode.ShouldBe(0, similar.Output);
        similar.Output.ShouldContain(string.Create(CultureInfo.InvariantCulture, $"{AllLeaksDump.RetentionItems * 6:N0} members in 3 groups"));
        similar.Output.ShouldContain("s_indexed");

        using var group = ChildProcess.Start(TestPaths.CliExe, "set", _dump, "--type", item, "--module", Module, "--group", "2", "--view", "paths");
        (await group.WaitForExitAsync(TimeSpan.FromSeconds(300))).ShouldBeTrue(group.Output);
        group.Process.ExitCode.ShouldBe(0, group.Output);
        group.Output.ShouldContain(string.Create(CultureInfo.InvariantCulture, $"Key retention paths: {AllLeaksDump.RetentionItems:N0} objects"));
        group.Output.ShouldContain("s_holders");

        using var summary = ChildProcess.Start(TestPaths.CliExe, "summary", _dump, "--group", "interface", "--all");
        (await summary.WaitForExitAsync(TimeSpan.FromSeconds(300))).ShouldBeTrue(summary.Output);
        summary.Process.ExitCode.ShouldBe(0, summary.Output);
        summary.Output.ShouldContain(typeof(ILeak).FullName!);
    }

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        _folder.Dispose();
    }

    private static long Long(string? text) => long.Parse(text!, CultureInfo.InvariantCulture);
}
