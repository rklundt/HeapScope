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

using System.CommandLine.Help;
using HeapScope.Application.Snapshots;
using HeapScope.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace HeapScope.Cli;

/// <summary>
/// One run of the command line: parse, then (unless the command line is wrong) build the host and run the command. The
/// writers and the host factory are parameters so tests run the CLI in process.
/// </summary>
internal static class CliApp
{
    /// <param name="args">The command line.</param>
    /// <param name="output">Results, help and version (stdout).</param>
    /// <param name="error">Progress, notes and errors (stderr).</param>
    /// <param name="hostFactory">
    /// Builds the host from the worker attach pipe, the DAC folder and the symbol server opt-in (<see cref="CliHost.Build"/>);
    /// a usage error never calls it.
    /// </param>
    /// <param name="errorIsConsole">Whether stderr is a console, where the attribution notice starts a run.</param>
    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, Func<string?, string?, bool, IHost> hostFactory, bool errorIsConsole)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        ArgumentNullException.ThrowIfNull(hostFactory);
        IHost? host = null;
        var root = CliCommands.Build(() => host!.Services);
        var parseResult = root.Parse(args);
        var configuration = CliCommands.CreateInvocationConfiguration(output, error);

        // The Attribution Notice (ADDITIONAL-TERMS.md 1.4) ends help output (version output prints it itself) and starts
        // every other run. At startup it goes to stderr and only to a console, so piped table, csv and json output stays
        // machine readable.
        var isHelp = parseResult.Action is HelpAction;
        if (!isHelp && parseResult.Action is not CliCommands.ProductVersionAction && errorIsConsole)
        {
            await error.WriteLineAsync(ProductInfo.AttributionWithUrl).ConfigureAwait(false);
        }

        if (parseResult.Errors.Count > 0)
        {
            // Prints the errors and usage; usage errors exit 64 (EX_USAGE) so 1 and 2 keep their meanings for CI.
            await parseResult.InvokeAsync(configuration).ConfigureAwait(false);
            await output.WriteLineAsync(ProductInfo.AttributionWithUrl).ConfigureAwait(false);
            return CliCommands.Usage;
        }

        // The worker attach pipe must be known before the host is built.
        host = hostFactory(
            parseResult.GetValue(CliCommands.PipeOption),
            parseResult.GetValue(CliCommands.DacFolderOption),
            parseResult.GetValue(CliCommands.AllowSymbolServerOption));
        using (host)
        {
            // The CLI never asks: graph-only commands open a cached dump whatever its DAC, and reads that need an untrusted
            // DAC refuse with the --trust-dac option to add (sprint 3.6 re-review).
            host.Services.GetRequiredService<SnapshotSession>().CheckDacOnCacheOpen = false;
            var trustedDacs = host.Services.GetRequiredService<TrustedDacs>();
            foreach (var sha256 in parseResult.GetValue(CliCommands.TrustDacOption) ?? [])
            {
                trustedDacs.TrustForThisRun(sha256);
            }

            await host.StartAsync().ConfigureAwait(false);
            try
            {
                var exitCode = await parseResult.InvokeAsync(configuration).ConfigureAwait(false);
                if (isHelp)
                {
                    await output.WriteLineAsync(ProductInfo.AttributionWithUrl).ConfigureAwait(false);
                }

                return exitCode;
            }
            finally
            {
                using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await host.StopAsync(stopTimeout.Token).ConfigureAwait(false);
            }
        }
    }
}
