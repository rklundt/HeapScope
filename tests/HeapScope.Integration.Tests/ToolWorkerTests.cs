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

using HeapScope.Application.Workers;
using HeapScope.Capture;
using HeapScope.Contracts;
using HeapScope.Integration.Tests.Infrastructure;
using Microsoft.Extensions.Options;

namespace HeapScope.Integration.Tests;

/// <summary>
/// The worker as the CLI's dotnet tool starts it (ADR-039; 3.9 review M2): its assembly through the dotnet host that runs
/// this process. It keeps Server GC, its start line names the host, and process discovery leaves it out as HeapScope's
/// own process though its image is the dotnet host.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ToolWorkerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_worker_assembly_runs_through_the_dotnet_host_with_server_gc_and_stays_out_of_the_process_list()
    {
        var assembly = Path.ChangeExtension(TestPaths.WorkerExe, ".dll");
        var host = WorkerHostOptions.RunningDotnetHost();
        host.ShouldNotBeNull("the tests run on an installed runtime, as the tool does");
        var logs = new ListLoggerProvider();
        await using var worker = new WorkerHost(Options.Create(new WorkerHostOptions { WorkerPath = assembly }), logs);

        var info = await worker.EnsureStartedAsync(Ct);

        info.ProtocolVersion.ShouldBe(WorkerProtocol.Version);
        info.ServerGc.ShouldBeTrue("the worker's runtimeconfig turns Server GC on whatever starts it");
        logs.Records.ShouldContain(r => r.Message.Contains("Started worker process", StringComparison.Ordinal)
            && r.Message.Contains(assembly, StringComparison.OrdinalIgnoreCase)
            && r.Message.Contains(host, StringComparison.OrdinalIgnoreCase));
        new ProcessDiscovery().List(Ct).ShouldNotContain(p => p.ProcessId == info.ProcessId, "HeapScope's own worker is never listed");
    }
}
