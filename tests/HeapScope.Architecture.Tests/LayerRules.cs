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

namespace HeapScope.Architecture.Tests;

/// <summary>The dependency table of ARCHITECTURE.md section 3.1, as code.</summary>
internal static class LayerRules
{
    private sealed record Rule(string[] Projects, string[] Packages, bool AllowsMicrosoftExtensions = true);

    private static readonly string[] s_buildOrderOnlyAllowed = ["HeapScope.Worker"];

    private static readonly Dictionary<string, Rule> s_rules = new(StringComparer.Ordinal)
    {
        ["HeapScope.Core"] = new([], [], AllowsMicrosoftExtensions: false),
        ["HeapScope.Analysis"] = new(["HeapScope.Core"], ["System.IO.Hashing"]),
        ["HeapScope.Storage"] = new(["HeapScope.Core"], ["System.IO.Hashing"]),
        ["HeapScope.Capture"] = new(
            ["HeapScope.Core", "HeapScope.ThirdParty.GcDump"],
            ["Microsoft.Diagnostics.NETCore.Client", "Microsoft.Diagnostics.Tracing.TraceEvent"]),
        ["HeapScope.Loaders.ClrMd"] = new(["HeapScope.Core"], ["Microsoft.Diagnostics.Runtime"]),
        ["HeapScope.Loaders.GcDump"] = new(["HeapScope.Core", "HeapScope.ThirdParty.GcDump"], ["Microsoft.Diagnostics.Tracing.TraceEvent"]),

        // Allocation traces (sprint 2.5, ADR-028): EventPipe traces read with TraceEvent.
        ["HeapScope.Loaders.Trace"] = new(["HeapScope.Core"], ["Microsoft.Diagnostics.Tracing.TraceEvent"]),

        // Vendored gcdump reader and writer (sprint 1.8, THIRD-PARTY-NOTICES.md): diagnostics libraries only.
        ["HeapScope.ThirdParty.GcDump"] = new(
            [],
            ["Microsoft.Diagnostics.NETCore.Client", "Microsoft.Diagnostics.Tracing.TraceEvent"],
            AllowsMicrosoftExtensions: false),
        ["HeapScope.Contracts"] = new([], ["StreamJsonRpc"]),
        ["HeapScope.Worker"] = new(
            [
                "HeapScope.Core", "HeapScope.Contracts", "HeapScope.Analysis", "HeapScope.Storage",
                "HeapScope.Loaders.ClrMd", "HeapScope.Loaders.GcDump", "HeapScope.Loaders.Trace",
            ],
            ["StreamJsonRpc"]),
        ["HeapScope.Application"] = new(
            ["HeapScope.Core", "HeapScope.Contracts", "HeapScope.Capture"],
            ["StreamJsonRpc"]),
        ["HeapScope.Cli"] = new(["HeapScope.Application", "HeapScope.Contracts"], ["System.CommandLine"]),
        // The view models without a UI toolkit (sprint 2.3.6, ADR-025): what the Avalonia app and its tests share.
        ["HeapScope.UI.ViewModels"] = new(["HeapScope.Application", "HeapScope.Contracts"], ["CommunityToolkit.Mvvm"]),

        // The Avalonia desktop app (sprint 2.3.6, ADR-025): MIT Avalonia packages only (the update rule in CLAUDE.md).
        // Avalonia.Fonts.Inter is referenced only to exclude its assets (an OFL font that comes with Avalonia.Headless).
        ["HeapScope.UI"] = new(
            ["HeapScope.Application", "HeapScope.Contracts", "HeapScope.UI.ViewModels"],
            ["Avalonia", "Avalonia.Desktop", "Avalonia.Themes.Fluent", "Avalonia.Controls.DataGrid", "Avalonia.Headless", "Avalonia.Fonts.Inter", "CommunityToolkit.Mvvm"]),
    };

    /// <summary>Assembly names that differ from project names.</summary>
    public static readonly IReadOnlyDictionary<string, string> AssemblyToProject = new Dictionary<string, string>
    {
        ["heapscope-cli"] = "HeapScope.Cli",
        ["HeapScope"] = "HeapScope.UI",
    };

