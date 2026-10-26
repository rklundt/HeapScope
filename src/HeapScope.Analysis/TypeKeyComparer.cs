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


namespace HeapScope.Analysis;

/// <summary>
/// Compares type keys (ADR-018): a type name and its module's file name (<see cref="TypeGroups.ModuleKey"/>). Names are
/// compared exactly; module file names in any case, as the summary groups them and the "open instances" link matches them,
/// so the summary, the diff and the inspections agree (2.3.5 review L2, 2.3.6 review).
/// </summary>
public sealed class TypeKeyComparer : IEqualityComparer<(string Name, string Module)>
{
    /// <summary>Type rows: the name exactly, the module in any case.</summary>
    public static readonly TypeKeyComparer Type = new(StringComparer.Ordinal);

    /// <summary>Assembly rows, whose name is the module's file name: both in any case.</summary>
    public static readonly TypeKeyComparer Assembly = new(StringComparer.OrdinalIgnoreCase);

    private readonly StringComparer _names;

    private TypeKeyComparer(StringComparer names) => _names = names;

    public bool Equals((string Name, string Module) x, (string Name, string Module) y) =>
        _names.Equals(x.Name, y.Name) && StringComparer.OrdinalIgnoreCase.Equals(x.Module, y.Module);

    public int GetHashCode((string Name, string Module) obj) =>
        HashCode.Combine(_names.GetHashCode(obj.Name), StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Module));
}
