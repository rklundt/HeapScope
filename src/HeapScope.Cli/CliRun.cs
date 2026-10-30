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
using System.CommandLine.Invocation;
using System.Globalization;
using HeapScope.Application.Logging;
using HeapScope.Application.Sessions;
using HeapScope.Application.Workers;
using HeapScope.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HeapScope.Cli;

/// <summary>The run boundary every command body goes through, the command line for the log, and the version action.</summary>
internal static partial class CliCommands
{
    /// <summary>
    /// Writes results to <paramref name="output"/> and diagnostics to <paramref name="error"/>. Ctrl+C cancels the
    /// command's token and then waits up to an hour for the command to return: a canceled capture must let the runtime
    /// finish writing and then delete the dump (sprint 1.1 review H1). The default (2 seconds) would return 130 and exit
    /// with the dump left on disk; null would turn Ctrl+C handling off altogether.
    /// </summary>
    public static InvocationConfiguration CreateInvocationConfiguration(TextWriter output, TextWriter error) =>
        new() { ProcessTerminationTimeout = TimeSpan.FromHours(1), Output = output, Error = error };

    /// <summary>Runs a command body with the CLI's error handling boundary (ARCHITECTURE.md 11).</summary>
    private static async Task<int> RunAsync(ParseResult parseResult, Func<IServiceProvider> services, Func<CliContext, Task<int>> body)
    {
        var provider = services();
        var logger = provider.GetService<ILoggerFactory>()?.CreateLogger("HeapScope.Cli") ?? NullLogger.Instance;
        var logFolder = provider.GetService<FileLoggerProvider>()?.Directory ?? FileLoggerProvider.DefaultDirectory;
        var output = parseResult.InvocationConfiguration.Output;
        var error = parseResult.InvocationConfiguration.Error;

        // The command and what it was given, under one activity whose trace id the worker's lines carry too (sprint 2.3.7
        // review H4, M1). Paths and numbers are logged as they are; any other value by its length (a filter may be secret).
        using var activity = new System.Diagnostics.Activity("HeapScope.Cli").Start();
        var command = parseResult.CommandResult.Command.Name;
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        logger.LogInformation("Command {Command} started: {Arguments}", command, DescribeTokens(parseResult));
        int exitCode;
        try
        {
            exitCode = await body(new CliContext(provider, output, error, logger)).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await error.WriteLineAsync("Canceled.").ConfigureAwait(false);
            exitCode = Canceled;
        }
        catch (Exception ex) when (ex is WorkerException
            or CaptureFailedException { Reason: CaptureFailure.NotSupportedOnThisSystem })
        {
            // What the user was told, in the log too (sprint 2.3.7 review H4).
            logger.LogInformation("Command {Command} failed: {Message}", command, ex.Message);
            await error.WriteLineAsync("Error: " + ex.Message).ConfigureAwait(false);
            exitCode = Failure;
        }
        catch (Exception ex)
        {
            // Last-chance boundary: a plain message for the user, the stack trace for the log (ARCHITECTURE.md 11).
            logger.LogError(ex, "Command {Command} failed", command);
            await error.WriteLineAsync($"Error: {ex.Message} Details are in the log in {logFolder}.").ConfigureAwait(false);
            exitCode = Failure;
        }

        logger.LogInformation("Command {Command} exited with {ExitCode} after {Seconds:N2} s", command, exitCode, System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds);
        return exitCode;
    }

    /// <summary>The command line for the log: option names, paths and numbers as given, other values by their length.</summary>
    internal static string DescribeTokens(ParseResult parseResult) => string.Join(
        " ",
        parseResult.Tokens.Select(token => token.Type switch
        {
            System.CommandLine.Parsing.TokenType.Argument when File.Exists(token.Value) || Directory.Exists(token.Value)
                || double.TryParse(token.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out _)
                || token.Value is "true" or "false" => token.Value,
            System.CommandLine.Parsing.TokenType.Argument => string.Create(CultureInfo.InvariantCulture, $"({token.Value.Length} chars)"),
            _ => token.Value,
        }));

    internal sealed class ProductVersionAction : SynchronousCommandLineAction
    {
        public override int Invoke(ParseResult parseResult)
        {
            var output = parseResult.InvocationConfiguration.Output;
            output.WriteLine(ProductInfo.Version);
            output.WriteLine(ProductInfo.AttributionWithUrl); // ADDITIONAL-TERMS.md 1.4
            return Success;
        }
    }
}
