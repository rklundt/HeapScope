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

namespace HeapScope.Core.Graph;

/// <summary>
/// Interned strings for type names, module names and root details. Never holds snapshot field values or string
/// contents (ARCHITECTURE.md 12). Id -1 means "no string".
/// </summary>
public sealed class StringTable
{
    public const int NoString = -1;

    private readonly string[] _strings;

    public StringTable(string[] strings)
    {
        _strings = strings ?? throw new ArgumentNullException(nameof(strings));
    }

    public static StringTable Empty { get; } = new([]);

    public int Count => _strings.Length;

    /// <summary>The string with this id, or null for <see cref="NoString"/>.</summary>
    public string? this[int id] => id == NoString ? null : _strings[id];

    /// <summary>All strings in id order (for persistence).</summary>
    public IReadOnlyList<string> Strings => _strings;
}

/// <summary>Builds a <see cref="StringTable"/>, interning equal strings to one id.</summary>
public sealed class StringTableBuilder
{
    private readonly Dictionary<string, int> _ids = new(StringComparer.Ordinal);
    private readonly List<string> _strings = [];

    public int Count => _strings.Count;

    /// <summary>Returns the id of <paramref name="value"/>, adding it when new; <see cref="StringTable.NoString"/> for null.</summary>
    public int GetOrAdd(string? value)
    {
        if (value is null)
        {
            return StringTable.NoString;
        }

        if (!_ids.TryGetValue(value, out var id))
        {
            id = _strings.Count;
            _strings.Add(value);
            _ids.Add(value, id);
        }

        return id;
    }

    public StringTable Build() => new([.. _strings]);
}
