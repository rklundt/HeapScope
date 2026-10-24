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
using System.Text.Json;
using Fixtures.Leaks;
using HeapScope.Application.Jobs;
using HeapScope.Application.Snapshots;
using HeapScope.Application.Workers;
using HeapScope.Contracts;
using HeapScope.Integration.Tests.Infrastructure;
using HeapScope.Storage;
using Microsoft.Extensions.Options;

namespace HeapScope.Integration.Tests;

/// <summary>
/// Sprint 2.1: who references an object or a type, through the worker and the session, against the known answers: each
/// leak's items are referenced by the type its path shape names just before the item.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ReferrersTests : IClassFixture<AllLeaksDump>, IAsyncDisposable
{
    private const string Module = "Leaks.dll";

    private readonly TempFolder _folder = new();
    private readonly WorkerHost _host = new(Options.Create(new WorkerHostOptions { WorkerPath = TestPaths.WorkerExe }), new ListLoggerProvider());
    private readonly SnapshotSession _session;
    private readonly string _dump;

    public ReferrersTests(AllLeaksDump shared)
    {
        _session = new SnapshotSession(_host, new JobTracker(TimeProvider.System));
        _dump = _folder.File("leaks.dmp");
        File.Copy(shared.DumpPath, _dump);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Every_rooted_known_answer_item_is_referenced_by_the_type_its_path_shape_names()
    {
        var handle = await _session.StartOpen(_dump).Result;
        var mismatches = new List<string>();
        foreach (var name in LeakCatalog.All.Select(l => l.Name).Concat(["ConditionalWeakTable", "EmbeddedStruct"]))
        {
            var answer = KnownAnswer.Load(name);
            var item = answer.Types[0].Type;
            var expected = ExpectedReferrer(answer.PathShape!, item);
            var items = answer.Kind == "leak" ? AllLeaksDump.Items : AllLeaksDump.SemanticItems;

            var list = await _session.QueryTypeReferrersAsync(handle, item, Module, null, true, null, Ct);
            var rows = await AllRowsAsync(list.Rows);
            await list.Rows.ReleaseAsync(Ct);

            list.Result.MatchedTypes.ShouldBe(1, name);
            list.Result.TargetObjects.ShouldBe(items, name);
            var match = rows.FirstOrDefault(r => DumpAnalysis.StripGenericArguments(r[TypeReferrerColumns.Type]!).Contains(expected, StringComparison.Ordinal));
            if (match is null)
            {
                mismatches.Add($"{name}: no referrer type contains '{expected}' (found {string.Join(", ", rows.Select(r => r[TypeReferrerColumns.Type]))})");
            }
            else if (Long(match[TypeReferrerColumns.Targets]) != items)
            {
                mismatches.Add($"{name}: '{expected}' references {match[TypeReferrerColumns.Targets]} of {items} items");
            }
        }

        mismatches.ShouldBeEmpty();
    }

    [Fact]
    public async Task One_item_lists_its_referrer_with_the_field_that_holds_it()
    {
        var handle = await _session.StartOpen(_dump).Result;
        var instances = await _session.QueryInstancesAsync(handle, "Fixtures.Leaks.UndisposedResource", Module, null, null, true, null, Ct);
        var address = ulong.Parse((await instances.Rows.GetRowsAsync(0, 1, Ct)).Rows[0][0]![2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        await instances.Rows.ReleaseAsync(Ct);

        var referrers = await _session.QueryReferrersAsync(handle, address, null, Ct);
        var rows = await AllRowsAsync(referrers.Rows);

        referrers.Result.FieldsNote.ShouldBeNull();
        rows.ShouldContain(r => r[ReferrerColumns.Type] == "Fixtures.Leaks.UndisposedResource[]" && r[ReferrerColumns.Field]!.StartsWith('['));
        referrers.Result.ReferenceCount.ShouldBe(rows.Sum(r => Long(r[ReferrerColumns.References])));
        referrers.Result.Index!.Bytes.ShouldBe((4L * (handle.Descriptor.ObjectCount + 1)) + (4L * handle.Descriptor.ReferenceCount));
    }

    [Fact]
    public async Task A_mapped_snapshot_maps_its_index_and_answers_the_same()
    {
        var mappedDump = _folder.File("mapped.dmp");
        File.Copy(_dump, mappedDump);
        var inMemory = await _session.StartOpen(_dump, mode: SnapshotMemoryMode.InMemory).Result;
        var mapped = await _session.StartOpen(mappedDump, mode: SnapshotMemoryMode.Mapped).Result;

        var a = await _session.QueryTypeReferrersAsync(inMemory, "Fixtures.Leaks.StaticCacheEntry", Module, null, true, null, Ct);
        var b = await _session.QueryTypeReferrersAsync(mapped, "Fixtures.Leaks.StaticCacheEntry", Module, null, true, null, Ct);

        b.Result.Index!.Mode.ShouldBe(SnapshotMemoryMode.Mapped);
        a.Result.Index!.Mode.ShouldBe(SnapshotMemoryMode.InMemory);
        (await AllRowsAsync(b.Rows)).ShouldBe(await AllRowsAsync(a.Rows));
        File.Exists(mappedDump + ".rev.hsidx").ShouldBeTrue("the sidecar sits next to the index cache");
    }

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        _folder.Dispose();
    }

    /// <summary>The last required step before the one naming the item: the type that holds each item.</summary>
    internal static string ExpectedReferrer(IReadOnlyList<string> shape, string item)
    {
        var at = shape.ToList().FindLastIndex(s => item.EndsWith(s.TrimEnd('?'), StringComparison.Ordinal));
        for (var i = at - 1; i > 0; i--)
        {
            if (!shape[i].EndsWith('?') && shape[i] != "(dependent handle)")
            {
                return shape[i];
            }
        }

        throw new InvalidOperationException($"The path shape has no step before {item}.");
    }

    internal static async Task<List<Dictionary<string, string?>>> AllRowsAsync(ResultSetHandle rows)
    {
        var all = new List<Dictionary<string, string?>>();
        while (all.Count < rows.Info.TotalCount)
        {
            var page = await rows.GetRowsAsync(all.Count, WorkerProtocol.MaxPageSize, Ct);
            all.AddRange(page.Rows.Select(r => rows.Info.Columns.Select((c, i) => (c.Name, Value: r[i])).ToDictionary(p => p.Name, p => p.Value)));
        }

        return all;
    }

    internal static long Long(string? value) => long.Parse(value!, CultureInfo.InvariantCulture);
}

/// <summary>Sprint 2.1: the reverse references sidecar is written once, reused on reopen and rebuilt when stale.</summary>
[Trait("Category", "Integration")]
public sealed class ReverseSidecarTests : IClassFixture<StaticCacheDump>, IAsyncDisposable
{
    private const string Entry = "Fixtures.Leaks.StaticCacheEntry";

    private readonly TempFolder _folder = new();
    private readonly WorkerHost _host = new(Options.Create(new WorkerHostOptions { WorkerPath = TestPaths.WorkerExe }), new ListLoggerProvider());
    private readonly SnapshotSession _session;
    private readonly string _dump;

    public ReverseSidecarTests(StaticCacheDump shared)
    {
        _session = new SnapshotSession(_host, new JobTracker(TimeProvider.System));
        _dump = _folder.File("app.dmp");
        File.Copy(shared.Path, _dump);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private string Sidecar => _dump + ".rev.hsidx";

    [Fact]
    public async Task The_index_is_built_once_written_as_a_sidecar_and_reused_after_a_restart()
    {
        var handle = await _session.StartOpen(_dump).Result;
        handle.Descriptor.CacheIdentity.ShouldNotBe(0UL);

        var first = await _session.QueryTypeReferrersAsync(handle, Entry, "Leaks.dll", null, true, null, Ct);
        var again = await _session.QueryTypeReferrersAsync(handle, Entry, "Leaks.dll", null, true, null, Ct);

        first.Result.Index!.FromSidecar.ShouldBeFalse();
        File.Exists(Sidecar).ShouldBeTrue();
        again.Result.Index!.FromSidecar.ShouldBeFalse("the loaded index is reused, not reopened (sharing is unit tested in DerivedDataTests)");
        first.Result.Index!.Saved.ShouldBeTrue();
        var written = File.GetLastWriteTimeUtc(Sidecar);

        await _host.RestartAsync(Ct);
        var reopened = await _session.QueryTypeReferrersAsync(handle, Entry, "Leaks.dll", null, true, null, Ct);

        reopened.Result.Index!.FromSidecar.ShouldBeTrue();
        reopened.Result.ReferenceCount.ShouldBe(first.Result.ReferenceCount);
        reopened.Result.TargetObjects.ShouldBe(StaticCacheDump.Items);
        File.GetLastWriteTimeUtc(Sidecar).ShouldBe(written);
        (await ReferrersTests.AllRowsAsync(reopened.Rows)).ShouldBe(await ReferrersTests.AllRowsAsync(first.Rows));
    }

    [Fact]
    public async Task An_unknown_type_is_answered_without_building_the_index()
    {
        var handle = await _session.StartOpen(_dump).Result;

        var none = await _session.QueryTypeReferrersAsync(handle, "Fixtures.Leaks.Nothing", "Leaks.dll", null, true, null, Ct);

        none.Result.MatchedTypes.ShouldBe(0);
        none.Result.Index.ShouldBeNull();
        none.Result.CompleteGraph.ShouldBeTrue();
        File.Exists(Sidecar).ShouldBeFalse();
        await Should.ThrowAsync<WorkerOperationException>(() => _session.QueryTypeReferrersAsync(handle, Entry, "Leaks.dll", "Nope", true, null, Ct));
        File.Exists(Sidecar).ShouldBeFalse("an invalid sort column is refused before the index is built");
    }

    [Fact]
    public async Task A_sidecar_of_another_index_cache_is_ignored_and_rebuilt()
    {
        var handle = await _session.StartOpen(_dump).Result;
        using (var writer = IndexCacheFileWriter.Create(Sidecar, cancellationToken: Ct))
        {
            writer.WriteSection<int>(IndexSection.InEdgeStarts, [0]);
            writer.WriteSection<int>(IndexSection.InEdges, []);
            writer.Commit(SnapshotFingerprint.Compute(_dump), baseIdentity: handle.Descriptor.CacheIdentity + 1);
        }

        var result = await _session.QueryTypeReferrersAsync(handle, Entry, "Leaks.dll", null, true, null, Ct);

        result.Result.Index!.FromSidecar.ShouldBeFalse();
        result.Result.TargetObjects.ShouldBe(StaticCacheDump.Items);
        var file = IndexCacheFile.Open(Sidecar, IndexFileLayout.ReverseReferences, handle.Descriptor.CacheIdentity, SnapshotFingerprint.Compute(_dump), MemoryMode.InMemory, Ct);
        file.Success.ShouldBeTrue(file.Detail);
        file.File!.Dispose();
    }

    [Fact]
    public async Task The_cli_lists_an_objects_referrers_and_a_types_referrer_types()
    {
        var (code, output) = await RunCliAsync("referrers", _dump, "--type", Entry, "--format", "json");

        code.ShouldBe(0, output);
        using var json = JsonDocument.Parse(output[output.IndexOf('{', StringComparison.Ordinal)..(output.LastIndexOf('}') + 1)]);
        json.RootElement.GetProperty("TargetObjects").GetInt64().ShouldBe(StaticCacheDump.Items);
        var rows = json.RootElement.GetProperty("Rows").EnumerateArray().ToList();
        rows.ShouldContain(r => DumpAnalysis.StripGenericArguments(r.GetProperty("Type").GetString()!).Contains("+Entry[]", StringComparison.Ordinal));

        var (instancesCode, instances) = await RunCliAsync("instances", _dump, "--type", Entry, "--top", "1", "--format", "csv");
        instancesCode.ShouldBe(0, instances);
        var address = instances.Split('\n').Select(l => l.Trim()).First(l => l.StartsWith("0x", StringComparison.Ordinal)).Split(',')[0];

        var (objectCode, table) = await RunCliAsync("referrers", _dump, "--address", address);

        objectCode.ShouldBe(0, table);
        table.ShouldContain($"Referrers of {address}: 1 objects, 1 references");
        table.ShouldContain("via [");

        var (usage, _) = await RunCliAsync("referrers", _dump);
        usage.ShouldBe(64);

        var (unknown, message) = await RunCliAsync("referrers", _dump, "--type", Entry, "--module", "Other.dll");
        unknown.ShouldBe(1, message);
        message.ShouldContain("there is no type");
    }

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        _folder.Dispose();
    }

    private static async Task<(int Code, string Output)> RunCliAsync(params string[] arguments)
    {
        using var cli = ChildProcess.Start(TestPaths.CliExe, arguments);
        (await cli.WaitForExitAsync(TimeSpan.FromSeconds(180))).ShouldBeTrue(cli.Output);
        return (cli.Process.ExitCode, cli.Output);
    }
}

/// <summary>Sprint 2.1: referrers of a gcdump, which has no field values.</summary>
[Trait("Category", "Integration")]
public sealed class GcDumpReferrersTests(StaticCacheGcDumps shared) : IClassFixture<StaticCacheGcDumps>, IAsyncDisposable
{
    private readonly TempFolder _folder = new();
    private readonly WorkerHost _host = new(Options.Create(new WorkerHostOptions { WorkerPath = TestPaths.WorkerExe }), new ListLoggerProvider());

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_gcdump_answers_referrers_and_explains_the_missing_fields()
    {
        var session = new SnapshotSession(_host, new JobTracker(TimeProvider.System));
        var path = StaticCacheGcDumps.CopyTo(shared.CurrentGcDump, _folder);
        var handle = await session.StartOpen(path).Result;

        var types = await session.QueryTypeReferrersAsync(handle, "Fixtures.Leaks.StaticCacheEntry", "Leaks.dll", null, true, null, Ct);
        types.Result.TargetObjects.ShouldBe(StaticCacheGcDumps.CurrentItems);
        var rows = await ReferrersTests.AllRowsAsync(types.Rows);

        // A gcdump names a nested type without its declaring type or namespace (sprint 1.8): Dictionary+Entry[] is Entry<...>[].
        rows.ShouldContain(r => DumpAnalysis.StripGenericArguments(r[TypeReferrerColumns.Type]!) == "Entry[]", string.Join(", ", rows.Select(r => r[TypeReferrerColumns.Type])));

        var instances = await session.QueryInstancesAsync(handle, "Fixtures.Leaks.StaticCacheEntry", "Leaks.dll", null, null, true, null, Ct);
        var address = ulong.Parse((await instances.Rows.GetRowsAsync(0, 1, Ct)).Rows[0][0]![2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var referrers = await session.QueryReferrersAsync(handle, address, null, Ct);

        referrers.Result.Rows.TotalCount.ShouldBe(1);
        referrers.Result.FieldsNote!.ShouldContain("gcdump");
        (await ReferrersTests.AllRowsAsync(referrers.Rows)).ShouldHaveSingleItem()[ReferrerColumns.Field].ShouldBe(string.Empty);
    }

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        _folder.Dispose();
    }
}
