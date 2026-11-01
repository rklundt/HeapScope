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

namespace HeapScope.Capture.Tests;

/// <summary>Sprint 3.9 (ADR-039): HeapScope's worker started through the dotnet host is still one of its own processes.</summary>
public sealed class ProcessDiscoveryTests
{
    [Fact]
    public void A_dotnet_host_running_this_installations_worker_assembly_is_own()
    {
        var own = Path.Combine(Path.GetTempPath(), "tools", "heapscope.cli");
        var worker = Path.Combine(own, "worker", "HeapScope.Worker.dll");

        ProcessDiscovery.RunsOwnWorkerAssembly($"\"dotnet\" \"{worker}\" --pipe HeapScope.W.1", own).ShouldBeTrue();
        ProcessDiscovery.RunsOwnWorkerAssembly($"dotnet {worker.ToUpperInvariant()} --pipe x", own).ShouldBeTrue();
        ProcessDiscovery.RunsOwnWorkerAssembly("dotnet MyApp.dll", own).ShouldBeFalse();
        ProcessDiscovery.RunsOwnWorkerAssembly($"dotnet {Path.Combine(Path.GetTempPath(), "other", "worker", "HeapScope.Worker.dll")}", own)
            .ShouldBeFalse("another installation's worker is listed like any process");
        ProcessDiscovery.RunsOwnWorkerAssembly(null, own).ShouldBeFalse();
    }
}
