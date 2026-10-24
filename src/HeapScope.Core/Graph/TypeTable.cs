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

/// <summary>The interned types of a graph, as columns indexed by type index (ARCHITECTURE.md 6.1).</summary>
public sealed class TypeTable
{
    public TypeTable(
        Column<int> nameIds,
        Column<ulong> methodTables,
        Column<uint> baseSizes,
        Column<uint> componentSizes,
        Column<int> moduleIds,
        Column<TypeKind> kinds,
        StringTable strings)
    {
        var count = nameIds.Length;
        if (methodTables.Length != count || baseSizes.Length != count || componentSizes.Length != count
            || moduleIds.Length != count || kinds.Length != count)
        {
            throw new ArgumentException("All type table columns must have the same length.");
        }

        NameIds = nameIds;
        MethodTables = methodTables;
        BaseSizes = baseSizes;
        ComponentSizes = componentSizes;
        ModuleIds = moduleIds;
        Kinds = kinds;
        Strings = strings ?? throw new ArgumentNullException(nameof(strings));
    }

    public int Count => NameIds.Length;

    public Column<int> NameIds { get; }

    public Column<ulong> MethodTables { get; }

    public Column<uint> BaseSizes { get; }

    public Column<uint> ComponentSizes { get; }

    public Column<int> ModuleIds { get; }

    public Column<TypeKind> Kinds { get; }

    public StringTable Strings { get; }

    public string GetName(int typeIndex) => Strings[NameIds[typeIndex]] ?? string.Empty;

    public string? GetModule(int typeIndex) => Strings[ModuleIds[typeIndex]];
}

/// <summary>
/// Builds a <see cref="TypeTable"/>. Types are interned by method table, or by name when the method table is
/// zero (synthetic graphs).
/// </summary>
public sealed class TypeTableBuilder(StringTableBuilder strings)
{
    private readonly StringTableBuilder _strings = strings ?? throw new ArgumentNullException(nameof(strings));
    private readonly Dictionary<ulong, int> _byMethodTable = [];
    // Types without a method table (synthetic graphs, gcdumps) are told apart by name and module (sprint 1.8): two
    // assemblies may define the same type name.
    private readonly Dictionary<(string Name, string? Module), int> _byName = [];
    private readonly List<int> _nameIds = [];
    private readonly List<ulong> _methodTables = [];
    private readonly List<uint> _baseSizes = [];
    private readonly List<uint> _componentSizes = [];
    private readonly List<int> _moduleIds = [];
    private readonly List<TypeKind> _kinds = [];

    public int Count => _nameIds.Count;

    /// <summary>Returns the index of the type, adding it when new.</summary>
    public int GetOrAdd(
        string name,
        ulong methodTable = 0,
        uint baseSize = 0,
        uint componentSize = 0,
        string? module = null,
        TypeKind kind = TypeKind.None)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (methodTable != 0 ? _byMethodTable.TryGetValue(methodTable, out var existing) : _byName.TryGetValue((name, module), out existing))
        {
            return existing;
        }

        var index = _nameIds.Count;
        _nameIds.Add(_strings.GetOrAdd(name));
        _methodTables.Add(methodTable);
        _baseSizes.Add(baseSize);
        _componentSizes.Add(componentSize);
        _moduleIds.Add(_strings.GetOrAdd(module));
        _kinds.Add(kind);
        if (methodTable != 0)
        {
            _byMethodTable.Add(methodTable, index);
        }
        else
        {
            _byName.Add((name, module), index);
        }

        return index;
    }

    /// <summary>Builds the table; <paramref name="strings"/> must be the built form of the shared string builder.</summary>
    public TypeTable Build(StringTable strings) => new(
        Column<int>.FromArray([.. _nameIds]),
        Column<ulong>.FromArray([.. _methodTables]),
        Column<uint>.FromArray([.. _baseSizes]),
        Column<uint>.FromArray([.. _componentSizes]),
        Column<int>.FromArray([.. _moduleIds]),
        Column<TypeKind>.FromArray([.. _kinds]),
        strings);
}
