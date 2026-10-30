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

using HeapScope.Core;
using Microsoft.Extensions.Configuration;

namespace HeapScope.Worker.Tests;

public class WorkerOptionsTests
{
    private static WorkerOptions Read(params string[] args)
    {
        var configuration = new ConfigurationBuilder().AddCommandLine(args, WorkerOptions.CreateSwitchMappings()).Build();
        WorkerOptions.TryRead(configuration, enableTestHooks: false, out var options, out var error).ShouldBeTrue(error);
        return options!;
    }

    [Fact]
    public void A_data_folder_redirects_every_folder_the_worker_writes()
    {
        var root = Path.Combine(Path.GetTempPath(), "hs-worker-data");
        var options = Read("--pipe", "p", "--data-folder", root);

        options.DataFolder.ShouldBe(root);
        options.Folders.Root.ShouldBe(Path.GetFullPath(root));
        options.Folders.Cache.ShouldStartWith(Path.GetFullPath(root));
    }

    [Fact]
    public void Without_a_data_folder_the_worker_uses_the_default()
    {
        var options = Read("--pipe", "p");

        options.DataFolder.ShouldBeNull();
        options.Folders.ShouldBeSameAs(HeapScopeFolders.Default);
    }
}
