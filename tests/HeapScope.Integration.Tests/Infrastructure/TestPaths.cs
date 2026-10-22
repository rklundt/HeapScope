namespace HeapScope.Integration.Tests.Infrastructure;

internal static class TestPaths
{
#if DEBUG
    public const string Configuration = "Debug";
#else
    public const string Configuration = "Release";
#endif

    public static string RepoRoot { get; } = FindRoot();

    public static string WorkerExe => Path.Combine(AppContext.BaseDirectory, "worker", "HeapScope.Worker.exe");

    public static string CliOutput => Path.Combine(RepoRoot, "src", "HeapScope.Cli", "bin", Configuration, "net10.0");

    public static string CliExe => Path.Combine(CliOutput, "heapscope-cli.exe");

    public static string UiOutput => Path.Combine(RepoRoot, "src", "HeapScope.UI.Wpf", "bin", Configuration, "net10.0-windows");

    public static string UiExe => Path.Combine(UiOutput, "HeapScope.exe");

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "HeapScope.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("HeapScope.sln not found above the test output folder.");
    }
}
