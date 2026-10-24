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

using System.Text.Json;
using System.Xml.Linq;

namespace HeapScope.Architecture.Tests;

/// <summary>The references of one project.</summary>
internal sealed record ProjectInfo(
    string Name,
    IReadOnlyList<string> ProjectReferences,
    IReadOnlyList<string> BuildOrderOnlyReferences,
    IReadOnlyList<string> PackageReferences)
{
    /// <summary>
    /// Loads the evaluated references from NuGet's restore output (<c>obj/project.assets.json</c>), which includes
    /// references added by imported props and targets files. Build-order-only references
    /// (<c>ReferenceOutputAssembly="false"</c>) are not part of the restore graph, so they are read from the
    /// project file itself and from the shared targets in <c>build/</c> that the project imports.
    /// </summary>
    public static ProjectInfo LoadEvaluated(string csprojPath)
    {
        var name = Path.GetFileNameWithoutExtension(csprojPath);
        var assetsPath = Path.Combine(Path.GetDirectoryName(csprojPath)!, "obj", "project.assets.json");
        if (!File.Exists(assetsPath))
        {
            throw new InvalidOperationException($"{assetsPath} not found; build the solution before running architecture tests.");
        }

        using var assets = JsonDocument.Parse(File.ReadAllText(assetsPath));
        var project = assets.RootElement.GetProperty("project");
        var packages = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var framework in project.GetProperty("frameworks").EnumerateObject())
        {
            if (framework.Value.TryGetProperty("dependencies", out var dependencies))
            {
                foreach (var dependency in dependencies.EnumerateObject())
                {
                    packages.Add(dependency.Name);
                }
            }
        }

        var projects = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var framework in project.GetProperty("restore").GetProperty("frameworks").EnumerateObject())
        {
            if (framework.Value.TryGetProperty("projectReferences", out var references))
            {
                foreach (var reference in references.EnumerateObject())
                {
                    projects.Add(Path.GetFileNameWithoutExtension(reference.Name.Replace('\\', '/')));
                }
            }
        }

        var buildOrderOnly = ReadBuildOrderOnly(XDocument.Load(csprojPath), Path.GetDirectoryName(csprojPath)!);
        return new ProjectInfo(name, [.. projects], buildOrderOnly, [.. packages]);
    }

    /// <summary>Parses the references written literally in a project file (used for negative samples).</summary>
    public static ProjectInfo Parse(string name, XDocument document)
    {
        var projectReferences = new List<string>();
        foreach (var element in document.Descendants("ProjectReference"))
        {
            if (!IsBuildOrderOnly(element))
            {
                projectReferences.Add(ReferencedName(element));
            }
        }

        var packages = document.Descendants("PackageReference")
            .Select(e => (string?)e.Attribute("Include"))
            .Where(include => !string.IsNullOrEmpty(include))
            .Select(include => include!)
            .ToList();
        return new ProjectInfo(name, projectReferences, ReadBuildOrderOnly(document, projectDirectory: null), packages);
    }

    private static List<string> ReadBuildOrderOnly(XDocument document, string? projectDirectory)
    {
        var result = document.Descendants("ProjectReference")
            .Where(IsBuildOrderOnly)
            .Select(ReferencedName)
            .ToList();

        if (projectDirectory is null)
        {
            return result;
        }

        // Follow imports of the repository's shared targets (for example build/Worker.Deploy.targets).
        foreach (var import in document.Descendants("Import"))
        {
            var target = ((string?)import.Attribute("Project") ?? string.Empty)
                .Replace("$(HeapScopeRoot)", RepoPaths.Root + Path.DirectorySeparatorChar, StringComparison.Ordinal);
            var path = Path.GetFullPath(Path.Combine(projectDirectory, target));
            if (File.Exists(path))
            {
                result.AddRange(XDocument.Load(path).Descendants("ProjectReference").Where(IsBuildOrderOnly).Select(ReferencedName));
            }
        }

        return result;
    }

    private static bool IsBuildOrderOnly(XElement element)
    {
        var referenceOutput = (string?)element.Attribute("ReferenceOutputAssembly")
            ?? (string?)element.Element("ReferenceOutputAssembly");
        return string.Equals(referenceOutput, "false", StringComparison.OrdinalIgnoreCase);
    }

    private static string ReferencedName(XElement element)
    {
        var include = (string?)element.Attribute("Include") ?? string.Empty;
        return Path.GetFileNameWithoutExtension(include.Replace('\\', '/').Replace("$(_HeapScopeWorkerProject)", "HeapScope.Worker.csproj", StringComparison.Ordinal));
    }
}
