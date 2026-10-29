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
using HeapScope.Application.Jobs;
using HeapScope.Application.Snapshots;
using HeapScope.Application.Workers;
using HeapScope.Contracts;
using HeapScope.Integration.Tests.Infrastructure;
using Microsoft.Extensions.Options;

namespace HeapScope.Integration.Tests;

/// <summary>Sprint 1.6: the simple inspections through the worker and the session, against the Waste known answer.</summary>
[Trait("Category", "Integration")]
public sealed class InspectionsTests : IClassFixture<AllLeaksDump>, IAsyncDisposable
{
    private readonly TempFolder _folder = new();
    private readonly WorkerHost _host = new(Options.Create(new WorkerHostOptions { WorkerPath = TestPaths.WorkerExe }), new ListLoggerProvider());
    private readonly SnapshotSession _session;
    private readonly string _dump;

    public InspectionsTests(AllLeaksDump shared)
    {
        _session = new SnapshotSession(_host, new JobTracker(TimeProvider.System));
        _dump = _folder.File("waste.dmp");
        File.Copy(shared.DumpPath, _dump);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Every_inspection_is_available_on_a_full_dump_and_reports_the_known_findings()
    {
        var handle = await _session.StartOpen(_dump).Result;
        var inspections = await _session.ListInspectionsAsync(handle, Ct);
        inspections.Select(i => i.Name).ShouldBe(
            [
                "duplicate-strings", "empty-collections", "oversized-collections", "sparse-arrays", "large-arrays", "loh", "finalizable", "fragmentation",
                "growing-static-collections", "event-handler-leaks", "timer-leaks", "disposed-but-referenced", "httpclient-instances", "undisposed-service-scopes",
                "pending-async-methods", "finalizer-backlog", "type-growth",
            ]);
        inspections.ShouldAllBe(i => i.Unavailable == null);
        inspections.Where(i => i.Kind == InspectionKinds.Leak).Select(i => i.Name).ShouldBe(inspections.Skip(8).Select(i => i.Name), "the 2.6 detectors are the leak kind");

        var runs = new Dictionary<string, (InspectionResultDto Result, List<Dictionary<string, string?>> Rows)>();
        var mismatches = new List<string>();
        foreach (var finding in KnownAnswer.Load("Waste").Findings!)
        {
            if (!runs.TryGetValue(finding.Inspection, out var run))
            {
                var ran = await _session.RunInspectionAsync(handle, finding.Inspection, null, Ct);
                run = runs[finding.Inspection] = (ran.Result, await AllRowsAsync(ran.Rows));
            }

            var actual = finding.Inspection switch
            {
                "duplicate-strings" => run.Rows.Where(r => r["Value"] == finding.Key).Sum(r => Long(r["Copies"])),
                "loh" or "finalizable" => run.Rows.Where(r => r["Type"] == finding.Key).Sum(r => Long(r["Objects"])),
                "fragmentation" => run.Result.Count,
                _ => run.Rows.Count(r => r["Type"] == finding.Key),
            };
            if (finding.Exact ? actual != finding.Count : actual < finding.Count)
            {
                mismatches.Add($"{finding.Inspection} {finding.Key}: {actual}, expected {(finding.Exact ? string.Empty : "at least ")}{finding.Count}");
            }
        }

        mismatches.ShouldBeEmpty();

        // Wasted bytes add up from the rows, and the fixture's right-sized and dense objects are not reported.
        var duplicates = runs["duplicate-strings"];
        duplicates.Result.WastedBytes.ShouldBe(duplicates.Rows.Sum(r => Long(r["Wasted"])));
        var waste = duplicates.Rows.Single(r => r["Value"] == "HeapScope waste duplicate A");
        Long(waste["Wasted"]).ShouldBe(49 * Long(waste["Bytes"]));
        runs["sparse-arrays"].Rows.Count(r => r["Type"] == "Fixtures.Leaks.WasteNode[]").ShouldBe(4, "the dense WasteNode[] is not sparse");
        var fragmentation = runs["fragmentation"];
        fragmentation.Result.WastedBytes.ShouldNotBeNull("a dump loaded by 1.6 knows its free space");
        fragmentation.Rows.Where(r => r["Heap"] is "Gen0" or "Gen1" or "Gen2").Sum(r => Long(r["PinnedObjects"])).ShouldBeGreaterThanOrEqualTo(1, "the handle-pinned buffer");
        Long(fragmentation.Rows.Single(r => r["Heap"] == "PinnedObjectHeap")["PinnedBytes"]).ShouldBeGreaterThanOrEqualTo(1_000, "the pinned object heap buffer");
        fragmentation.Rows.ShouldAllBe(r => r["Free"] != null);
    }

    [Fact]
    public async Task Inspection_rows_open_the_objects_they_count()
    {
        var handle = await _session.StartOpen(_dump).Result;

        var duplicates = await AllRowsAsync((await _session.RunInspectionAsync(handle, "duplicate-strings", null, Ct)).Rows);
        var row = duplicates.Single(r => r["Value"] == "HeapScope waste duplicate B");
        var same = await _session.QueryInstancesAsync(handle, Query(row), null, Ct);
        same.Result.TotalObjects.ShouldBe(30);

        var loh = await AllRowsAsync((await _session.RunInspectionAsync(handle, "loh", null, Ct)).Rows);
        var lohRow = loh.Single(r => r["Type"] == "Fixtures.Leaks.WasteRecord[]");
        (await _session.QueryInstancesAsync(handle, Query(lohRow), null, Ct)).Result.TotalObjects.ShouldBe(3);

        var finalizable = await AllRowsAsync((await _session.RunInspectionAsync(handle, "finalizable", null, Ct)).Rows);
        var finalizableRow = finalizable.Single(r => r["Type"] == "Fixtures.Leaks.WasteFinalizable");
        (await _session.QueryInstancesAsync(handle, Query(finalizableRow), null, Ct)).Result.TotalObjects.ShouldBe(25, "suppressed ones are not finalizable");
    }

    [Fact]
    public async Task Inspection_rows_survive_a_worker_restart_and_unknown_names_are_refused()
    {
        var handle = await _session.StartOpen(_dump).Result;
        var run = await _session.RunInspectionAsync(handle, "large-arrays", null, Ct);
        var first = (await run.Rows.GetRowsAsync(0, 10, Ct)).Rows.Select(r => r[0]).ToList();

        await _host.RestartAsync(Ct);

        (await run.Rows.GetRowsAsync(0, 10, Ct)).Rows.Select(r => r[0]).ShouldBe(first);
        run.Rows.Requeries.ShouldBe(1);
        var unknown = await Should.ThrowAsync<WorkerOperationException>(() => _session.RunInspectionAsync(handle, "no-such-inspection", null, Ct));
        unknown.ErrorCode.ShouldBe(HeapScopeErrorCode.InvalidArgument);
        unknown.Message.ShouldContain("duplicate-strings");
    }

    [Fact]
    public async Task Cli_inspect_prints_a_summary_one_inspection_in_every_format_and_exit_codes()
    {
        var summary = await RunCliAsync("inspect", _dump);
        summary.Code.ShouldBe(0, summary.Output);
        foreach (var name in new[] { "duplicate-strings", "empty-collections", "oversized-collections", "sparse-arrays", "large-arrays", "loh", "finalizable", "fragmentation" })
        {
            summary.Output.ShouldContain(name);
        }

        var table = await RunCliAsync("inspect", _dump, "duplicate-strings", "--top", "500");
        table.Code.ShouldBe(0, table.Output);
        table.Output.ShouldContain("Cause: ");
        table.Output.ShouldMatch(@"HeapScope waste duplicate A\s+50\s");
        table.Output.ShouldNotContain("SameValue", customMessage: "hidden columns stay hidden in the table");

        var json = await RunCliAsync("inspect", _dump, "finalizable", "--all", "--format", "json");
        json.Code.ShouldBe(0, json.Output);
        json.Output.ShouldContain("\"Type\": \"Fixtures.Leaks.WasteFinalizable\"");

        var csv = await RunCliAsync("inspect", _dump, "loh", "--format", "csv");
        csv.Code.ShouldBe(0, csv.Output);
        csv.Output.ShouldContain("Type,Objects,Bytes,TypeName,Module,Scope");

        var unknown = await RunCliAsync("inspect", _dump, "no-such");
        unknown.Code.ShouldBe(1, unknown.Output);
        unknown.Output.ShouldContain("there is no inspection no-such");
        (await RunCliAsync("inspect", _dump, "loh", "--top", "0")).Code.ShouldBe(64);
        (await RunCliAsync("inspect", _dump, "loh", "--top", "3", "--all")).Code.ShouldBe(64);
        (await RunCliAsync("inspect", _dump + ".missing.dmp")).Code.ShouldBe(1);
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

    /// <summary>The instances query a row opens: its hidden type, module and scope columns, and its address for equal strings.</summary>
    private static InstancesQuery Query(Dictionary<string, string?> row)
    {
        var scope = Enum.Parse<InstanceScope>(row[InspectionColumns.Scope]!);
        var sameAs = scope == InstanceScope.SameValue ? ulong.Parse(row[InspectionColumns.Address]![2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture) : 0;
        return new InstancesQuery(row[InspectionColumns.TypeName]!, row[InspectionColumns.Module], Scope: scope, SameValueAs: sameAs);
    }

    private static long Long(string? cell) => long.Parse(cell!, CultureInfo.InvariantCulture);

    private static async Task<List<Dictionary<string, string?>>> AllRowsAsync(ResultSetHandle rows)
    {
        var columns = rows.Info.Columns.Select(c => c.Name).ToList();
        var all = new List<Dictionary<string, string?>>();
        while (all.Count < rows.Info.TotalCount)
        {
            var page = await rows.GetRowsAsync(all.Count, WorkerProtocol.MaxPageSize, Ct);
            if (page.Rows.Count == 0)
            {
                break;
            }

            all.AddRange(page.Rows.Select(r => columns.Select((c, i) => (c, r[i])).ToDictionary(x => x.c, x => x.Item2, StringComparer.Ordinal)));
        }

        return all;
    }
}