    public static IReadOnlyCollection<string> KnownProjects => s_rules.Keys;

    /// <summary>Returns one message per violation; empty when the project follows the rules.</summary>
    public static IReadOnlyList<string> Check(ProjectInfo project)
    {
        var violations = new List<string>();
        if (!s_rules.TryGetValue(project.Name, out var rule))
        {
            violations.Add($"{project.Name} is not in the dependency table; add it to ARCHITECTURE.md 3.1 and LayerRules.");
            return violations;
        }

        foreach (var reference in project.ProjectReferences)
        {
            if (!rule.Projects.Contains(reference, StringComparer.Ordinal))
            {
                violations.Add($"{project.Name} must not reference project {reference}.");
            }
        }

        foreach (var reference in project.BuildOrderOnlyReferences)
        {
            if (!s_buildOrderOnlyAllowed.Contains(reference, StringComparer.Ordinal))
            {
                violations.Add($"{project.Name} has a build-order-only reference to {reference}, which is not allowed.");
            }
        }

        foreach (var package in project.PackageReferences)
        {
            var allowed = package.StartsWith("Microsoft.Extensions.", StringComparison.Ordinal)
                ? rule.AllowsMicrosoftExtensions
                : rule.Packages.Contains(package, StringComparer.Ordinal);
            if (!allowed)
            {
                violations.Add($"{project.Name} must not reference package {package}.");
            }
        }

        return violations;
    }

    /// <summary>Assembly name prefixes of the governed packages, so compiled references can be checked too.</summary>
    private static readonly (string AssemblyPrefix, string Package)[] s_packageAssemblies =
    [
        ("Microsoft.Diagnostics.Runtime", "Microsoft.Diagnostics.Runtime"),
        ("Microsoft.Diagnostics.NETCore.Client", "Microsoft.Diagnostics.NETCore.Client"),
        ("Microsoft.Diagnostics.Tracing.TraceEvent", "Microsoft.Diagnostics.Tracing.TraceEvent"),
        ("Microsoft.Diagnostics.FastSerialization", "Microsoft.Diagnostics.Tracing.TraceEvent"),
        ("StreamJsonRpc", "StreamJsonRpc"),
        ("CommunityToolkit.Mvvm", "CommunityToolkit.Mvvm"),
        ("System.CommandLine", "System.CommandLine"),
        ("System.IO.Hashing", "System.IO.Hashing"),
    ];

    /// <summary>
    /// Checks one compiled assembly reference: solution assemblies and governed package assemblies must follow
    /// the table, and Microsoft.Extensions.* is not allowed where the rule forbids it (Core).
    /// </summary>
    public static string? CheckAssemblyReference(string project, string referencedAssembly)
    {
        var rule = s_rules[project];
        var referencedProject = AssemblyToProject.TryGetValue(referencedAssembly, out var mapped) ? mapped : referencedAssembly;
        if (s_rules.ContainsKey(referencedProject))
        {
            return rule.Projects.Contains(referencedProject, StringComparer.Ordinal)
                ? null
                : $"{project} uses types from {referencedProject}.";
        }

        if (referencedAssembly.StartsWith("Microsoft.Extensions.", StringComparison.Ordinal))
        {
            return rule.AllowsMicrosoftExtensions ? null : $"{project} uses {referencedAssembly}.";
        }

        foreach (var (prefix, package) in s_packageAssemblies)
        {
            if (referencedAssembly.StartsWith(prefix, StringComparison.Ordinal))
            {
                return rule.Packages.Contains(package, StringComparer.Ordinal)
                    ? null
                    : $"{project} uses {referencedAssembly} from package {package}.";
            }
        }

        if (referencedAssembly.StartsWith("Microsoft.Diagnostics.", StringComparison.Ordinal))
        {
            return $"{project} uses unknown diagnostics library {referencedAssembly}; add it to LayerRules.";
        }

        return null;
    }
}
