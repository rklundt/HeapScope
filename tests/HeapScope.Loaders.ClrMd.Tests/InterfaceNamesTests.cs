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

using HeapScope.Loaders.ClrMd;

namespace HeapScope.Loaders.ClrMd.Tests;

/// <summary>Sprint 3.2 re-review: the generic arguments a type's interfaces are decoded with.</summary>
public sealed class InterfaceNamesTests
{
    [Theory]
    [InlineData("App.Plain", new string[0])]
    [InlineData("System.Collections.Generic.List<System.String>", new[] { "System.String" })]
    [InlineData("System.Collections.Generic.Dictionary<System.String, System.Collections.Generic.List<System.Int32>>", new[] { "System.String", "System.Collections.Generic.List<System.Int32>" })]
    [InlineData("System.Collections.Generic.Dictionary<System.String, System.Int32>+KeyCollection", new[] { "System.String", "System.Int32" })]
    [InlineData("App.Outer<App.A>+Inner<App.B[], App.C>", new[] { "App.A", "App.B[]", "App.C" })]
    public void Generic_arguments_are_taken_from_every_generic_segment_in_order(string name, string[] expected) =>
        InterfaceNames.GenericArguments(name).ShouldBe(expected);
}
