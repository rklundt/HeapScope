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

using System.Runtime.InteropServices;

namespace HeapScope.Contracts;

/// <summary>
/// What a HeapScope process runs on, for the first line of every log (sprint 2.3.7): a contested result is first traced
/// to the build and the system that produced it.
/// </summary>
public static class RuntimeEnvironment
{
    /// <summary>The operating system, as the runtime describes it.</summary>
    public static string System => RuntimeInformation.OSDescription;

    /// <summary>The process architecture (AnyCPU builds run as the machine's, ADR-025).</summary>
    public static Architecture Architecture => RuntimeInformation.ProcessArchitecture;

    /// <summary>The .NET runtime, for example ".NET 10.0.0".</summary>
    public static string Framework => RuntimeInformation.FrameworkDescription;

    /// <summary>Whether the process carries its own runtime (a published build) rather than an installed one.</summary>
    public static bool SelfContained { get; } = IsSelfContained();

    /// <summary>One line: version with revision, system, architecture, runtime, self-contained.</summary>
    public static string Summary =>
        $"{ProductInfo.Name} {ProductInfo.InformationalVersion}; {System}; {Architecture}; {Framework}; self-contained {SelfContained}";

    private static bool IsSelfContained()
    {
        // A self-contained process loads the runtime library from its own folder; a framework-dependent one from
        // an installed shared runtime.
        var runtime = Path.GetDirectoryName(typeof(object).Assembly.Location);
        return runtime is not null && string.Equals(
            Path.TrimEndingDirectorySeparator(runtime),
            Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }
}
