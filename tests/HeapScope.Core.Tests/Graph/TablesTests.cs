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

using HeapScope.Core.Graph;

namespace HeapScope.Core.Tests.Graph;

public class TablesTests
{
    [Fact]
    public void Types_are_interned_by_method_table_or_by_name_when_it_is_zero()
    {
        var strings = new StringTableBuilder();
        var types = new TypeTableBuilder(strings);

        var a = types.GetOrAdd("List<A>", methodTable: 0x100);
        var sameMt = types.GetOrAdd("List<A>", methodTable: 0x100);
        var otherMtSameName = types.GetOrAdd("List<A>", methodTable: 0x200);
        var synthetic = types.GetOrAdd("Synthetic");
        var syntheticAgain = types.GetOrAdd("Synthetic");

        sameMt.ShouldBe(a);
        otherMtSameName.ShouldNotBe(a);
        syntheticAgain.ShouldBe(synthetic);

        var table = types.Build(strings.Build());
        table.Count.ShouldBe(3);
        table.GetName(otherMtSameName).ShouldBe("List<A>");
        table.NameIds[a].ShouldBe(table.NameIds[otherMtSameName]);
    }

    [Fact]
    public void Type_table_keeps_sizes_module_and_kind()
    {
        var strings = new StringTableBuilder();
        var types = new TypeTableBuilder(strings);
        var index = types.GetOrAdd("System.String", 0x10, baseSize: 22, componentSize: 2, module: "System.Private.CoreLib.dll", kind: TypeKind.String);

        var table = types.Build(strings.Build());

        table.BaseSizes[index].ShouldBe(22u);
        table.ComponentSizes[index].ShouldBe(2u);
        table.GetModule(index).ShouldBe("System.Private.CoreLib.dll");
        table.Kinds[index].ShouldBe(TypeKind.String);
        table.MethodTables[index].ShouldBe(0x10UL);
    }

    [Fact]
    public void String_table_interns_and_maps_null_to_no_string()
    {
        var builder = new StringTableBuilder();

        builder.GetOrAdd("x").ShouldBe(builder.GetOrAdd("x"));
        builder.GetOrAdd(null).ShouldBe(StringTable.NoString);

        var table = builder.Build();
        table.Count.ShouldBe(1);
        table[StringTable.NoString].ShouldBeNull();
        table[0].ShouldBe("x");
    }

    [Fact]
    public void Root_table_returns_empty_for_unrooted_objects()
    {
        var table = new RootTable(Column<GcRoot>.FromArray([new GcRoot(2, RootKind.StaticField, RootFlags.Retaining, -1)]));

        table.GetRoots(1).Length.ShouldBe(0);
        table.GetRoots(3).Length.ShouldBe(0);
        table.GetRoots(2).Length.ShouldBe(1);
        RootTable.Empty.GetRoots(0).Length.ShouldBe(0);
    }

    [Fact]
    public void Chunked_buffer_spans_chunk_boundaries()
    {
        var buffer = new ChunkedBuffer<int>(chunkLength: 3);
        for (var i = 0; i < 10; i++)
        {
            buffer.Add(i);
        }

        var copy = new int[10];
        buffer.CopyTo(copy);

        buffer.Count.ShouldBe(10);
        buffer[7].ShouldBe(7);
        copy.ShouldBe(Enumerable.Range(0, 10).ToArray());
        buffer.Clear();
        buffer.Count.ShouldBe(0);
    }

    [Fact]
    public void Graph_from_columns_rejects_inconsistent_shapes()
    {
        var columns = new HeapGraphColumns(
            Column<ulong>.FromArray([0x1000]),
            Column<int>.FromArray([0]),
            Column<uint>.FromArray([24]),
            Column<HeapKind>.FromArray([HeapKind.Gen0]),
            Column<ObjectFlags>.FromArray([ObjectFlags.None]),
            Column<int>.FromArray([0]),
            Column<int>.Empty,
            Column<LargeObjectSize>.Empty);
        var strings = StringTable.Empty;
        var types = new TypeTableBuilder(new StringTableBuilder()).Build(strings);

        Should.Throw<ArgumentException>(() => HeapGraph.FromColumns(columns, types, RootTable.Empty, strings, 0, 0, null));
        Should.Throw<ArgumentException>(() => HeapGraph.FromColumns(
            columns with { EdgeStarts = Column<int>.FromArray([0, 1]) }, types, RootTable.Empty, strings, 0, 0, null));
        Should.NotThrow(() => HeapGraph.FromColumns(
            columns with { EdgeStarts = Column<int>.FromArray([0, 0]) }, types, RootTable.Empty, strings, 0, 0, null));
    }
}
