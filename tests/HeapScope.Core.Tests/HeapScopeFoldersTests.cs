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


namespace HeapScope.Core.Tests;

public class HeapScopeFoldersTests
{
    [Fact]
    public void Every_folder_is_under_the_root_it_was_given()
    {
        var root = Path.Combine(Path.GetTempPath(), "hs-folders");
        var folders = new HeapScopeFolders(root);

        folders.Root.ShouldBe(Path.GetFullPath(root));
        new[] { folders.Sessions, folders.Logs, folders.Cache, folders.DacCopies, folders.Etlx }
            .ShouldAllBe(f => f.StartsWith(folders.Root + Path.DirectorySeparatorChar, StringComparison.Ordinal));
        new[] { folders.Sessions, folders.Logs, folders.Cache, folders.DacCopies, folders.Etlx }.Distinct().Count().ShouldBe(5);
    }

    [Fact]
    public void Tests_run_with_a_data_folder_of_their_own()
    {
        // tests/Shared/TestDataFolder.cs: nothing a test does reaches the user's own folders.
        Environment.GetEnvironmentVariable(HeapScopeFolders.EnvironmentVariable).ShouldNotBeNullOrEmpty();
        HeapScopeFolders.Default.Root.ShouldBe(Path.GetFullPath(Environment.GetEnvironmentVariable(HeapScopeFolders.EnvironmentVariable)!));
        var users = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HeapScope");
        HeapScopeFolders.Default.Root.ShouldNotStartWith(users, Case.Insensitive);
    }
}
