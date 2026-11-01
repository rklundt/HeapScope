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

using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;

namespace HeapScope.Loaders.ClrMd.Tests;

/// <summary>The local-only DAC lookup (ADR-017), with a real PE file standing in for the DAC.</summary>
public sealed class DacLocatorTests : IDisposable
{
    private const string DacName = "mscordaccore.dll";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "HeapScopeTests", Guid.NewGuid().ToString("N"));
    private readonly int _timeStamp;
    private readonly int _imageSize;

    public DacLocatorTests()
    {
        Directory.CreateDirectory(_root);
        using var stream = File.OpenRead(typeof(object).Assembly.Location);
        using var reader = new PEReader(stream);
        _timeStamp = reader.PEHeaders.CoffHeader.TimeDateStamp;
        _imageSize = reader.PEHeaders.PEHeader!.SizeOfImage;
    }

    [Fact]
    public void Candidates_are_runtime_folder_installed_runtimes_user_folder_and_own_runtime_in_order()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The locator's rules are checked with Windows paths (drive letters, UNC shares).");
        var request = Request(runtimeDirectory: @"C:\dotnet\shared\Microsoft.NETCore.App\10.0.13-custom");
        var options = new ClrMdLoaderOptions { InstalledRuntimeRoots = [@"C:\dotnet\shared\Microsoft.NETCore.App"], DacFolder = @"C:\dacs", OwnRuntimeDirectory = @"C:\HeapScope\worker\" };

        var candidates = DacLocator.CandidateDirectories(request, @"C:\dumps\app.dmp", options);

        candidates.ShouldBe([
            @"C:\dotnet\shared\Microsoft.NETCore.App\10.0.13-custom",
            @"C:\dotnet\shared\Microsoft.NETCore.App\10.0.13",
            @"C:\dacs",
            @"C:\HeapScope\worker"]);

        // The dump's own folders come last and apart (peer review P2): a DAC there is loaded only once trusted.
        DacLocator.BesideDumpDirectories(request, @"C:\dumps\app.dmp").ShouldBe([@"C:\dumps", @"C:\dumps\dac\10.0.13"]);
        DacLocator.BesideDumpDirectories(request, @"\\server\share\app.dmp").ShouldBeEmpty("never a network share");
    }

    [Fact]
    public void The_dumped_runtime_folder_can_be_left_out()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The locator's rules are checked with Windows paths (drive letters, UNC shares).");
        var options = new ClrMdLoaderOptions { InstalledRuntimeRoots = [@"C:\dotnet\shared\Microsoft.NETCore.App"], SearchDumpedRuntimeDirectory = false, OwnRuntimeDirectory = null };

        DacLocator.CandidateDirectories(Request(runtimeDirectory: @"C:\dotnet\shared\Microsoft.NETCore.App\x"), @"C:\dumps\app.dmp", options)
            .ShouldBe([@"C:\dotnet\shared\Microsoft.NETCore.App\10.0.13"]);
    }

    [Theory]
    [InlineData(@"\\attacker\share\runtime")]
    [InlineData(@"\\?\UNC\attacker\share\runtime")]
    [InlineData(@"//attacker/share/runtime")]
    [InlineData(@"C:\elsewhere\runtime")]
    [InlineData(@"C:\dotnet\shared\Microsoft.NETCore.App\..\..\..\elsewhere")]
    public void A_runtime_folder_recorded_in_the_dump_outside_the_installed_runtimes_is_never_searched(string recorded)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The locator's rules are checked with Windows paths (drive letters, UNC shares).");
        var options = new ClrMdLoaderOptions { InstalledRuntimeRoots = [@"C:\dotnet\shared\Microsoft.NETCore.App"], OwnRuntimeDirectory = null };

        DacLocator.CandidateDirectories(Request(runtimeDirectory: recorded), @"C:\dumps\app.dmp", options)
            .ShouldBe([@"C:\dotnet\shared\Microsoft.NETCore.App\10.0.13"]);
    }

    [Theory]
    [InlineData(@"\\server\share\dumps\app.dmp")]
    [InlineData(@"\\?\UNC\server\share\app.dmp")]
    public void Network_dump_and_user_folders_are_not_searched(string dumpPath)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The locator's rules are checked with Windows paths (drive letters, UNC shares).");
        var options = new ClrMdLoaderOptions { InstalledRuntimeRoots = [], DacFolder = @"\\server\dacs", OwnRuntimeDirectory = @"\\server\HeapScope" };

        DacLocator.CandidateDirectories(Request(), dumpPath, options).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(@"C:\Windows", true)]
    [InlineData(@"\\server\share", false)]
    [InlineData(@"\\?\UNC\server\share", false)]
    [InlineData(@"\\.\pipe\x", false)]
    [InlineData(@"relative\path", false)]
    [InlineData(@"", false)]
    public void Local_fixed_paths_are_recognized(string path, bool expected)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "The locator's rules are checked with Windows paths (drive letters, UNC shares).");
        DacLocator.IsLocalFixedPath(path).ShouldBe(expected);
    }

    [Fact]
    public void Finds_a_matching_file_in_the_first_folder_that_has_one()
    {
        var empty = Folder("empty");
        var wrong = Folder("wrong");
        var right = Folder("right");
        File.WriteAllText(Path.Combine(wrong, DacName), "not a PE image");
        File.Copy(typeof(object).Assembly.Location, Path.Combine(right, DacName));

        var found = DacLocator.Find(Request(), [empty, wrong, right], out var searched);

        found.ShouldBe(Path.Combine(right, DacName));
        searched.Count.ShouldBe(3);
    }

    [Fact]
    public void A_file_of_another_build_is_rejected()
    {
        var folder = Folder("other-build");
        File.Copy(typeof(object).Assembly.Location, Path.Combine(folder, DacName));

        DacLocator.Find(Request() with { TimeStamp = _timeStamp + 1 }, [folder], out _).ShouldBeNull();
        DacLocator.Find(Request() with { ImageSize = _imageSize + 4096 }, [folder], out _).ShouldBeNull();
    }

    [Fact]
    public void Without_an_index_in_the_dump_only_a_file_of_the_runtime_version_matches()
    {
        var folder = Folder("unknown-index");
        File.Copy(typeof(object).Assembly.Location, Path.Combine(folder, DacName));
        var version = System.Diagnostics.FileVersionInfo.GetVersionInfo(typeof(object).Assembly.Location);
        var fileVersion = $"{version.FileMajorPart}.{version.FileMinorPart}.{version.FileBuildPart}.{version.FilePrivatePart}";

        DacLocator.Find(Request() with { TimeStamp = 0, ImageSize = 0, RuntimeVersion = fileVersion }, [folder], out _).ShouldNotBeNull();
        DacLocator.Find(Request() with { TimeStamp = 0, ImageSize = 0, RuntimeVersion = "8.0.11.0" }, [folder], out _).ShouldBeNull();
    }

    [Fact]
    public void A_framework_dependent_process_runs_on_an_installed_runtime_and_searches_no_folder_of_its_own()
    {
        // The test host runs on a shared runtime: its folder is an installed runtime, so there is no own runtime folder.
        Path.GetFileName(DacLocator.RunningSharedFolder()).ShouldBe("shared", StringCompareShould.IgnoreCase);
        new ClrMdLoaderOptions().OwnRuntimeDirectory.ShouldBeNull();
    }

    [Theory]
    [InlineData("WINDOWS", Architecture.X64, "WINDOWS", Architecture.X64, false, null)]
    [InlineData("LINUX", Architecture.Arm64, "LINUX", Architecture.Arm64, false, null)]
    [InlineData("WINDOWS", Architecture.X64, "WINDOWS", Architecture.Arm64, true, "This dump comes from an X64 process and HeapScope runs as Arm64: open it with the X64 build of HeapScope.")]
    [InlineData("LINUX", Architecture.X64, "WINDOWS", Architecture.X64, false, "This dump comes from Linux and HeapScope runs on Windows: open it with HeapScope or heapscope-cli on Linux.")]
    [InlineData("LINUX", Architecture.X64, "WINDOWS", Architecture.X64, true, null)]
    [InlineData("OSX", Architecture.Arm64, "LINUX", Architecture.Arm64, false, "This dump comes from macOS and HeapScope runs on Linux: open it with HeapScope or heapscope-cli on macOS.")]
    public void A_dump_of_another_architecture_or_system_is_refused_with_what_to_do(string dumpSystem, Architecture dumpArchitecture, string system, Architecture host, bool symbolServer, string? expected)
    {
        // Sprint 2.3.6 review: checked before the DAC search, so a DAC of another architecture that is found never gets the
        // generic "wrong runtime version" message. The architecture wins even with the symbol server: no DAC loads across it.
        DacLocator.Mismatch(OSPlatform.Create(dumpSystem), dumpArchitecture, OSPlatform.Create(system), host, symbolServer).ShouldBe(expected);
    }

    [Fact]
    public void Missing_component_message_names_the_version_the_file_and_the_opt_in()
    {
        var exception = new RuntimeComponentMissingException("8.0.11", DacName, [@"C:\a\mscordaccore.dll"]);

        exception.Message.ShouldContain(".NET 8.0.11");
        exception.Message.ShouldContain(DacName);
        exception.Message.ShouldContain("Symbols:AllowMicrosoftSymbolServer");
        exception.Message.ShouldContain(@"C:\a\mscordaccore.dll");
        exception.RuntimeVersion.ShouldBe("8.0.11");
    }

    [Fact]
    public void Missing_component_message_leads_with_the_hint_and_never_names_an_unknown_version()
    {
        // Sprint 2.3.5 review: a dump of another system needs HeapScope there; ClrMD reads some versions as 0.0.
        var exception = new RuntimeComponentMissingException("0.0", "libmscordaccore.so", [], "This dump comes from Linux.");

        exception.Message.ShouldContain("whose version it does not record");
        exception.Message.ShouldNotContain(".NET 0.0");
        exception.Message.ShouldContain("This dump comes from Linux. Install that runtime");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private DacRequest Request(string? runtimeDirectory = null) => new(DacName, _timeStamp, _imageSize, "10.0.13", runtimeDirectory);

    private string Folder(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }
}
