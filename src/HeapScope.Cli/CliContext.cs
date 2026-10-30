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

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HeapScope.Cli;

/// <summary>
/// What a running command works with: the host's services, the result output (stdout), the diagnostics output (stderr)
/// and the command's logger. Created per command run by <see cref="CliCommands"/>' run boundary.
/// </summary>
internal sealed class CliContext(IServiceProvider services, TextWriter output, TextWriter error, ILogger logger)
{
    public IServiceProvider Services { get; } = services;

    /// <summary>Command results: tables, CSV and JSON.</summary>
    public TextWriter Output { get; } = output;

    /// <summary>Progress, notes, warnings and errors.</summary>
    public TextWriter Error { get; } = error;

    public ILogger Logger { get; } = logger;

    public T Get<T>()
        where T : notnull => Services.GetRequiredService<T>();
}
