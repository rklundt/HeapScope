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

public class TextRulesTests
{
    private const char EnDash = (char)0x2013;
    private const char EmDash = (char)0x2014;

    private static readonly string[] s_extensions =
        [".cs", ".md", ".xaml", ".axaml", ".csproj", ".props", ".targets", ".json", ".ps1", ".yml", ".editorconfig", ".gitignore"];

    [Fact]
    public void No_em_or_en_dashes_in_repository_text()
    {
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(RepoPaths.Root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(RepoPaths.Root, file);
            if (IsIgnored(relative) || !IsText(file))
            {
                continue;
            }

            var text = File.ReadAllText(file);
            if (text.Contains(EnDash, StringComparison.Ordinal) || text.Contains(EmDash, StringComparison.Ordinal))
            {
                offenders.Add(relative);
            }
        }

        offenders.ShouldBeEmpty();
    }

    // HeapScope's data folders are named in one place, so one setting (HEAPSCOPE_DATA_FOLDER, --data-folder) moves them
    // all, and tests never write into the user's profile (sprint 3.8).
    [Fact]
    public void Only_HeapScopeFolders_names_the_local_application_data_folder()
    {
        var offenders = Directory.EnumerateFiles(Path.Combine(RepoPaths.Root, "src"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !IsIgnored(Path.GetRelativePath(RepoPaths.Root, f)) && Path.GetFileName(f) != "HeapScopeFolders.cs")
            .Where(f => File.ReadAllText(f).Contains("SpecialFolder.LocalApplicationData", StringComparison.Ordinal))
            .Select(f => Path.GetRelativePath(RepoPaths.Root, f))
            .ToList();

        offenders.ShouldBeEmpty();
    }

    // Every source file we own starts with the license header of docs/licensing/SOURCE-HEADER.txt, in its own comment
    // syntax (LICENSE, ADDITIONAL-TERMS.md). Vendored code (a ThirdParty project or folder) keeps its own header.
    [Fact]
    public void Every_source_file_starts_with_the_license_header()
    {
        var header = HeaderLines(File.ReadLines(Path.Combine(RepoPaths.Root, "docs", "licensing", "SOURCE-HEADER.txt")));
        header.ShouldContain("Copyright (C) 2026 Ray Klundt");
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(RepoPaths.Root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(RepoPaths.Root, file);
            if (IsIgnored(relative) || relative.Split(Path.DirectorySeparatorChar).Any(p => p.Contains("ThirdParty", StringComparison.Ordinal))
                || Path.GetExtension(file) is not (".cs" or ".xaml" or ".axaml" or ".ps1"))
            {
                continue;
            }

            var lines = File.ReadLines(file).Where(l => !l.StartsWith("<?xml", StringComparison.Ordinal)).Take(header.Count * 2);
            var top = HeaderLines(lines).Take(header.Count);
            if (!top.SequenceEqual(header, StringComparer.Ordinal))
            {
                offenders.Add(relative);
            }
        }

        offenders.ShouldBeEmpty();
    }

    // The non-empty text of a comment block, without its comment markers.
    private static List<string> HeaderLines(IEnumerable<string> lines) =>
        [.. lines
            .Select(l => l.Trim())
            .Select(l => l switch
            {
                _ when l.StartsWith("//", StringComparison.Ordinal) => l[2..],
                _ when l.StartsWith("<!--", StringComparison.Ordinal) || l.StartsWith("/*", StringComparison.Ordinal) => l[(l.StartsWith('<') ? 4 : 2)..],
                _ when l.StartsWith("-->", StringComparison.Ordinal) || l.StartsWith("*/", StringComparison.Ordinal) => string.Empty,
                _ when l.StartsWith('*') || l.StartsWith('#') => l[1..],
                _ => l,
            })
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)];

    private static bool IsText(string file) =>
        s_extensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)
        || s_extensions.Contains(Path.GetFileName(file), StringComparer.OrdinalIgnoreCase);

    private static bool IsIgnored(string relative)
    {
        var parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return parts.Any(p => p is "bin" or "obj" or ".git" or "artifacts" or ".vs" or "TestResults");
    }
}
