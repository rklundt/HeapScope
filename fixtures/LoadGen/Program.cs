using System.Diagnostics;
using System.Globalization;
using Microsoft.Diagnostics.NETCore.Client;

// Usage: LoadGen --url http://127.0.0.1:5080 [--requests 1000] [--concurrency 8]
//                [--dump <path> --pid <pid> [--dump-type Full|WithHeap]]
// Drives LeakyApi's POST /work, then optionally captures a dump of the target process.
// LoadGen --ctrl-c <pid>: sends Ctrl+C to the console of another process (tests press Ctrl+C in a HeapScope CLI run).
// LoadGen --launch <exe> [args]: runs a program on this console with Ctrl+C enabled (a test host's children inherit
// "ignore Ctrl+C"; an interactive shell's do not) and returns its exit code.
if (Array.IndexOf(args, "--ctrl-c") is var ctrlIndex and >= 0)
{
    return ConsoleSignal.SendCtrlC(int.Parse(args[ctrlIndex + 1], CultureInfo.InvariantCulture)) ? 0 : 1;
}

if (args.Length > 1 && args[0] == "--launch")
{
    return await ConsoleSignal.LaunchWithCtrlCAsync(args[1], args[2..]).ConfigureAwait(false);
}

var url = Arg("--url") ?? "http://127.0.0.1:5080";
var requests = int.Parse(Arg("--requests") ?? "1000", CultureInfo.InvariantCulture);
var concurrency = int.Parse(Arg("--concurrency") ?? "8", CultureInfo.InvariantCulture);
var dumpPath = Arg("--dump");
var pid = Arg("--pid");
var dumpType = Enum.Parse<DumpType>(Arg("--dump-type") ?? "Full", ignoreCase: true);

using var http = new HttpClient { BaseAddress = new Uri(url), Timeout = TimeSpan.FromMinutes(5) };
var stopwatch = Stopwatch.StartNew();
var next = 0;
var workers = Enumerable.Range(0, concurrency).Select(async _ =>
{
    while (Interlocked.Increment(ref next) <= requests)
    {
        using var response = await http.PostAsync(new Uri("/work", UriKind.Relative), null).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }
});
await Task.WhenAll(workers).ConfigureAwait(false);
Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Sent {requests:N0} requests in {stopwatch.Elapsed.TotalSeconds:N1} s"));

if (dumpPath is not null)
{
    if (pid is null)
    {
        await Console.Error.WriteLineAsync("--dump needs --pid <process id>.").ConfigureAwait(false);
        return 2;
    }

    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dumpPath))!);
    stopwatch.Restart();
    new DiagnosticsClient(int.Parse(pid, CultureInfo.InvariantCulture)).WriteDump(dumpType, Path.GetFullPath(dumpPath), logDumpGeneration: false);
    Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
        $"Wrote {dumpType} dump {dumpPath} ({new FileInfo(dumpPath).Length / (1024.0 * 1024.0):N0} MB) in {stopwatch.Elapsed.TotalSeconds:N1} s"));
}

return 0;

string? Arg(string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

/// <summary>Sends Ctrl+C to the console another process is attached to, without stopping this process.</summary>
internal static partial class ConsoleSignal
{
    private const uint CtrlCEvent = 0;

    public static async Task<int> LaunchWithCtrlCAsync(string fileName, string[] arguments)
    {
        // Clear the inherited "ignore Ctrl+C" flag; children inherit the cleared flag. This process survives the event.
        SetConsoleCtrlHandler(IntPtr.Zero, false);
        Console.CancelKeyPress += (_, e) => e.Cancel = true;
        var info = new System.Diagnostics.ProcessStartInfo(fileName) { UseShellExecute = false };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        using var child = System.Diagnostics.Process.Start(info)!;
        await child.WaitForExitAsync().ConfigureAwait(false);
        return child.ExitCode;
    }

    public static bool SendCtrlC(int processId)
    {
        FreeConsole();
        if (!AttachConsole((uint)processId))
        {
            return false;
        }

        // Ignore the event in this process; it is delivered to every process on that console.
        SetConsoleCtrlHandler(IntPtr.Zero, true);
        var sent = GenerateConsoleCtrlEvent(CtrlCEvent, 0);
        Thread.Sleep(500);
        FreeConsole();
        return sent;
    }

    [System.Runtime.InteropServices.LibraryImport("kernel32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool AttachConsole(uint processId);

    [System.Runtime.InteropServices.LibraryImport("kernel32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool FreeConsole();

    [System.Runtime.InteropServices.LibraryImport("kernel32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool SetConsoleCtrlHandler(IntPtr handler, [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)] bool add);

    [System.Runtime.InteropServices.LibraryImport("kernel32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool GenerateConsoleCtrlEvent(uint ctrlEvent, uint processGroupId);
}
