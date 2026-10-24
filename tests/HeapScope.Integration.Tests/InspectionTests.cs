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

/// <summary>Sprint 1.5: instances, object details and references through the worker and the session.</summary>
[Trait("Category", "Integration")]
public sealed class InspectionTests : IClassFixture<AllLeaksDump>, IAsyncDisposable
{
    private const string SampleType = "Fixtures.Leaks.InspectionSample";

    private readonly TempFolder _folder = new();
    private readonly WorkerHost _host = new(Options.Create(new WorkerHostOptions { WorkerPath = TestPaths.WorkerExe }), new ListLoggerProvider());
    private readonly SnapshotSession _session;
    private readonly string _dump;

    public InspectionTests(AllLeaksDump shared)
    {
        _session = new SnapshotSession(_host, new JobTracker(TimeProvider.System));
        _dump = _folder.File("inspection.dmp");
        File.Copy(shared.DumpPath, _dump);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_summary_row_opens_its_instances_and_an_instance_shows_the_known_field_values()
    {
        var handle = await _session.StartOpen(_dump).Result;
        handle.Descriptor.Features.HasFlag(SnapshotFeatures.FieldValues).ShouldBeTrue();
        handle.Descriptor.Features.HasFlag(SnapshotFeatures.StringContents).ShouldBeTrue();

        // The hidden key columns of the type row open its instances.
        var summary = await _session.QueryTypeSummaryAsync(handle, TypeGrouping.Type, "InspectionSample", null, true, null, Ct);
        var columns = summary.Rows.Info.Columns.Select(c => c.Name).ToList();
        var row = (await summary.Rows.GetRowsAsync(0, 10, Ct)).Rows.Single(r => r[0] == SampleType);
        var typeName = row[columns.IndexOf(TypeSummaryColumns.TypeName)]!;
        var module = row[columns.IndexOf(TypeSummaryColumns.Module)];
        (typeName, module).ShouldBe((SampleType, "Leaks.dll"));

        var instances = await _session.QueryInstancesAsync(handle, typeName, module, null, null, true, null, Ct);
        instances.Result.TotalObjects.ShouldBe(1);
        var address = Address((await instances.Rows.GetRowsAsync(0, 1, Ct)).Rows[0][0]!);

        var details = await _session.GetObjectAsync(handle, address, null, Ct);
        details.TypeName.ShouldBe(SampleType);
        details.IsRooted.ShouldBeTrue("its static field is a root (RootKind.StaticField)");
        details.FieldsUnavailable.ShouldBeNull();
        var fields = Flatten(details.Fields!);
        var mismatches = new List<string>();
        foreach (var expected in KnownAnswer.Load("Inspection").Fields!)
        {
            var ok = fields.TryGetValue(expected.Path, out var actual)
                && (expected.Value is null || actual.Value == expected.Value)
                && (expected.ValuePrefix is null || actual.Value.StartsWith(expected.ValuePrefix, StringComparison.Ordinal))
                && (expected.Length is null || actual.Value.Length == expected.Length)
                && (expected.Type is null || (actual.Kind == ObjectFieldKindDto.Reference && actual.Value == expected.Type));
            if (!ok)
            {
                mismatches.Add($"{expected.Path}: {(actual is null ? "missing" : actual.Value)}");
            }
        }

        mismatches.ShouldBeEmpty();
        fields["Box.TopLeft"].Kind.ShouldBe(ObjectFieldKindDto.Struct);
        fields["Box.TopLeft"].Children.Select(c => c.Name).ShouldBe(["X", "Y"], "nested struct fields are nested rows");

        // Outgoing references, named by the field that holds them.
        var references = await _session.QueryReferencesAsync(handle, address, null, Ct);
        var referenceRows = (await references.GetRowsAsync(0, 100, Ct)).Rows;
        referenceRows.Count.ShouldBe(details.ReferenceCount);
        referenceRows.Select(r => r[0]).ShouldContain("Child");
        referenceRows.Select(r => r[0]).ShouldContain("Box.Label");
        referenceRows.Single(r => r[0] == "Child")[2].ShouldBe("Fixtures.Leaks.InspectionChild");
        Address(referenceRows.Single(r => r[0] == "Child")[1]!).ShouldBe(fields["Child"].Address);

        // Slots sharing one object name the first few and count the rest; struct elements name their field.
        var repeated = (await (await _session.QueryReferencesAsync(handle, fields["Repeated"].Address, null, Ct)).GetRowsAsync(0, 100, Ct)).Rows;
        repeated.Select(r => r[0]).Distinct().ShouldBe(["[0], [1], [2] and 7 more"]);
        var pairs = (await (await _session.QueryReferencesAsync(handle, fields["Pairs"].Address, null, Ct)).GetRowsAsync(0, 100, Ct)).Rows;
        pairs.Select(r => r[0]).ShouldBe(["[0].Value", "[1].Value"], ignoreOrder: true);
    }

    [Fact]
    public async Task String_instances_filter_by_text_regex_and_address_and_show_their_value()
    {
        var handle = await _session.StartOpen(_dump).Result;
        const string Module = "System.Private.CoreLib.dll";

        var text = await _session.QueryInstancesAsync(handle, "System.String", Module, "heapscope INSPECTION", null, true, null, Ct);
        var row = (await text.Rows.GetRowsAsync(0, 10, Ct)).Rows.ShouldHaveSingleItem();
        row[4].ShouldBe("HeapScope inspection");
        text.Result.ValuesAvailable.ShouldBeTrue();
        text.Result.TotalObjects.ShouldBeGreaterThan(1000, "totals cover every string, not only the matches");

        var regex = await _session.QueryInstancesAsync(handle, "System.String", Module, "/^HeapScope insp.*n$/", null, true, null, Ct);
        regex.Result.Rows.TotalCount.ShouldBe(1);

        var byAddress = await _session.QueryInstancesAsync(handle, "System.String", Module, row[0], null, true, null, Ct);
        (await byAddress.Rows.GetRowsAsync(0, 10, Ct)).Rows.ShouldContain(r => r[0] == row[0]);

        var bad = await Should.ThrowAsync<WorkerOperationException>(() => _session.QueryInstancesAsync(handle, "System.String", Module, "/(/", null, true, null, Ct));
        bad.ErrorCode.ShouldBe(HeapScopeErrorCode.InvalidArgument);
        var missing = await Should.ThrowAsync<WorkerOperationException>(() => _session.GetObjectAsync(handle, 0x10, null, Ct));
        missing.ErrorCode.ShouldBe(HeapScopeErrorCode.InvalidArgument);
        missing.Message.ShouldContain("no object at 0x10");
    }

    [Fact]
    public async Task Value_sorts_order_strings_by_text_and_arrays_by_length()
    {
        var handle = await _session.StartOpen(_dump).Result;
        const string Module = "System.Private.CoreLib.dll";

        var texts = await _session.QueryInstancesAsync(handle, "System.String", Module, "inspection", InstanceColumns.Value, false, null, Ct);
        var values = (await texts.Rows.GetRowsAsync(0, 100, Ct)).Rows.Select(r => r[4]).ToList();
        values.Count.ShouldBeGreaterThan(0);
        values.ShouldBe(values.Order(StringComparer.Ordinal).ToList());

        // byte[] lengths sort as numbers ([1,000] after [999]), largest first.
        var arrays = await _session.QueryInstancesAsync(handle, "System.Byte[]", Module, null, InstanceColumns.Value, true, null, Ct);
        var lengths = (await arrays.Rows.GetRowsAsync(0, 200, Ct)).Rows
            .Select(r => long.Parse(r[4]!.Trim('[', ']'), NumberStyles.AllowThousands, CultureInfo.InvariantCulture))
            .ToList();
        lengths.ShouldBe(lengths.OrderDescending().ToList());

        // A sort by value that would hold every string's text is refused with a next step.
        var all = await _session.QueryInstancesAsync(handle, "System.String", Module, null, null, true, null, Ct);
        if (all.Result.TotalObjects > InstancesSourceLimits.MaxValueSort)
        {
            var refused = await Should.ThrowAsync<WorkerOperationException>(() => _session.QueryInstancesAsync(handle, "System.String", Module, null, InstanceColumns.Value, true, null, Ct));
            refused.ErrorCode.ShouldBe(HeapScopeErrorCode.InvalidArgument);
            refused.Message.ShouldContain("filter them");
        }
    }

    [Fact]
    public async Task Instances_and_references_survive_a_worker_restart()
    {
        var handle = await _session.StartOpen(_dump).Result;
        var instances = await _session.QueryInstancesAsync(handle, SampleType, "Leaks.dll", null, null, true, null, Ct);
        var address = Address((await instances.Rows.GetRowsAsync(0, 1, Ct)).Rows[0][0]!);
        var references = await _session.QueryReferencesAsync(handle, address, null, Ct);

        await _host.RestartAsync(Ct);

        (await instances.Rows.GetRowsAsync(0, 1, Ct)).Rows[0][0].ShouldBe(string.Create(CultureInfo.InvariantCulture, $"0x{address:x}"));
        (await references.GetRowsAsync(0, 100, Ct)).Rows.Select(r => r[0]).ShouldContain("Child");
        (instances.Rows.Requeries, references.Requeries).ShouldBe((1, 1));
        (await _session.GetObjectAsync(handle, address, null, Ct)).Fields.ShouldNotBeNull("the reopened snapshot reads its dump again");
    }

    [Fact]
    public async Task Cli_instances_finds_the_module_filters_and_prints_formats()
    {
        var table = await RunCliAsync("instances", _dump, "--type", SampleType);
        table.Code.ShouldBe(0, table.Output);
        table.Output.ShouldContain($"{SampleType} (Leaks.dll): 1 objects");
        table.Output.ShouldMatch(@"0x[0-9a-f]+\s+[0-9,]+\s+\w+");

        var json = await RunCliAsync("instances", _dump, "--type", "System.String", "--module", "System.Private.CoreLib.dll", "--filter", "heapscope INSPECTION", "--format", "json");
        json.Code.ShouldBe(0, json.Output);
        json.Output.ShouldContain("\"MatchingObjects\": 1");
        json.Output.ShouldContain("\"Value\": \"HeapScope inspection\"");

        var csv = await RunCliAsync("instances", _dump, "--type", "System.String", "--sort", "address", "--top", "3", "--format", "csv");
        csv.Code.ShouldBe(0, csv.Output);
        csv.Output.ShouldContain("Address,Type,Bytes,Generation,Value");
    }

    [Fact]
    public async Task Cli_object_prints_fields_and_references_and_json()
    {
        var address = await SampleAddressAsync();

        var text = await RunCliAsync("object", _dump, address);
        text.Code.ShouldBe(0, text.Output);
        text.Output.ShouldContain($"Type:        {SampleType}");
        text.Output.ShouldContain("Rooted:      yes");
        text.Output.ShouldContain("Fields (");
        text.Output.ShouldContain("    X ", customMessage: "nested struct fields are indented under their struct");
        text.Output.ShouldMatch(@"References \(\d+, showing \d+\):");
        text.Output.ShouldContain("Fixtures.Leaks.InspectionChild  via Child");

        var none = await RunCliAsync("object", _dump, address[2..], "--references", "0");
        none.Code.ShouldBe(0, none.Output);
        none.Output.ShouldMatch(@"References \(\d+, showing 0\):");

        var json = await RunCliAsync("object", _dump, address, "--format", "json");
        json.Code.ShouldBe(0, json.Output);
        json.Output.ShouldContain($"\"TypeName\": \"{SampleType}\"");
        json.Output.ShouldContain("\"Children\": [");
        json.Output.ShouldContain("\"Field\": \"Child\"");
    }

    [Fact]
    public async Task Cli_inspection_reports_errors_and_usage_with_their_own_exit_codes()
    {
        var noObject = await RunCliAsync("object", _dump, "0x10");
        noObject.Code.ShouldBe(1, noObject.Output);
        noObject.Output.ShouldContain("no object at 0x10");

        var noType = await RunCliAsync("instances", _dump, "--type", "Fixtures.Leaks.NoSuchType");
        noType.Code.ShouldBe(1, noType.Output);
        noType.Output.ShouldContain("there is no type Fixtures.Leaks.NoSuchType");
        (await RunCliAsync("instances", _dump, "--type", SampleType, "--module", "Other.dll")).Code.ShouldBe(1);
        (await RunCliAsync("instances", _dump + ".missing.dmp", "--type", SampleType)).Code.ShouldBe(1);

        (await RunCliAsync("object", _dump, "not-hex")).Code.ShouldBe(64);
        (await RunCliAsync("object", _dump)).Code.ShouldBe(64);
        (await RunCliAsync("object", _dump, "0x10", "--references", "-1")).Code.ShouldBe(64);
        (await RunCliAsync("instances", _dump)).Code.ShouldBe(64);
        (await RunCliAsync("instances", _dump, "--type", SampleType, "--sort", "size")).Code.ShouldBe(64);
        (await RunCliAsync("instances", _dump, "--type", SampleType, "--top", "3", "--all")).Code.ShouldBe(64);
    }

    [Fact]
    public async Task A_dump_changed_after_the_snapshot_opened_is_not_read_for_field_values()
    {
        var handle = await _session.StartOpen(_dump).Result;
        var instances = await _session.QueryInstancesAsync(handle, SampleType, "Leaks.dll", null, null, true, null, Ct);
        var address = Address((await instances.Rows.GetRowsAsync(0, 1, Ct)).Rows[0][0]!);

        // Nothing has read the dump yet; now it is replaced (here: touched) under the open snapshot.
        File.SetLastWriteTimeUtc(_dump, DateTime.UtcNow.AddMinutes(5));

        var ex = await Should.ThrowAsync<WorkerOperationException>(() => _session.GetObjectAsync(handle, address, null, Ct));
        ex.ErrorCode.ShouldBe(HeapScopeErrorCode.SnapshotChanged);
        ex.Message.ShouldContain("changed since this snapshot was opened");
    }

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        _folder.Dispose();
    }

