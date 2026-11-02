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

namespace HeapScope.Application.Tests;

/// <summary>3.9 review M12: every log header says which build form ran and from where.</summary>
public sealed class RuntimeEnvironmentTests
{
    [Fact]
    public void The_build_form_tells_a_release_archive_from_the_dotnet_tool_and_a_source_build()
    {
        var tool = Path.Combine(Path.GetTempPath(), ".dotnet", "tools", ".store", "heapscope.cli", "3.9.0", "heapscope.cli", "3.9.0", "tools", "net10.0", "any");

        RuntimeEnvironment.DescribeBuild(selfContained: true, tool).ShouldBe("self-contained build");
        RuntimeEnvironment.DescribeBuild(selfContained: false, tool).ShouldBe("dotnet tool");
        RuntimeEnvironment.DescribeBuild(selfContained: false, Path.Combine(tool, "worker")).ShouldBe("dotnet tool");
        RuntimeEnvironment.DescribeBuild(selfContained: false, Path.Combine(Path.GetTempPath(), "src", "bin", "Debug")).ShouldBe("framework-dependent build");
    }

    [Fact]
    public void The_summary_names_the_build_form_and_the_folder()
    {
        RuntimeEnvironment.Summary.ShouldContain("; framework-dependent build; folder " + AppContext.BaseDirectory);
    }
}
