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

using System.CommandLine;
using System.Globalization;
using HeapScope.Application.Sessions;
using HeapScope.Contracts;

namespace HeapScope.Cli;

/// <summary>The <c>list</c> and <c>capture</c> commands: the .NET processes that can be captured, and a capture.</summary>
internal static partial class CliCommands
{
    private static Command BuildListCommand(Func<IServiceProvider> services)
    {
        var command = new Command("list", "List running .NET processes that can be captured.");
        command.SetAction((parseResult, ct) => RunAsync(parseResult, services, async context =>
        {
            var (output, error) = (context.Output, context.Error);
            var capture = context.Get<ICaptureService>();
            var processes = await capture.ListProcessesAsync(ct).ConfigureAwait(false);
            await output.WriteLineAsync($"{"PID",8}  {"Name",-28} {"Runtime",-18} {"Elevation",-12} {"Working set",12}  Command line").ConfigureAwait(false);
            foreach (var p in processes)
            {
                var elevation = p.Elevation switch
                {
                    ProcessElevation.Elevated => "elevated",
                    ProcessElevation.NotElevated => "no",
                    _ => "unknown",
                };
                var commandLine = p.CommandLine is { Length: > 80 } text ? text[..77] + "..." : p.CommandLine ?? string.Empty;
                await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                    $"{p.ProcessId,8}  {Truncate(p.Name, 28),-28} {p.RuntimeVersion ?? "?",-18} {elevation,-12} {(p.WorkingSetBytes is { } ws ? Mb(ws) : "?"),12}  {commandLine}")).ConfigureAwait(false);
            }

            if (processes.Any(p => p.NeedsAdministrator))
            {
                await error.WriteLineAsync("Processes marked elevated can only be captured when HeapScope runs as administrator.").ConfigureAwait(false);
            }

            return Success;
        }));
        return command;
    }

    private static Command BuildCaptureCommand(Func<IServiceProvider> services)
    {
        var pid = new Option<int>("--pid") { Description = "Process id to capture (see 'list').", Required = true };
        var output = new Option<FileInfo?>("--out") { Description = "Dump file (or existing folder) to write; default: a new file in the session folder." };
        var sessionOption = new Option<string?>("--session") { Description = "Capture into this existing session (folder name or path under the sessions root) instead of a new one." };
        var label = new Option<string?>("--label") { Description = "A label for the snapshot, such as baseline." };
        var open = new Option<bool>("--open") { Description = "Open the snapshot after capturing it and print its summary." };
        var kind = KindOption();
        var command = new Command("capture", "Capture a full memory dump (the process is paused while it is written) or, with --kind gcdump, a gcdump (the process runs one full GC). Ctrl+C cancels.");
        command.Options.Add(pid);
        command.Options.Add(output);
        command.Options.Add(sessionOption);
        command.Options.Add(label);
        command.Options.Add(open);
        command.Options.Add(kind);
        command.SetAction((parseResult, ct) => RunAsync(parseResult, services, async context =>
        {
            var (stdout, error) = (context.Output, context.Error);
            var capture = context.Get<ICaptureService>();
            try
            {
                if (parseResult.GetValue(sessionOption) is { } sessionName && capture.Library.Open(sessionName) is null)
                {
                    await error.WriteLineAsync($"Error: {sessionName} is not a HeapScope session under {capture.Library.Root}.").ConfigureAwait(false);
                    return Failure;
                }
            }
            catch (SessionUnreadableException ex)
            {
                // A session that exists but cannot be read says why, not "is not a session" (sprint 3.6 review).
                await error.WriteLineAsync($"Error: {ex.Message}").ConfigureAwait(false);
                return Failure;
            }

            var job = capture.StartCapture(parseResult.GetValue(pid), parseResult.GetValue(output)?.FullName, parseResult.GetValue(label), kind: KindOf(parseResult.GetValue(kind)));
            CaptureResult result;
            try
            {
                result = await AwaitJobAsync(context, job, ct).ConfigureAwait(false);
            }
            catch (CaptureFailedException ex)
            {
                await error.WriteLineAsync("Error: " + ex.Message).ConfigureAwait(false);
                return Failure;
            }

            await stdout.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                $"Captured {result.Process.Name} (pid {result.Process.ProcessId}): {result.Path} ({Mb(result.SizeBytes)})")).ConfigureAwait(false);
            if (result.Session is { } recorded)
            {
                await stdout.WriteLineAsync($"Session: {recorded.Folder}").ConfigureAwait(false);
            }

            if (result.Warning is { } captureWarning)
            {
                await error.WriteLineAsync("Warning: " + captureWarning).ConfigureAwait(false);
            }

            await error.WriteLineAsync("Note: " + result.SecretsNote).ConfigureAwait(false);
            if (!parseResult.GetValue(open))
            {
                return Success;
            }

            var handle = (await OpenWithProgressAsync(context, result.Path, ct).ConfigureAwait(false))!;
            await stdout.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                $"Opened: {handle.Descriptor.ObjectCount:N0} objects ({Mb((long)handle.Descriptor.TotalSize)}), {handle.Descriptor.TypeCount:N0} types, {handle.Descriptor.OpenSeconds:N1} s")).ConfigureAwait(false);
            return Success;
        }));
        return command;
    }
}
