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

using HeapScope.Analysis.Inspections;

namespace HeapScope.Analysis.Tests;

/// <summary>One rule for framework types and one tally per type (sprint 3.8, peer review P19).</summary>
public class FrameworkTypesTests
{
    [Theory]
    [InlineData("System.String", null, true)]
    [InlineData("Microsoft.Extensions.Logging.Logger", null, true)]
    [InlineData("Internal.Runtime.CompilerHelpers.ThrowHelpers", null, true)]
    [InlineData("MyApp.Orders.Cache", null, false)]
    [InlineData("SystemX.Foo", null, false)]
    [InlineData("MyApp.Orders.Cache", "System.Private.CoreLib.dll", true)]
    [InlineData("MyApp.Orders.Cache", "microsoft.extensions.di.dll", true)]
    [InlineData("MyApp.Orders.Cache", "netstandard.dll", true)]
    [InlineData("MyApp.Orders.Cache", "MyApp.dll", false)]
    public void A_type_is_the_frameworks_by_its_namespace_or_its_module(string name, string? module, bool framework)
    {
        FrameworkTypes.IsFramework(name, module).ShouldBe(framework);
        if (module is null)
        {
            FrameworkTypes.IsFramework(name).ShouldBe(framework);
        }
    }

    [Theory]
    [InlineData("Internal.Orders.Cache", "MyApp.dll", false)]
    [InlineData("Internal.Runtime.Helpers", "System.Private.CoreLib.dll", true)]
    [InlineData("Internal.Runtime.Helpers", null, true)]
    [InlineData("Interop+Kernel32", "System.Private.CoreLib.dll", true)]
    [InlineData("MyApp.Orders.Cache", "MyApp.dll", false)]
    [InlineData("System.Collections.Generic.List<T>", "MyApp.dll", true)]
    public void A_retention_step_folds_an_Internal_type_only_from_a_framework_module(string name, string? module, bool fold)
    {
        // 3.8 review: an application may have an Internal namespace of its own; its steps stay visible.
        FrameworkTypes.IsFrameworkStep(name, module).ShouldBe(fold);
    }

    [Fact]
    public void A_module_given_as_a_path_is_matched_by_its_file_name()
    {
        FrameworkTypes.IsFramework("MyApp.Thing", Path.Combine("usr", "share", "dotnet", "System.Private.CoreLib.dll")).ShouldBeTrue();
        FrameworkTypes.IsFrameworkModule(Path.Combine("app", "MyApp.dll")).ShouldBeFalse();
    }

    [Fact]
    public void A_tally_counts_objects_bytes_and_members_in_the_order_added()
    {
        var tally = new TypeTally();
        tally.Add(7, 10, 24);
        tally.Add(3, 11, 100);
        tally.Add(7, 12, 40);

        tally.Count.ShouldBe(2);
        var seven = tally.Types.Single(t => t.Type == 7);
        (seven.Objects, seven.Bytes).ShouldBe((2L, 64L));
        seven.Members.ShouldBe([10, 12]);
        tally.Types.Select(t => t.Type).ShouldBe([7, 3], "types in the order first seen");
    }
}
