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

using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace HeapScope.Worker;

/// <summary>Validated command line options of the worker process.</summary>
public sealed record WorkerOptions(string Pipe, int? ParentPid, bool EnableTestHooks = false)
{
    public const string SectionName = "Worker";

    /// <summary>Flag (no value) that enables the crash and hang test hooks.</summary>
    public const string EnableTestHooksSwitch = "--enable-test-hooks";

    /// <summary>Configuration section of the DAC lookup options (ADR-017).</summary>
    public const string SymbolsSectionName = "Symbols";

    /// <summary>A user folder searched for the data access component (<c>--dac-folder</c>).</summary>
    public string? DacFolder { get; init; }

    /// <summary>Opt-in: let ClrMD download a missing DAC from Microsoft's symbol server (<c>--allow-symbol-server true</c>).</summary>
    public bool AllowMicrosoftSymbolServer { get; init; }

    /// <summary>How long the worker waits for its client before giving up.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Creates the map from command line switches to configuration keys.</summary>
    public static Dictionary<string, string> CreateSwitchMappings() => new()
    {
        ["--pipe"] = $"{SectionName}:Pipe",
        ["--parent-pid"] = $"{SectionName}:ParentPid",
        ["--dac-folder"] = $"{SymbolsSectionName}:DacFolder",
        ["--allow-symbol-server"] = $"{SymbolsSectionName}:AllowMicrosoftSymbolServer",
    };

    /// <summary>
    /// Reads and validates the options. Returns false with a user-facing error for missing or malformed values,
    /// so the worker can exit with a usage error instead of an unhandled exception.
    /// </summary>
    public static bool TryRead(IConfiguration configuration, bool enableTestHooks, out WorkerOptions? options, out string? error)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        options = null;
        var section = configuration.GetSection(SectionName);

        var pipe = section["Pipe"];
        if (string.IsNullOrWhiteSpace(pipe))
        {
            error = "Missing required option --pipe <name>.";
            return false;
        }

        int? parentPid = null;
        var rawParentPid = section["ParentPid"];
        if (rawParentPid is not null)
        {
            if (!int.TryParse(rawParentPid, NumberStyles.None, CultureInfo.InvariantCulture, out var pid) || pid <= 0)
            {
                error = $"Invalid --parent-pid '{rawParentPid}': expected a process id.";
                return false;
            }

            parentPid = pid;
        }

        var symbols = configuration.GetSection(SymbolsSectionName);
        var rawAllow = symbols["AllowMicrosoftSymbolServer"];
        var allow = false;
        if (rawAllow is not null && !bool.TryParse(rawAllow, out allow))
        {
            error = $"Invalid --allow-symbol-server '{rawAllow}': expected true or false.";
            return false;
        }

        options = new WorkerOptions(pipe, parentPid, enableTestHooks)
        {
            DacFolder = string.IsNullOrWhiteSpace(symbols["DacFolder"]) ? null : symbols["DacFolder"],
            AllowMicrosoftSymbolServer = allow,
        };
        error = null;
        return true;
    }
}
