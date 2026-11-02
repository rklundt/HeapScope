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

namespace HeapScope.Application.Tests;

/// <summary>The worker's GC heap count leaves cores for the UI (sprint 1.3).</summary>
public sealed class WorkerHostOptionsTests
{
    [Fact]
    public void The_worker_gets_half_the_logical_processors_as_gc_heaps_unless_configured()
    {
        new WorkerHostOptions().ResolveGcHeapCount().ShouldBe(Math.Max(1, Environment.ProcessorCount / 2));
        new WorkerHostOptions { GcHeapCount = 3 }.ResolveGcHeapCount().ShouldBe(3);
        new WorkerHostOptions { GcHeapCount = 0 }.ResolveGcHeapCount().ShouldBe(1);
    }

    [Fact]
    public void The_worker_is_found_by_this_systems_executable_name_unless_configured()
    {
        // Sprint 2.3.5: .NET's application host has no extension off Windows.
        var expected = OperatingSystem.IsWindows() ? "HeapScope.Worker.exe" : "HeapScope.Worker";

        WorkerHostOptions.WorkerExecutableName.ShouldBe(expected);
        new WorkerHostOptions().ResolveWorkerPath().ShouldBe(Path.Combine(AppContext.BaseDirectory, "worker", expected));
        new WorkerHostOptions { WorkerPath = "/opt/heapscope/w" }.ResolveWorkerPath().ShouldBe("/opt/heapscope/w");
    }

    [Fact]
    public void The_worker_executable_starts_on_its_own_and_its_assembly_through_the_dotnet_host()
    {
        // ADR-039: the CLI's dotnet tool ships the worker's assembly without an executable.
        var root = Path.Combine(Path.GetTempPath(), "hs-tool");
        var executable = Path.Combine(root, "worker", WorkerHostOptions.WorkerExecutableName);
        var assembly = Path.Combine(root, "worker", "HeapScope.Worker.dll");
        const string Host = "/usr/share/dotnet/dotnet";
        var options = new WorkerHostOptions();

        options.ResolveWorkerLaunch(root, Host, true, f => f == executable || f == assembly).ShouldBe(new WorkerLaunch(executable, null));
        options.ResolveWorkerLaunch(root, null, true, f => f == executable).ShouldBe(new WorkerLaunch(executable, null));
        options.ResolveWorkerLaunch(root, Host, true, f => f == assembly).ShouldBe(new WorkerLaunch(Host, assembly));
        options.ResolveWorkerLaunch(root, Host, true, f => f == assembly).WorkerFile.ShouldBe(assembly);
    }

    [Fact]
    public void A_worker_that_cannot_start_says_which_case_it_is_and_what_to_do()
    {
        // 3.9 review M1 and M3: each refusal names the files or the host and a next step.
        var root = Path.Combine(Path.GetTempPath(), "hs-tool");
        var folder = Path.Combine(root, "worker");
        var assembly = Path.Combine(folder, "HeapScope.Worker.dll");
        const string Host = "/usr/share/dotnet/dotnet";
        var options = new WorkerHostOptions();

        var none = Should.Throw<WorkerStartException>(() => options.ResolveWorkerLaunch(root, Host, true, _ => false));
        none.Message.ShouldContain($"No analysis worker in {folder}");
        none.Message.ShouldContain(WorkerHostOptions.WorkerExecutableName);
        none.Message.ShouldContain("HeapScope.Worker.dll");
        none.Message.ShouldContain("dotnet tool uninstall -g HeapScope.Cli");

        var noHost = Should.Throw<WorkerStartException>(() => options.ResolveWorkerLaunch(root, null, true, f => f == assembly));
        noHost.Message.ShouldContain(assembly);
        noHost.Message.ShouldContain("needs a dotnet host");
        noHost.Message.ShouldContain("Install the .NET 10 runtime");

        var x86 = Should.Throw<WorkerStartException>(() => options.ResolveWorkerLaunch(root, Host, false, f => f == assembly));
        x86.Message.ShouldContain("needs a 64-bit .NET");
        x86.Message.ShouldContain(Host);
    }

    [Fact]
    public void A_configured_worker_path_is_an_executable_unless_it_names_an_assembly()
    {
        const string Host = "/usr/share/dotnet/dotnet";

        new WorkerHostOptions { WorkerPath = "/opt/w/HeapScope.Worker" }.ResolveWorkerLaunch("/ignored", Host, true, _ => true)
            .ShouldBe(new WorkerLaunch("/opt/w/HeapScope.Worker", null));
        new WorkerHostOptions { WorkerPath = "/opt/w/HeapScope.Worker.dll" }.ResolveWorkerLaunch("/ignored", Host, true, _ => true)
            .ShouldBe(new WorkerLaunch(Host, "/opt/w/HeapScope.Worker.dll"));
        Should.Throw<WorkerStartException>(() => new WorkerHostOptions { WorkerPath = "/opt/w/HeapScope.Worker" }.ResolveWorkerLaunch("/ignored", Host, true, _ => false))
            .Message.ShouldContain("Worker:WorkerPath was not found: /opt/w/HeapScope.Worker");
        Should.Throw<WorkerStartException>(() => new WorkerHostOptions { WorkerPath = "/opt/w/HeapScope.Worker.dll" }.ResolveWorkerLaunch("/ignored", null, true, _ => true))
            .Message.ShouldContain("needs a dotnet host");
    }

    [Fact]
    public void A_test_process_runs_on_an_installed_runtime_and_finds_its_dotnet_host()
    {
        // The tests run framework-dependent, as the dotnet tool does.
        var host = WorkerHostOptions.RunningDotnetHost();

        host.ShouldNotBeNull();
        Path.GetFileNameWithoutExtension(host).ShouldBe("dotnet");
        File.Exists(host).ShouldBeTrue();
    }

    [Fact]
    public void Pipe_names_are_unique_and_fit_a_macos_socket_path()
    {
        // Sprint 2.3.5: off Windows the pipe is a socket at <temp>/CoreFxPipe_<name>, at most 104 characters on macOS,
        // whose temp folder is about 49.
        const string MacTemp = "/var/folders/n6/hn37q6pj7sj_9dmqqc2113800000gn/T/";
        var first = WorkerHost.NewPipeName();

        first.ShouldStartWith("HeapScope.W.");
        (MacTemp + "CoreFxPipe_" + first).Length.ShouldBeLessThan(104, "104 bytes hold the path and its terminating NUL");
        WorkerHost.NewPipeName().ShouldNotBe(first);
    }

    [Fact]
    public void An_attach_pipe_name_too_long_for_a_socket_path_is_refused_with_its_length()
    {
        // 2.3.5 review L16: --pipe names a worker to attach to; off Windows it must fit a socket path.
        const string MacTemp = "/var/folders/n6/hn37q6pj7sj_9dmqqc2113800000gn/T/";
        var tooLong = new string('w', 60);

        WorkerHost.PipeNameProblem("HeapScope.W.short", isWindows: false, MacTemp).ShouldBeNull();
        WorkerHost.PipeNameProblem(tooLong, isWindows: false, MacTemp).ShouldNotBeNull().ShouldContain("too long");
        WorkerHost.PipeNameProblem(tooLong, isWindows: true, MacTemp).ShouldBeNull("Windows pipe names have no such limit");
    }
}
