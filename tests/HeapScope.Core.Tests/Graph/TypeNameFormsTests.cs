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

public class TypeNameFormsTests
{
    [Theory]
    [InlineData("System.String", "System.String")]
    [InlineData("System.Collections.Generic.Dictionary<System.Int32, Fixtures.X>", "System.Collections.Generic.Dictionary<System.Int32, Fixtures.X>")]
    [InlineData("System.Collections.Generic.Dictionary<System.Int32, Fixtures.X>+Entry[]", "Entry<System.Int32, Fixtures.X>[]")]
    [InlineData("System.Collections.Concurrent.ConcurrentDictionary<System.String, Fixtures.R>+Node", "Node<System.String, Fixtures.R>")]
    [InlineData("Fixtures.Leaks.ClosureLeak+<>c__DisplayClass6_0", "<>c__DisplayClass6_0")]
    [InlineData("Program+<<Main>$>d__0", "<<Main>$>d__0")]
    [InlineData("System.Collections.Generic.List<Outer+Inner>", "System.Collections.Generic.List<Outer+Inner>")]
    [InlineData("A<B>+C<D>[,]", "C<B, D>[,]")]
    public void Nested_types_keep_only_their_own_name_with_the_declaring_arguments(string fullName, string gcdumpName) =>
        TypeNameForms.AsGcDumpName(fullName).ShouldBe(gcdumpName);
}
