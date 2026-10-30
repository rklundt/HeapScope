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

using Microsoft.Extensions.Hosting;

namespace HeapScope.Cli.Tests;

/// <summary>Runs the CLI in this process with captured output, as the executable would with the same arguments.</summary>
internal static class InProcessCli
{
    /// <summary>A host factory for runs that must not get as far as a host: a usage error needs none.</summary>
    public static IHost NoHost(string? pipe, string? dacFolder, bool allowSymbolServer) =>
        throw new InvalidOperationException("This run was expected to stop before building a host.");

    public static async Task<(int Code, string Output, string Error)> RunAsync(Func<string?, string?, bool, IHost> hostFactory, params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var code = await CliApp.RunAsync(args, output, error, hostFactory, errorIsConsole: false);
        return (code, output.ToString(), error.ToString());
    }
}
