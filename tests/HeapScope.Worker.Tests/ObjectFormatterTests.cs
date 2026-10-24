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
using HeapScope.Contracts;
using HeapScope.Core;
using HeapScope.Worker.Rpc;

namespace HeapScope.Worker.Tests;

/// <summary>Display text for typed values (sprint 1.6): every kind, invariant culture, cuts, enums and flags.</summary>
public sealed class ObjectFormatterTests
{
    private static readonly EnumMember[] s_access = [new("None", 0), new("Read", 1), new("Write", 2), new("Execute", 4), new("All", 7)];

    public static TheoryData<ObjectValue, string> Values() => new()
    {
        { ObjectValue.Null, "null" },
        { new ObjectValue(ObjectValueKind.Boolean) { Boolean = true }, "true" },
        { new ObjectValue(ObjectValueKind.Char) { Char = 'Q' }, "'Q'" },
        { new ObjectValue(ObjectValueKind.Integer) { Integer = -9_000_000_000 }, "-9000000000" },
        { new ObjectValue(ObjectValueKind.Unsigned) { Unsigned = ulong.MaxValue }, "18446744073709551615" },
        { new ObjectValue(ObjectValueKind.Floating) { Floating = 0.1f, TypeName = "System.Single" }, "0.1" },
        { new ObjectValue(ObjectValueKind.Floating) { Floating = 0.25, TypeName = "System.Double" }, "0.25" },
        { new ObjectValue(ObjectValueKind.Pointer) { Unsigned = 0x1f2a }, "0x1f2a" },
        { new ObjectValue(ObjectValueKind.String) { Text = "box", TextLength = 3 }, "\"box\"" },
        { new ObjectValue(ObjectValueKind.String) { Text = "abc", TextLength = 1500 }, "\"abc...\"" },
        { new ObjectValue(ObjectValueKind.Reference) { TypeName = "App.Order", Address = 0x10 }, "App.Order" },
        { new ObjectValue(ObjectValueKind.Array) { ElementTypeName = "System.Int32", Dimensions = [5] }, "int[5]" },
        { new ObjectValue(ObjectValueKind.Array) { ElementTypeName = "System.Int32", Dimensions = [2, 3] }, "int[2,3]" },
        { new ObjectValue(ObjectValueKind.Array) { ElementTypeName = "App.Order", Dimensions = [0] }, "App.Order[0]" },
        { new ObjectValue(ObjectValueKind.Struct) { TypeName = "System.Int64" }, "long" },
        { new ObjectValue(ObjectValueKind.Boxed) { TypeName = "System.Int32", Boxed = new ObjectValue(ObjectValueKind.Integer) { Integer = 12345 } }, "12345 (boxed int)" },
        { new ObjectValue(ObjectValueKind.Invalid) { Address = 0xdead }, "0xdead (not a valid object in this dump)" },
        { new ObjectValue(ObjectValueKind.More) { Unsigned = 1234 }, "1,234 more" },
        { new ObjectValue(ObjectValueKind.Unknown), "?" },
    };

    [Theory]
    [MemberData(nameof(Values))]
    public void Every_kind_formats_in_the_invariant_culture(ObjectValue value, string expected)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
        try
        {
            ObjectFormatter.Format(value).ShouldBe(expected);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData(2UL, "Write (2)")]
    [InlineData(3UL, "Read, Write (3)")]
    [InlineData(7UL, "All (7)")]
    [InlineData(0UL, "None (0)")]
    [InlineData(8UL, "8")]
    [InlineData(9UL, "9")]
    public void Enums_show_their_name_or_the_flags_that_make_them_up(ulong bits, string expected)
    {
        var value = new ObjectValue(ObjectValueKind.Integer) { Integer = (long)bits, EnumMembers = s_access };

        ObjectFormatter.Format(value).ShouldBe(expected);
    }

    [Fact]
    public void Fields_map_to_the_wire_kinds_links_and_type_names()
    {
        var array = new ObjectField("Numbers", "System.Int32[]", new ObjectValue(ObjectValueKind.Array) { TypeName = "System.Int32[]", ElementTypeName = "System.Int32", Dimensions = [2], Address = 0x40 },
            [new ObjectField("[0]", "System.Int32", new ObjectValue(ObjectValueKind.Integer) { Integer = 3 }, [])]);
        var boxed = new ObjectField("Boxed", "System.Object", new ObjectValue(ObjectValueKind.Boxed) { TypeName = "System.Int32", Address = 0x50, Boxed = new ObjectValue(ObjectValueKind.Integer) { Integer = 1 } }, []);
        var number = new ObjectField("Number", "System.Int32", new ObjectValue(ObjectValueKind.Integer) { Integer = 42 }, []);

        var arrayDto = ObjectFormatter.ToDto(array);
        (arrayDto.Kind, arrayDto.Address, arrayDto.TypeName, arrayDto.Value).ShouldBe((ObjectFieldKindDto.Array, 0x40UL, "System.Int32[]", "int[2]"));
        arrayDto.Children.ShouldHaveSingleItem().Value.ShouldBe("3");
        var boxedDto = ObjectFormatter.ToDto(boxed);
        (boxedDto.Kind, boxedDto.Address, boxedDto.TypeName).ShouldBe((ObjectFieldKindDto.Reference, 0x50UL, "System.Int32"), "a box links to its object");
        var numberDto = ObjectFormatter.ToDto(number);
        (numberDto.Kind, numberDto.Address).ShouldBe((ObjectFieldKindDto.Primitive, 0UL));
    }
}
