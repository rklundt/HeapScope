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


using HeapScope.Application.Tests;
using HeapScope.Contracts;
using HeapScope.Core;
using HeapScope.Worker.Rpc;
using Microsoft.Extensions.Logging;
using StreamJsonRpc;

namespace HeapScope.Worker.Tests;

/// <summary>
/// Every answer of the worker leaves one line saying what was asked and what was answered (sprint 2.3.7 review H3, M2),
/// with names and numbers only: field values, string contents and filter text never reach the log (ARCHITECTURE.md 12).
/// </summary>
public sealed class ProvenanceLogTests
{
    private static readonly Guid s_snapshot = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Fact]
    public async Task An_answer_is_logged_with_its_request_and_result()
    {
        var logger = new RecordingLogger();
        var request = new InstancesRequest(s_snapshot, "App.Order", "App.dll", Filter: "secret-token-42", SortColumn: InstanceColumns.Bytes);
        var result = new InstancesResult(new ResultSetInfo(Guid.Empty, 3, []), 10, 400, 1, true, 0.25, FilterNote: "2 strings could not be read.");

        await WorkerErrors.GuardAsync(() => Task.FromResult(result), logger, "QueryInstancesAsync", request);

        var line = logger.Lines.ShouldHaveSingleItem();
        line.ShouldStartWith("QueryInstancesAsync answered in ");
        line.ShouldContain("SnapshotId=11111111-2222-3333-4444-555555555555");
        line.ShouldContain("TypeName=App.Order, Module=App.dll, Filter=(15 chars; 1 terms, 0 excluding, 0 on the module)");
        line.ShouldContain("TotalObjects=10, TotalBytes=400");
        line.ShouldNotContain("secret");
    }

    [Fact]
    public void A_set_is_logged_by_its_shape_with_its_names_and_never_its_addresses()
    {
        // The 3.1 review: a disputed set view traces to which types, namespace or row the set was, never to addresses.
        var types = Provenance.Describe(new InstancesRequest(s_snapshot, string.Empty, null, Set: ObjectSetSpec.OfTypes([new("App.Order", "App.dll"), new("App.Line", "App.dll")])));
        var retained = Provenance.Describe(new RetainedBySetRequest(s_snapshot, string.Empty, null, ObjectSetSpec.RetainedByOf(ObjectSetSpec.OfInspectionRow("event-handler-leaks", 3))));
        var objects = Provenance.Describe(new SetDominatorsRequest(s_snapshot, ObjectSetSpec.OfObjects([0xdead0001, 0xdead0002])));

        types.ShouldContain("Set=ObjectSetSpec {types (2): App.Order [App.dll], App.Line [App.dll]}");
        retained.ShouldContain("Set=ObjectSetSpec {retained by inspection event-handler-leaks row 3}");
        objects.ShouldContain("Set=ObjectSetSpec {objects (2 addresses, #");
        objects.ShouldNotContain("dead", Case.Insensitive);
        types.ShouldNotContain("CacheKey");
    }

    [Fact]
    public void A_specs_texts_are_not_sent_and_every_kind_has_its_own_cache_key()
    {
        // The 3.1 review (ADR-016): the shape and the cache key are computed where they are used, never serialized.
        var json = Newtonsoft.Json.JsonConvert.SerializeObject(ObjectSetSpec.OfObjects([1, 2]));
        json.ShouldNotContain("Shape");
        json.ShouldNotContain("CacheKey");
        json.ShouldNotContain("AddressHash");

        ObjectSetSpec[] specs =
        [
            ObjectSetSpec.OfType("A", null), ObjectSetSpec.OfNamespace("A"), ObjectSetSpec.OfAssembly("A"), ObjectSetSpec.OfObjects([1]),
            ObjectSetSpec.OfInspectionRow("A", 0), ObjectSetSpec.RetainedByOf(ObjectSetSpec.OfNamespace("A")),
            ObjectSetSpec.OfInterface("A"), ObjectSetSpec.RetentionGroupOf(ObjectSetSpec.OfNamespace("A"), new RetentionGroupDto(0, 1, 1, 1, "A", 1, [], 0, "ab")),
            ObjectSetSpec.ComparedOf(ObjectSetSpec.OfNamespace("A"), ObjectChange.New, Guid.NewGuid()), ObjectSetSpec.OfUnreachable(),
        ];
        specs.Select(s => s.Kind).Distinct().Count().ShouldBe(Enum.GetValues<ObjectSetKind>().Length, "every kind is covered here");
        specs.Select(s => s.CacheKey).Distinct().Count().ShouldBe(specs.Length);
        Should.Throw<ArgumentException>(() => new ObjectSetSpec((ObjectSetKind)99).CacheKey);
    }

    [Fact]
    public void The_interface_row_names_mirrored_in_contracts_match_the_analysis()
    {
        // Sprint 3.2 review: front ends name these rows from Contracts, the worker from Analysis.
        (TypeSummaryColumns.NoInterface, TypeSummaryColumns.UnreadInterfaces).ShouldBe((HeapScope.Analysis.TypeGroups.NoInterface, HeapScope.Analysis.TypeGroups.Unread));
        SimilarRetentionLimits.MaxReferrers.ShouldBe(HeapScope.Analysis.Retention.SimilarRetention.MaxReferrers);
    }

    [Fact]
    public void A_filter_is_logged_by_its_shape_never_its_text()
    {
        // 2.7 review: an exclusion or a module term explains a missing row.
        var line = Provenance.Describe(new TypeSummaryRequest(s_snapshot, Filter: "order -secret m:App.dll"));

        line.ShouldContain("Filter=(23 chars; 1 terms, 1 excluding, 1 on the module)");
        line.ShouldNotContain("secret");
        line.ShouldNotContain("order");
    }

    [Fact]
    public void Values_and_texts_read_from_a_dump_are_written_by_their_length_only()
    {
        var details = new ObjectDetails(0x1000, "System.String", 42, HeapKindDto.Gen0, 0, true, [new ObjectFieldDto("_firstChar", "System.Char", ObjectFieldKindDto.Primitive, "p", 0, [])], null, "password123");

        var line = Provenance.Describe(details);

        line.ShouldContain("TypeName=System.String");
        line.ShouldContain("Fields=[1]");
        line.ShouldContain("Text=(11 chars)");
        line.ShouldNotContain("password");
    }

    [Fact]
    public void A_retention_path_names_its_root_and_steps()
    {
        // Sprint 2.3.7 review H3, scenario c: "held by a static field" must be answerable from the log.
        var path = new KeyPathDto(
            new PathRootDto(RootKindDto.StaticField, false, "static field App.Cache.s_items"),
            [new PathStepDto("System.Collections.Generic.Dictionary<System.String, App.Order>", true)],
            "App.Order",
            2000,
            96000,
            0x1000);

        Provenance.Describe(path).ShouldBe("[StaticField: static field App.Cache.s_items] System.Collections.Generic.Dictionary<System.String, App.Order> > App.Order (2000 objects, 96000 bytes)");
        Provenance.Describe(new PathRootDto(RootKindDto.Other, true, "frozen string literal")).ShouldBe("frozen object: frozen string literal");
    }

    [Fact]
    public async Task An_expected_refusal_is_information_without_a_stack()
    {
        var logger = new RecordingLogger();

        await Should.ThrowAsync<LocalRpcException>(() => WorkerErrors.GuardAsync<int>(
            () => throw new CapabilityNotSupportedException(SnapshotCapabilities.StringContents), logger, "RunInspectionAsync", new InspectionRequest(s_snapshot, "duplicate-strings")));

        var (level, line) = logger.Entries.ShouldHaveSingleItem();
        level.ShouldBe(LogLevel.Information);
        line.ShouldStartWith("RunInspectionAsync refused (CapabilityNotSupported): ");
        line.ShouldContain("Name=duplicate-strings");
        line.ShouldNotContain("   at ");
    }

    [Fact]
    public async Task An_error_created_as_a_code_is_logged_too()
    {
        var logger = new RecordingLogger();

        await Should.ThrowAsync<LocalRpcException>(() => WorkerErrors.GuardAsync<int>(
            () => throw WorkerErrors.Create(HeapScopeErrorCode.ResultSetNotFound, "Result set 1 is not available."), logger, "GetRowsAsync"));

        logger.Lines.ShouldHaveSingleItem().ShouldStartWith("GetRowsAsync refused (ResultSetNotFound): Result set 1 is not available.");
    }

    [Fact]
    public async Task An_unexpected_failure_keeps_its_error_level()
    {
        var logger = new RecordingLogger();

        await Should.ThrowAsync<InvalidOperationException>(() => WorkerErrors.GuardAsync<int>(() => throw new InvalidOperationException("broken"), logger, "QueryTypeSummaryAsync"));

        logger.Entries.ShouldHaveSingleItem().Level.ShouldBe(LogLevel.Error);
    }
}
