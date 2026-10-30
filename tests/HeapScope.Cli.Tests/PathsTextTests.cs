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

using HeapScope.Contracts;

namespace HeapScope.Cli.Tests;

/// <summary>Sprint 2.2 review H3: the CLI prints the merged tree depth first, each holder's own holders under it.</summary>
public sealed class PathsTextTests
{
    [Fact]
    public async Task Each_holder_is_followed_by_its_own_holders_closest_root_first()
    {
        var root = new PathRootDto(RootKindDto.StaticField, false, "Static field App.s_a");
        var other = new PathRootDto(RootKindDto.StaticField, false, "Static field App.s_b");
        RetentionNodeDto[] nodes =
        [
            new(0, -1, 0x10, "App.Target", null, false, false, 0, null, 2, 0),
            new(1, 0, 0x20, "App.A", "_target", false, false, 1, root, 1, 0),
            new(2, 0, 0x30, "App.B", "_target", false, false, 1, null, 1, 3),
            new(3, 2, 0x40, "App.Holder", "_b", false, false, 2, other, 1, 0),
            new(4, 3, 0, "(5 objects not shown)", null, false, false, 3, null, 1, 5),
        ];
        using var output = new StringWriter();

        await CliCommands.WriteHoldersAsync(output, nodes.Skip(1).ToLookup(n => n.ChildId), 0, 1, fold: true);

        output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries).ShouldBe(
        [
            "  <- _target of 0x20  App.A   ROOT: Static field App.s_a",
            "  <- _target of 0x30  App.B (and 3 more of this type)",
            "    <- _b of 0x40  App.Holder   ROOT: Static field App.s_b",
            "      <- (5 objects not shown)",
        ]);
    }
}
