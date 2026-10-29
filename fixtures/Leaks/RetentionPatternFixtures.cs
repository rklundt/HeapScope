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

namespace Fixtures.Leaks;

/// <summary>
/// Objects held by known distinct retention patterns (sprint 3.2, Similar Retention): per item, three
/// <see cref="PatternItem"/>s only in a list, two in the list and in a dictionary, and one held by a holder in an array.
/// </summary>
public static class RetentionPatternFixtures
{
    private static readonly List<PatternItem> s_listed = [];
    private static readonly Dictionary<int, PatternItem> s_indexed = [];
    private static PatternHolder[] s_holders = [];

    /// <summary>Creates the objects of <paramref name="count"/> items.</summary>
    public static void Create(int count)
    {
        var holders = new PatternHolder[count];
        for (var i = 0; i < count; i++)
        {
            for (var j = 0; j < 3; j++)
            {
                s_listed.Add(new PatternItem((i * 6) + j));
            }

            for (var j = 3; j < 5; j++)
            {
                var item = new PatternItem((i * 6) + j);
                s_listed.Add(item);
                s_indexed.Add(item.Id, item);
            }

            holders[i] = new PatternHolder(new PatternItem((i * 6) + 5));
        }

        s_holders = holders;
    }

    public static int ItemCount => s_holders.Length;
}

public sealed class PatternItem(int id)
{
    public int Id { get; } = id;
}

public sealed class PatternHolder(PatternItem item)
{
    public PatternItem Item { get; } = item;
}
