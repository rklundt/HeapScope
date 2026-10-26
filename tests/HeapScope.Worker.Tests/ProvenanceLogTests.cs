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
        line.ShouldContain("TypeName=App.Order, Module=App.dll, Filter=(15 chars)");
        line.ShouldContain("TotalObjects=10, TotalBytes=400");
        line.ShouldNotContain("secret");
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
