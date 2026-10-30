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
using HeapScope.Cli;
using HeapScope.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

IHost? host = null;
var root = CliCommands.Build(() => host!.Services);
var parseResult = root.Parse(args);

// The Attribution Notice (ADDITIONAL-TERMS.md 1.4) ends help output (version output prints it itself) and starts every
// other run. At startup it goes to stderr and only to a console, so piped table, csv and json output stays machine
// readable.
var isHelp = parseResult.Action is HelpAction;
if (!isHelp && parseResult.Action is not CliCommands.ProductVersionAction && !Console.IsErrorRedirected)
{
    await Console.Error.WriteLineAsync(ProductInfo.AttributionWithUrl).ConfigureAwait(false);
}

if (parseResult.Errors.Count > 0)
{
    // Prints the errors and usage; usage errors exit 64 (EX_USAGE) so 1 and 2 keep their meanings for CI.
    await parseResult.InvokeAsync(CliCommands.CreateInvocationConfiguration()).ConfigureAwait(false);
    await Console.Out.WriteLineAsync(ProductInfo.AttributionWithUrl).ConfigureAwait(false);
    return CliCommands.Usage;
}

// The worker attach pipe must be known before the host is built.
host = CliHost.Build(
    parseResult.GetValue(CliCommands.PipeOption),
    parseResult.GetValue(CliCommands.DacFolderOption),
    parseResult.GetValue(CliCommands.AllowSymbolServerOption));
using (host)
{
    // The CLI never asks: graph-only commands open a cached dump whatever its DAC, and reads that need an untrusted DAC
    // refuse with the --trust-dac option to add (sprint 3.6 re-review).
    host.Services.GetRequiredService<HeapScope.Application.Snapshots.SnapshotSession>().CheckDacOnCacheOpen = false;
    var trustedDacs = host.Services.GetRequiredService<HeapScope.Application.Snapshots.TrustedDacs>();
    foreach (var sha256 in parseResult.GetValue(CliCommands.TrustDacOption) ?? [])
    {
        trustedDacs.TrustForThisRun(sha256);
    }

    await host.StartAsync().ConfigureAwait(false);
    try
    {
        var exitCode = await parseResult.InvokeAsync(CliCommands.CreateInvocationConfiguration()).ConfigureAwait(false);
        if (isHelp)
        {
            await Console.Out.WriteLineAsync(ProductInfo.AttributionWithUrl).ConfigureAwait(false);
        }

        return exitCode;
    }
    finally
    {
        using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await host.StopAsync(stopTimeout.Token).ConfigureAwait(false);
    }
}
