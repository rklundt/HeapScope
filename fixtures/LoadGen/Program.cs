using System.Diagnostics;
using System.Globalization;
using Microsoft.Diagnostics.NETCore.Client;

// Usage: LoadGen --url http://127.0.0.1:5080 [--requests 1000] [--concurrency 8]
//                [--dump <path> --pid <pid> [--dump-type Full|WithHeap]]
// Drives LeakyApi's POST /work, then optionally captures a dump of the target process.
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
