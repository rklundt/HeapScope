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
/// One public type per file (DEVELOPER.md 4), as a ratchet: <c>PublicTypesPerFile.txt</c> lists the files that held more
/// when the rule got its test (sprint 3.8). No other file may hold more than one top-level public type, a listed file may
/// not grow, and a listed file that is split below two must leave the list. Vendored code is left out.
/// </summary>
public partial class PublicTypesPerFileTests
{
    private const string ListName = "PublicTypesPerFile.txt";

    [Fact]
    public void No_file_holds_more_public_types_than_the_list_allows()
    {
        var allowed = File.ReadLines(Path.Combine(AppContext.BaseDirectory, ListName))
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Select(l => l.Split(' '))
            .ToDictionary(p => p[0], p => int.Parse(p[1], System.Globalization.CultureInfo.InvariantCulture), StringComparer.Ordinal);
        var found = Count();
        var problems = new List<string>();
        foreach (var (file, count) in found)
        {
            var limit = allowed.GetValueOrDefault(file, 1);
            if (count > limit)
            {
                problems.Add($"{file} holds {count} public types (allowed {limit}): give the new type its own file");
            }
        }

        foreach (var (file, limit) in allowed)
        {
            if (found.GetValueOrDefault(file) < 2)
            {
                problems.Add($"{file} holds fewer than two public types now: take it off {ListName}");
            }
            else if (found[file] < limit)
            {
                problems.Add($"{file} holds {found[file]} public types: lower its count in {ListName} to {found[file]}");
            }
        }

        problems.ShouldBeEmpty();
    }

    /// <summary>Top-level public types per source file (file-scoped namespaces put them at the start of a line).</summary>
    internal static Dictionary<string, int> Count()
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(Path.Combine(RepoPaths.Root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(RepoPaths.Root, file).Replace(Path.DirectorySeparatorChar, '/');
            if (relative.Contains("/bin/", StringComparison.Ordinal) || relative.Contains("/obj/", StringComparison.Ordinal) || relative.Contains("ThirdParty", StringComparison.Ordinal))
            {
                continue;
            }

            var count = File.ReadLines(file).Count(l => PublicType().IsMatch(l));
            if (count > 1)
            {
                counts[relative] = count;
            }
        }

        return counts;
    }

    [GeneratedRegex(@"^public\s+(?:(?:sealed|static|abstract|partial|readonly|ref|unsafe|file)\s+)*(?:class|struct|interface|enum|record|delegate)\b")]
    private static partial Regex PublicType();
}