    /// <summary>The worker's limit, repeated here: the worker assembly is not referenced by integration tests.</summary>
    private static class InstancesSourceLimits
    {
        public const int MaxValueSort = 250_000;
    }

    private async Task<string> SampleAddressAsync()
    {
        var handle = await _session.StartOpen(_dump).Result;
        var instances = await _session.QueryInstancesAsync(handle, SampleType, "Leaks.dll", null, null, true, null, Ct);
        return (await instances.Rows.GetRowsAsync(0, 1, Ct)).Rows[0][0]!;
    }

    private static async Task<(int Code, string Output)> RunCliAsync(params string[] arguments)
    {
        using var cli = ChildProcess.Start(TestPaths.CliExe, arguments);
        (await cli.WaitForExitAsync(TimeSpan.FromSeconds(180))).ShouldBeTrue(cli.Output);
        return (cli.Process.ExitCode, cli.Output);
    }

    private static ulong Address(string text) => ulong.Parse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    private static Dictionary<string, ObjectFieldDto> Flatten(IReadOnlyList<ObjectFieldDto> fields, string prefix = "")
    {
        var all = new Dictionary<string, ObjectFieldDto>(StringComparer.Ordinal);
        foreach (var field in fields)
        {
            var path = field.Name.StartsWith('[') || prefix.Length == 0 ? prefix + field.Name : prefix + "." + field.Name;
            all[path] = field;
            foreach (var (childPath, child) in Flatten(field.Children, path))
            {
                all[childPath] = child;
            }
        }

        return all;
    }
}
