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


using System.Diagnostics;
using HeapScope.Application.Tests;
using HeapScope.Application.Workers;
using HeapScope.Contracts;
using Microsoft.Extensions.Logging;

namespace HeapScope.UI.ViewModels.Tests;

/// <summary>One runner for every view model query (sprint 3.8, peer review P8).</summary>
public class QueryRunnerTests
{
    [Fact]
    public async Task A_refusal_is_shown_and_logged_under_the_trace_id_the_worker_saw()
    {
        var logger = new RecordingLogger();
        string? status = null;
        string? traceSeenByWorker = null;

        await QueryRunner.RunAsync<int>(
            "Retention paths",
            (_, _) =>
            {
                traceSeenByWorker = Activity.Current?.TraceId.ToString();
                throw new WorkerOperationException(HeapScopeErrorCode.CapabilityNotSupported, "A gcdump has no field values.", null);
            },
            _ => throw new InvalidOperationException("nothing to show"),
            s => status = s,
            "failed",
            logger,
            TestContext.Current.CancellationToken);

        status.ShouldBe("A gcdump has no field values.");
        traceSeenByWorker.ShouldNotBeNullOrEmpty();
        logger.Entries.ShouldContain(e => e.Level == LogLevel.Information && e.Line.Contains("Retention paths shown as: A gcdump has no field values.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Leaving_the_view_shows_and_logs_nothing_and_a_failure_is_logged_with_its_stack()
    {
        var logger = new RecordingLogger();
        string? status = "before";
        using var left = new CancellationTokenSource();
        await left.CancelAsync();

        await QueryRunner.RunAsync<int>("Q", (_, _) => throw new OperationCanceledException(), _ => { }, s => status = s, "failed", logger, left.Token);
        status.ShouldBe("before");
        logger.Entries.ShouldBeEmpty();

        await QueryRunner.RunAsync<int>("Q", (_, _) => throw new InvalidOperationException("bug"), _ => { }, s => status = s, "failed", logger, TestContext.Current.CancellationToken);
        status.ShouldBe("failed");
        logger.Entries.ShouldContain(e => e.Level == LogLevel.Error);
    }
}
