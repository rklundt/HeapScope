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


using System.Text.RegularExpressions;

namespace HeapScope.Architecture.Tests;

/// <summary>
/// Every relative link in the repository's Markdown resolves to a file or folder: the docs are split into pages (an
/// overview, topic pages, one file per ADR), so a moved or renamed page must not leave a dead link behind.
/// </summary>
public partial class DocLinksTests
{
    [Fact]
    public void Every_relative_link_in_markdown_resolves()
    {
        var dead = new List<string>();
        foreach (var file in Directory.EnumerateFiles(RepoPaths.Root, "*.md", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(RepoPaths.Root, file);
            if (relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(p => p is "bin" or "obj" or ".git" or "artifacts" or ".vs" or "TestResults" or "node_modules"))
            {
                continue;
            }

            var inCode = false;
            foreach (var line in File.ReadLines(file))
            {
                if (line.TrimStart().StartsWith("```", StringComparison.Ordinal))
                {
                    inCode = !inCode;
                    continue;
                }

                if (inCode)
                {
                    continue;
                }

                foreach (Match link in Link().Matches(line))
                {
                    var target = link.Groups["target"].Value;
                    if (target.Contains("://", StringComparison.Ordinal) || target.StartsWith('#') || target.StartsWith("mailto:", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var path = target.Split('#')[0];
                    var resolved = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, Uri.UnescapeDataString(path)));
                    if (!File.Exists(resolved) && !Directory.Exists(resolved))
                    {
                        dead.Add($"{relative}: {target}");
                    }
                }
            }
        }

        dead.ShouldBeEmpty();
    }

    [GeneratedRegex(@"\]\((?<target>[^)\s]+)\)")]
    private static partial Regex Link();
}
