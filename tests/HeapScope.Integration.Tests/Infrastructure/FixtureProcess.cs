using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Diagnostics.NETCore.Client;

namespace HeapScope.Integration.Tests.Infrastructure;

/// <summary>
/// A running LeakyConsole or LeakyApi fixture (sprint 0.4). Waits for its READY line, can be driven (API) and dumped
/// any number of times, and is always killed on dispose. The fixture also watches this test process and exits on
/// its own if the test run is aborted.
/// </summary>
public sealed partial class FixtureProcess : IDisposable
{
    private static readonly TimeSpan s_readyTimeout = TimeSpan.FromSeconds(120);

    private readonly ChildProcess _process;
    private HttpClient? _http;

    private FixtureProcess(ChildProcess process, Uri? url)
    {
        _process = process;
        Url = url;
    }

    public int ProcessId => _process.Process.Id;

    /// <summary>The bound URL of LeakyApi; null for LeakyConsole.</summary>
    public Uri? Url { get; }

    public string Output => _process.Output;

    public static async Task<FixtureProcess> StartConsoleAsync(params string[] arguments)
    {
        var child = ChildProcess.Start(TestPaths.LeakyConsoleExe, [.. arguments, "--parent-pid", Environment.ProcessId.ToString(CultureInfo.InvariantCulture)]);
        await WaitReadyAsync(child).ConfigureAwait(false);
        return new FixtureProcess(child, null);
    }

    /// <summary>Starts LeakyApi on a free local port with the given configuration arguments.</summary>
    public static async Task<FixtureProcess> StartApiAsync(params string[] arguments)
    {
        var child = ChildProcess.Start(
            TestPaths.LeakyApiExe,
            [.. arguments, "--urls", "http://127.0.0.1:0", "--ParentPid", Environment.ProcessId.ToString(CultureInfo.InvariantCulture)]);
        await WaitReadyAsync(child).ConfigureAwait(false);
        var match = ReadyUrl().Match(child.Output);
        if (!match.Success)
        {
            child.Dispose();
            throw new InvalidOperationException("LeakyApi did not report its URL: " + child.Output);
        }

        return new FixtureProcess(child, new Uri(match.Groups["url"].Value));
    }

    /// <summary>Writes a dump of the fixture (heap only by default, enough for ClrMD analysis).</summary>
    public void CaptureDump(string path, DumpType type = DumpType.WithHeap)
    {
        try
        {
            new DiagnosticsClient(ProcessId).WriteDump(type, path, logDumpGeneration: false);
        }
        catch (Exception ex) when (ex is ServerNotAvailableException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"Could not dump fixture process {ProcessId}. Dumping needs the same user and no elevation difference between the test run and the fixture: {ex.Message}",
                ex);
        }
    }

    /// <summary>Sends <paramref name="requests"/> POST /work requests to LeakyApi.</summary>
    public async Task PostWorkAsync(int requests, CancellationToken cancellationToken)
    {
        _http ??= new HttpClient { BaseAddress = Url ?? throw new InvalidOperationException("Only LeakyApi accepts work.") };
        for (var i = 0; i < requests; i++)
        {
            using var response = await _http.PostAsync(new Uri("/work", UriKind.Relative), null, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
        }
    }

    public void Dispose()
    {
        _http?.Dispose();
        _process.Dispose();
    }

    private static async Task WaitReadyAsync(ChildProcess child)
    {
        if (!await child.WaitForOutputAsync("READY", s_readyTimeout).ConfigureAwait(false))
        {
            var output = child.Output;
            var exited = child.Process.HasExited ? $" (exited with code {child.Process.ExitCode})" : string.Empty;
            child.Dispose();
            throw new InvalidOperationException($"Fixture did not become ready{exited}: {output}");
        }
    }

    [GeneratedRegex(@"READY pid=\d+ url=(?<url>\S+)")]
    private static partial Regex ReadyUrl();
}

/// <summary>A temporary folder for dumps, deleted on dispose (best effort).</summary>
public sealed class TempFolder : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "HeapScopeTests", Guid.NewGuid().ToString("N"));

    public TempFolder() => Directory.CreateDirectory(Path);

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // A scanner may still hold a dump briefly; the folder is under %TEMP%.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
