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

using System.Globalization;
using HeapScope.Analysis.Retention;
using HeapScope.Core;
using HeapScope.Core.Graph;

namespace HeapScope.Analysis.Inspections;

/// <summary>
/// What a detector left out, by reason, with the most common keys for each (sprint 2.6 re-review): a disputed absence
/// ("why was my event not reported?") is answered from the log by name, not only by a count.
/// </summary>
internal sealed class Exclusions
{
    private readonly List<string> _order = [];
    private readonly Dictionary<string, Dictionary<string, long>> _byReason = new(StringComparer.Ordinal);

    public void Add(string reason, string key)
    {
        if (!_byReason.TryGetValue(reason, out var keys))
        {
            keys = new Dictionary<string, long>(StringComparer.Ordinal);
            _byReason[reason] = keys;
            _order.Add(reason);
        }

        keys[key] = keys.GetValueOrDefault(key) + 1;
    }

    /// <summary>"left out: reason count (Key (n), Key (n), Key (n)), ..."; "none left out" when empty.</summary>
    public override string ToString() =>
        _order.Count == 0
            ? "none left out"
            : "left out: " + string.Join(", ", _order.Select(r => string.Create(CultureInfo.InvariantCulture, $"{r} {_byReason[r].Values.Sum()} ({DetectorGraph.Top(_byReason[r])})")));
}
