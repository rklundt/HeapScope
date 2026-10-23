using System.Diagnostics;
using System.Text;

namespace HeapScope.Integration.Tests.Infrastructure;

/// <summary>
/// A process started by a test. Captures output and always kills the whole process tree on dispose,
/// so a failing test never leaves processes behind.
/// </summary>
internal sealed class ChildProcess : IDisposable
{
    private readonly StringBuilder _output = new();
    private readonly object _gate = new();

    private ChildProcess(Process process)
    {
        Process = process;
    }

    public Process Process { get; }

    public string Output
    {
        get
        {
            lock (_gate)
            {
                return _output.ToString();
            }
        }
    }

    /// <summary>Session library root for every front end a test starts (sprint 1.1).</summary>
    public static string TestSessionsRoot { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "HeapScopeTests", "sessions");

    public static ChildProcess Start(string fileName, params string[] arguments)
    {
        var info = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }

        // HeapScope front ends started by tests keep their session folders out of the user's real library.
        info.Environment["Sessions__Root"] = TestSessionsRoot;

        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        var child = new ChildProcess(process);
        process.OutputDataReceived += (_, e) => child.Append(e.Data);
        process.ErrorDataReceived += (_, e) => child.Append(e.Data);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return child;
    }

    /// <summary>Waits for exit; returns false on timeout.</summary>
    public async Task<bool> WaitForExitAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await Process.WaitForExitAsync(cts.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>Polls until the output contains <paramref name="text"/>; returns false on timeout.</summary>
    public async Task<bool> WaitForOutputAsync(string text, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (Output.Contains(text, StringComparison.Ordinal))
            {
                return true;
            }

            if (Process.HasExited)
            {
                // Let the output reader drain before the final check.
                await Process.WaitForExitAsync();
                return Output.Contains(text, StringComparison.Ordinal);
            }

            await Task.Delay(50);
        }

        return Output.Contains(text, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        try
        {
            if (!Process.HasExited)
            {
                Process.Kill(entireProcessTree: true);
                Process.WaitForExit(5000);
            }
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }

        Process.Dispose();
    }

    private void Append(string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (_gate)
        {
            _output.AppendLine(line);
        }
    }
}
