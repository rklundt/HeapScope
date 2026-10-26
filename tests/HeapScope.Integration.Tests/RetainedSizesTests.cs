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
using System.IO.Hashing;
using System.Text.Json;
using Fixtures.Leaks;
using HeapScope.Application.Jobs;
using HeapScope.Application.Snapshots;
using HeapScope.Application.Workers;
using HeapScope.Contracts;
using HeapScope.Integration.Tests.Infrastructure;
using Microsoft.Extensions.Options;

namespace HeapScope.Integration.Tests;

/// <summary>Sprint 2.3: retained sizes through the real worker, against the known answers and an independent computation.</summary>
[Trait("Category", "Integration")]
public sealed class RetainedSizesTests : IClassFixture<AllLeaksDump>, IAsyncDisposable
{
    private const string Module = "Leaks.dll";

    private readonly AllLeaksDump _shared;
    private readonly TempFolder _folder = new();
    private readonly WorkerHost _host = new(Options.Create(new WorkerHostOptions { WorkerPath = TestPaths.WorkerExe }), new ListLoggerProvider());
    private readonly SnapshotSession _session;
    private readonly string _dump;

    public RetainedSizesTests(AllLeaksDump shared)
    {
        _shared = shared;
        _session = new SnapshotSession(_host, new JobTracker(TimeProvider.System));
        _dump = _folder.File("leaks.dmp");
        File.Copy(shared.DumpPath, _dump);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static IEnumerable<string> Rooted => LeakCatalog.All.Select(l => l.Name).Concat(["ConditionalWeakTable", "EmbeddedStruct"]);

    [Fact]
    public async Task Every_rooted_items_retained_size_matches_an_independent_computation_and_the_known_minimum()
    {
        var handle = await _session.StartOpen(_dump).Result;
        var mismatches = new List<string>();
        foreach (var name in Rooted)
        {
            var answer = KnownAnswer.Load(name);
            var payload = answer.Kind == "leak" ? AllLeaksDump.Payload : 0;
            var minimum = answer.MinRetainedBytesPerItem!.Base + (answer.MinRetainedBytesPerItem.PayloadMultiplier * payload);

            // The first, middle and last item (review M12), each against a brute-force walk of the dump.
            var items = _shared.Dump.Instances(answer.Types[0].Type).ToList();
            foreach (var anchor in new[] { items[0], items[items.Count / 2], items[^1] })
            {
                var expected = (long)_shared.Dump.RetainedBytes(anchor);
                var retained = await _session.QueryRetainedAsync(handle, anchor.Address, null, Ct);
                if (!retained.Reachable || retained.RetainedBytes != expected || retained.RetainedBytes < minimum)
                {
                    mismatches.Add($"{name} at 0x{anchor.Address:x}: HeapScope {retained.RetainedBytes} (reachable {retained.Reachable}), ClrMD {expected}, minimum {minimum}");
                }
            }
        }

        mismatches.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_frozen_literal_is_alive_and_only_the_roots_dominate_it()
    {
        // ADR-024 on a real dump (review M12): a string literal in the frozen heap is never collected.
        var literal = _shared.Dump.ReadStatic("Fixtures.Leaks.RuntimeFixtures", "s_literal");
        var handle = await _session.StartOpen(_dump).Result;

        var retained = await _session.QueryRetainedAsync(handle, literal.Address, null, Ct);

        (retained.Reachable, retained.DominatedByRoots).ShouldBe((true, true));
        retained.RetainedBytes.ShouldBe((long)_shared.Dump.RetainedBytes(literal));
    }

    [Fact]
    public async Task A_leak_types_retained_lower_bound_covers_its_items_and_late_garbage_retains_nothing()
    {
        var handle = await _session.StartOpen(_dump).Result;
        var summary = await _session.QueryTypeSummaryAsync(handle, TypeGrouping.Type, "Fixtures.Leaks", TypeSummaryColumns.Retained, true, includeRetained: true, null, Ct);
        var rows = await AllRowsAsync(summary.Rows);
        await summary.Rows.ReleaseAsync(Ct);
        summary.Result.Dominators.ShouldNotBeNull().Saved.ShouldBeTrue("the snapshot has an index cache, so the dominators are saved next to it");
        var mismatches = new List<string>();
        foreach (var name in LeakCatalog.All.Select(l => l.Name))
        {
            var answer = KnownAnswer.Load(name);
            var minimum = (long)AllLeaksDump.Items * (answer.MinRetainedBytesPerItem!.Base + (answer.MinRetainedBytesPerItem.PayloadMultiplier * AllLeaksDump.Payload));
            var row = rows.Single(r => r[TypeSummaryColumns.TypeName] == answer.Types[0].Type && r[TypeSummaryColumns.Module] == Module);
            var retained = long.Parse(row[TypeSummaryColumns.Retained]!, CultureInfo.InvariantCulture);
            if (retained < minimum)
            {
                mismatches.Add($"{name}: {retained} retained, at least {minimum} expected");
            }
        }

        mismatches.ShouldBeEmpty();
        foreach (var type in KnownAnswer.Load("LateGarbage").PresentButUnreachable!)
        {
            var instances = await _session.QueryInstancesAsync(handle, new InstancesQuery(type, Module, IncludeRetained: true), null, Ct);
            var cells = (await AllRowsAsync(instances.Rows)).Select(r => r[InstanceColumns.Retained]).ToList();
            await instances.Rows.ReleaseAsync(Ct);
            cells.Count.ShouldBe(AllLeaksDump.LateGarbageItems, type);
            cells.ShouldAllBe(c => c == null, type);
            var row = rows.SingleOrDefault(r => r[TypeSummaryColumns.TypeName] == type);
            row?[TypeSummaryColumns.Retained].ShouldBe("0", type);
        }
    }

    [Fact]
    public async Task A_leak_types_objects_retain_together_at_least_their_lower_bound_and_their_items()
    {
        // Sprint 2.4: the exact retention of each leak's item type, next to the type summary's lower bound, and equal to
        // an independent walk of the dump with ClrMD (the 2.4 review).
        var handle = await _session.StartOpen(_dump).Result;
        var reachable = _shared.Dump.ReachableFromRoots();
        var mismatches = new List<string>();
        foreach (var name in LeakCatalog.All.Select(l => l.Name))
        {
            var answer = KnownAnswer.Load(name);
            var minimum = (long)AllLeaksDump.Items * (answer.MinRetainedBytesPerItem!.Base + (answer.MinRetainedBytesPerItem.PayloadMultiplier * AllLeaksDump.Payload));
            var set = await _session.QueryRetainedBySetAsync(handle, answer.Types[0].Type, Module, null, Ct);
            var rows = await AllRowsAsync(set.Rows);
            await set.Rows.ReleaseAsync(Ct);
            var result = set.Result;
            var ownRow = rows.SingleOrDefault(r => r[RetainedBySetColumns.TypeName] == answer.Types[0].Type);
            var expected = (long)_shared.Dump.SetRetainedBytes(answer.Types[0].Type, reachable);
            if (result.RetainedBytes != expected)
            {
                mismatches.Add($"{name}: HeapScope {result.RetainedBytes}, ClrMD {expected}");
            }

            if (result.MatchedTypes != 1 || result.SetObjects != AllLeaksDump.Items || result.RetainedBytes < result.LowerBoundBytes
                || result.RetainedBytes < minimum || rows.Sum(r => long.Parse(r[RetainedBySetColumns.Retained]!, CultureInfo.InvariantCulture)) != result.RetainedBytes
                || ownRow is null || long.Parse(ownRow[RetainedBySetColumns.Retained]!, CultureInfo.InvariantCulture) != result.SetBytes)
            {
                mismatches.Add($"{name}: {result.SetObjects} objects retain {result.RetainedBytes} (lower bound {result.LowerBoundBytes}, at least {minimum} expected, {rows.Count} rows)");
            }
        }

        mismatches.ShouldBeEmpty();
        var garbage = await _session.QueryRetainedBySetAsync(handle, KnownAnswer.Load("LateGarbage").PresentButUnreachable![0], Module, null, Ct);
        (garbage.Result.MatchedTypes, garbage.Result.SetObjects, garbage.Result.RetainedBytes, garbage.Result.Types.TotalCount).ShouldBe((1, 0L, 0L, 0));
        garbage.Result.AllObjects.ShouldBe(AllLeaksDump.LateGarbageItems, "the objects no root reaches are counted apart");
        (await Should.ThrowAsync<WorkerOperationException>(() => _session.QueryRetainedBySetAsync(handle, "Fixtures.Leaks.NoSuchType", Module, null, Ct))).ErrorCode.ShouldBe(HeapScopeErrorCode.InvalidArgument);
    }

    [Fact]
    public async Task Dominators_are_saved_reused_on_the_next_open_and_rebuilt_when_their_rules_change()
    {
        var address = First("Fixtures.Leaks.StaticCacheEntry").Address;
        var handle = await _session.StartOpen(_dump).Result;
        var computed = await _session.QueryRetainedAsync(handle, address, null, Ct);
        await _session.CloseAsync(handle, Ct);
        var sidecar = _dump + ".dom.hsidx";

        handle = await _session.StartOpen(_dump).Result;
        var reopened = await _session.QueryRetainedAsync(handle, address, null, Ct);
        await _session.CloseAsync(handle, Ct);
        SetSemanticsVersion(sidecar, 1);
        handle = await _session.StartOpen(_dump).Result;
        var rebuilt = await _session.QueryRetainedAsync(handle, address, null, Ct);

        (computed.Index.FromSidecar, computed.Index.Saved).ShouldBe((false, true));
        File.Exists(sidecar).ShouldBeTrue();
        reopened.Index.FromSidecar.ShouldBeTrue();
        reopened.RetainedBytes.ShouldBe(computed.RetainedBytes);
        (rebuilt.Index.FromSidecar, rebuilt.Index.Saved).ShouldBe((false, true), "a sidecar computed under other rules is computed again");
        rebuilt.RetainedBytes.ShouldBe(computed.RetainedBytes);
    }

    [Fact]
    public async Task A_mapped_snapshot_keeps_its_dominators_mapped_and_agrees_with_one_in_memory()
    {
        var address = First("Fixtures.Leaks.StaticCacheEntry").Address;
        var handle = await _session.StartOpen(_dump, mode: SnapshotMemoryMode.Mapped).Result;

        var mapped = await _session.QueryRetainedAsync(handle, address, null, Ct);
        await _session.CloseAsync(handle, Ct);
        handle = await _session.StartOpen(_dump, mode: SnapshotMemoryMode.InMemory).Result;
        var inMemory = await _session.QueryRetainedAsync(handle, address, null, Ct);

        mapped.Index.Mode.ShouldBe(SnapshotMemoryMode.Mapped);
        inMemory.Index.Mode.ShouldBe(SnapshotMemoryMode.InMemory);
        (inMemory.RetainedBytes, inMemory.DominatorAddress).ShouldBe((mapped.RetainedBytes, mapped.DominatorAddress));
    }

    [Fact]
    public async Task An_object_names_its_dominator_and_bad_addresses_are_refused()
    {
        var handle = await _session.StartOpen(_dump).Result;
        var entry = First("Fixtures.Leaks.StaticCacheEntry");
        var garbage = First(KnownAnswer.Load("LateGarbage").PresentButUnreachable![0]);

        var held = await _session.QueryRetainedAsync(handle, entry.Address, null, Ct);
        var dead = await _session.QueryRetainedAsync(handle, garbage.Address, null, Ct);

        // An entry lives in the dictionary's entries array, which the dictionary owns.
        held.DominatorType.ShouldNotBeNull().ShouldContain("Entry[]");
        held.DominatedByRoots.ShouldBeFalse();
        (dead.Reachable, dead.RetainedBytes, dead.DominatorAddress).ShouldBe((false, 0L, 0UL));
        dead.UnreachableNote.ShouldContain("garbage");
        (await Should.ThrowAsync<WorkerOperationException>(() => _session.QueryRetainedAsync(handle, 0x10, null, Ct))).ErrorCode.ShouldBe(HeapScopeErrorCode.InvalidArgument);
    }

    [Fact]
    public async Task The_cli_prints_retained_sizes_for_types_objects_and_one_object()
    {
        var entry = First("Fixtures.Leaks.StaticCacheEntry").Address;
        var address = string.Create(CultureInfo.InvariantCulture, $"0x{entry:x}");

        var (summaryCode, summary) = await RunCliAsync("summary", _dump, "--filter", "Fixtures.Leaks", "--sort", "retained", "--format", "json");
        var (instancesCode, instances) = await RunCliAsync("instances", _dump, "--type", "Fixtures.Leaks.StaticCacheEntry", "--retained", "--top", "3");
        var (objectCode, text) = await RunCliAsync("object", _dump, address, "--retained", "--references", "0");

        summaryCode.ShouldBe(0, summary);
        using (var json = JsonDocument.Parse(summary[summary.IndexOf('{', StringComparison.Ordinal)..(summary.LastIndexOf('}') + 1)]))
        {
            json.RootElement.GetProperty("RetainedIsLowerBound").GetBoolean().ShouldBeTrue();
            json.RootElement.GetProperty("Rows")[0].GetProperty("Retained").GetInt64().ShouldBeGreaterThan(0);
        }

        instancesCode.ShouldBe(0, instances);
        instances.ShouldContain("Retained sizes: dominators read from the saved index");

        // Review H2: an object no root reaches has a null retained cell, written as JSON null.
        var garbageType = KnownAnswer.Load("LateGarbage").PresentButUnreachable![0];
        var (garbageCode, garbage) = await RunCliAsync("instances", _dump, "--type", garbageType, "--module", Module, "--retained", "--top", "3", "--format", "json");
        garbageCode.ShouldBe(0, garbage);
        using (var json = JsonDocument.Parse(garbage[garbage.IndexOf('{', StringComparison.Ordinal)..(garbage.LastIndexOf('}') + 1)]))
        {
            json.RootElement.GetProperty("Rows")[0].GetProperty("Retained").ValueKind.ShouldBe(JsonValueKind.Null);
        }

        objectCode.ShouldBe(0, text);
        text.ShouldContain("Retained:");
        text.ShouldContain("Dominator:   0x");
    }

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        _folder.Dispose();
    }

    internal static async Task<List<Dictionary<string, string?>>> AllRowsAsync(ResultSetHandle rows)
    {
        var page = await rows.GetRowsAsync(0, rows.Info.TotalCount, Ct);
        var columns = rows.Info.Columns;
        return [.. page.Rows.Select(r => columns.Select((c, i) => (c.Name, r[i])).ToDictionary(x => x.Name, x => x.Item2))];
    }

    /// <summary>
    /// Rewrites a sidecar's semantics version (header offset 20) and its header checksum (offset 48), as if an older
    /// HeapScope had computed it under other rules.
    /// </summary>
    private static void SetSemanticsVersion(string path, int version)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite);
        var region = new byte[4096];
        stream.ReadExactly(region);
        BitConverter.TryWriteBytes(region.AsSpan(20), version).ShouldBeTrue();
        var sections = BitConverter.ToInt32(region, 16);
        BitConverter.TryWriteBytes(region.AsSpan(48), 0UL).ShouldBeTrue();
        BitConverter.TryWriteBytes(region.AsSpan(48), XxHash3.HashToUInt64(region.AsSpan(0, 64 + (40 * sections)))).ShouldBeTrue();
        stream.Position = 0;
        stream.Write(region);
    }

    private Microsoft.Diagnostics.Runtime.ClrObject First(string type) => _shared.Dump.Instances(type).First();

    private static async Task<(int Code, string Output)> RunCliAsync(params string[] arguments)
    {
        using var cli = ChildProcess.Start(TestPaths.CliExe, arguments);
        (await cli.WaitForExitAsync(TimeSpan.FromSeconds(180))).ShouldBeTrue(cli.Output);
        return (cli.Process.ExitCode, cli.Output);
    }
}
