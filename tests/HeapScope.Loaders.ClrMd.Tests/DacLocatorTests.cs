using System.Reflection.PortableExecutable;

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
    public void Candidates_are_runtime_folder_installed_runtimes_dump_folder_and_user_folder_in_order()
    {
        var request = Request(runtimeDirectory: @"C:\dotnet\shared\Microsoft.NETCore.App\10.0.13-custom");
        var options = new ClrMdLoaderOptions { InstalledRuntimeRoots = [@"C:\dotnet\shared\Microsoft.NETCore.App"], DacFolder = @"C:\dacs" };

        var candidates = DacLocator.CandidateDirectories(request, @"C:\dumps\app.dmp", options);

        candidates.ShouldBe([
            @"C:\dotnet\shared\Microsoft.NETCore.App\10.0.13-custom",
            @"C:\dotnet\shared\Microsoft.NETCore.App\10.0.13",
            @"C:\dumps",
            @"C:\dumps\dac\10.0.13",
            @"C:\dacs"]);
    }

    [Fact]
    public void The_dumped_runtime_folder_can_be_left_out()
    {
        var options = new ClrMdLoaderOptions { InstalledRuntimeRoots = [@"C:\dotnet\shared\Microsoft.NETCore.App"], SearchDumpedRuntimeDirectory = false };

        DacLocator.CandidateDirectories(Request(runtimeDirectory: @"C:\dotnet\shared\Microsoft.NETCore.App\x"), @"C:\dumps\app.dmp", options)
            .ShouldBe([@"C:\dotnet\shared\Microsoft.NETCore.App\10.0.13", @"C:\dumps", @"C:\dumps\dac\10.0.13"]);
    }

    [Theory]
    [InlineData(@"\\attacker\share\runtime")]
    [InlineData(@"\\?\UNC\attacker\share\runtime")]
    [InlineData(@"//attacker/share/runtime")]
    [InlineData(@"C:\elsewhere\runtime")]
    [InlineData(@"C:\dotnet\shared\Microsoft.NETCore.App\..\..\..\elsewhere")]
    public void A_runtime_folder_recorded_in_the_dump_outside_the_installed_runtimes_is_never_searched(string recorded)
    {
        var options = new ClrMdLoaderOptions { InstalledRuntimeRoots = [@"C:\dotnet\shared\Microsoft.NETCore.App"] };

        DacLocator.CandidateDirectories(Request(runtimeDirectory: recorded), @"C:\dumps\app.dmp", options)
            .ShouldBe([@"C:\dotnet\shared\Microsoft.NETCore.App\10.0.13", @"C:\dumps", @"C:\dumps\dac\10.0.13"]);
    }

    [Theory]
    [InlineData(@"\\server\share\dumps\app.dmp")]
    [InlineData(@"\\?\UNC\server\share\app.dmp")]
    public void Network_dump_and_user_folders_are_not_searched(string dumpPath)
    {
        var options = new ClrMdLoaderOptions { InstalledRuntimeRoots = [], DacFolder = @"\\server\dacs" };

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
    public void Missing_component_message_names_the_version_the_file_and_the_opt_in()
    {
        var exception = new RuntimeComponentMissingException("8.0.11", DacName, [@"C:\a\mscordaccore.dll"]);

        exception.Message.ShouldContain(".NET 8.0.11");
        exception.Message.ShouldContain(DacName);
        exception.Message.ShouldContain("Symbols:AllowMicrosoftSymbolServer");
        exception.Message.ShouldContain(@"C:\a\mscordaccore.dll");
        exception.RuntimeVersion.ShouldBe("8.0.11");
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
