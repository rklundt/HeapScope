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

/// <summary>
/// Builds the command tree (ADR-008); each command is in its own file, the shared options in CliOptions.cs and the run
/// boundary in CliRun.cs. Exit codes: 0 success, 1 failure, 2 <c>diff --fail-on-growth</c> threshold exceeded, 64 usage
/// error (EX_USAGE), 130 canceled.
/// </summary>
internal static partial class CliCommands
{
    public const int Success = 0;
    public const int Failure = 1;
    public const int GrowthExceeded = 2;
    public const int Usage = 64;
    public const int Canceled = 130;

    /// <param name="services">The host's services, asked for only when a command runs (the host is built after parsing).</param>
    public static RootCommand Build(Func<IServiceProvider> services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var root = new RootCommand($"{ProductInfo.Name} {ProductInfo.Version}: .NET memory profiler command line");
        root.Options.Add(PipeOption);
        root.Options.Add(DacFolderOption);
        root.Options.Add(AllowSymbolServerOption);
        root.Options.Add(TrustDacOption);

        // The built-in --version prints the raw informational version (with the source revision);
        // the product version is ProductInfo.Version everywhere (ARCHITECTURE.md 15).
        foreach (var option in root.Options)
        {
            if (option is VersionOption)
            {
                option.Action = new ProductVersionAction();
            }
        }

        root.Subcommands.Add(BuildWorkerCommand(services));
        root.Subcommands.Add(BuildSyntheticCommand(services));
        root.Subcommands.Add(BuildOpenCommand(services));
        root.Subcommands.Add(BuildSummaryCommand(services));
        root.Subcommands.Add(BuildDiffCommand(services));
        root.Subcommands.Add(BuildTrendCommand(services));
        root.Subcommands.Add(BuildInstancesCommand(services));
        root.Subcommands.Add(BuildObjectCommand(services));
        root.Subcommands.Add(BuildReferrersCommand(services));
        root.Subcommands.Add(BuildPathsCommand(services));
        root.Subcommands.Add(BuildRetainersCommand(services));
        root.Subcommands.Add(BuildRetainedByCommand(services));
        root.Subcommands.Add(BuildSetCommand(services));
        root.Subcommands.Add(BuildInspectCommand(services));
        root.Subcommands.Add(BuildTraceCommand(services));
        root.Subcommands.Add(BuildAllocationsCommand(services));
        root.Subcommands.Add(BuildMonitorCommand(services));
        root.Subcommands.Add(BuildListCommand(services));
        root.Subcommands.Add(BuildCaptureCommand(services));
        return root;
    }
}
