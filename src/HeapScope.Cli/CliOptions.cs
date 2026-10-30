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
using HeapScope.Contracts;

namespace HeapScope.Cli;

/// <summary>The options several commands share: the root's worker and DAC options, and the table options.</summary>
internal static partial class CliCommands
{
    /// <summary>Attach to an already running worker (debugging) instead of launching one.</summary>
    public static readonly Option<string?> PipeOption = new("--pipe")
    {
        Description = "Attach to a worker already listening on this pipe instead of starting one.",
        Recursive = true,
    };

    /// <summary>A folder searched for the data access component (ADR-017), like <c>Symbols:DacFolder</c>.</summary>
    public static readonly Option<string?> DacFolderOption = new("--dac-folder")
    {
        Description = "A folder holding mscordaccore.dll of the dumped runtime, when that runtime is not installed here.",
        Recursive = true,
    };

    /// <summary>Opt-in to symbol server downloads (ADR-017), like <c>Symbols:AllowMicrosoftSymbolServer</c>.</summary>
    public static readonly Option<bool> AllowSymbolServerOption = new("--allow-symbol-server")
    {
        Description = "Allow downloading a missing mscordaccore.dll from Microsoft's symbol server (sends module identifiers, never dump contents).",
        Recursive = true,
    };

    /// <summary>
    /// DACs trusted for this run by SHA-256 (sprint 3.6, peer review P2): a DAC found only next to a dump came with it, so
    /// it is loaded only when its hash is named here (the refusal prints the exact option to add).
    /// </summary>
    public static readonly Option<string[]> TrustDacOption = new("--trust-dac")
    {
        Description = "Trust the DAC next to a dump with this SHA-256 for this run (the refusal names it). Repeat for several.",
        Recursive = true,
        Validators =
        {
            result =>
            {
                foreach (var value in result.GetValueOrDefault<string[]>() ?? [])
                {
                    if (!HeapScope.Application.Snapshots.TrustedDacs.IsSha256(value.Trim().ToLowerInvariant()))
                    {
                        result.AddError($"--trust-dac takes a SHA-256 of 64 hex digits; {value} is not one.");
                    }
                }
            },
        },
    };

    /// <summary>What a capture writes (sprint 1.8): a full dump (the default) or a gcdump.</summary>
    internal static Option<string> KindOption()
    {
        var kind = new Option<string>("--kind") { Description = "What to capture: full (a full memory dump) or gcdump (the object graph only: smaller, no field values or string text).", DefaultValueFactory = _ => "full" };
        kind.AcceptOnlyFromAmong("full", "gcdump");
        return kind;
    }

    internal static SnapshotKindDto KindOf(string? kind) => kind == "gcdump" ? SnapshotKindDto.GcDump : SnapshotKindDto.FullDump;

    /// <summary>The <c>--retained</c> option of summary, instances and object (sprint 2.3).</summary>
    internal static Option<bool> RetainedOption(string description) => new("--retained") { Description = description };

    private static Option<string> GroupOption()
    {
        var group = new Option<string>("--group") { Description = "Rows per type, namespace, assembly or interface (full dumps; a type counts under each interface it implements).", DefaultValueFactory = _ => "type" };
        group.AcceptOnlyFromAmong("type", "namespace", "assembly", "interface");
        return group;
    }

    private static Option<string?> FilterOption(string more = "") => new("--filter")
    {
        Description = "Keep rows whose name matches: lower case ignores case; capitals match as typed or as CamelHumps (SCE finds StaticCacheEntry); * matches anything." + more,
    };

    private static Option<int> TopOption()
    {
        var top = new Option<int>("--top") { Description = "Number of rows.", DefaultValueFactory = _ => 25 };
        top.Validators.Add(result =>
        {
            if (result.GetValueOrDefault<int>() < 1)
            {
                result.AddError("--top must be 1 or more.");
            }
        });
        return top;
    }

    private static Option<bool> AllOption() => new("--all") { Description = "Print every row." };

    private static Option<string> FormatOption()
    {
        var format = new Option<string>("--format") { Description = "Output: table, csv or json.", DefaultValueFactory = _ => "table" };
        format.AcceptOnlyFromAmong("table", "csv", "json");
        return format;
    }

    private static void AddTopOrAllValidator(Command command, Option<int> top, Option<bool> all) =>
        command.Validators.Add(result =>
        {
            if (result.GetResult(all) is { Implicit: false } && result.GetResult(top) is { Implicit: false })
            {
                result.AddError("Use either --top or --all, not both.");
            }
        });
}
