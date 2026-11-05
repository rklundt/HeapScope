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

namespace HeapScope.Loaders.ClrMd.Tests;

/// <summary>The process name a dump shows: the reader does not list the executable first.</summary>
public class ProcessNameTests
{
    [Theory]
    [InlineData(new[] { @"C:\app\Microsoft.AspNetCore.Http.Results.dll", @"C:\Program Files\dotnet\dotnet.exe", @"C:\app\LeakyApi.dll" }, "dotnet")]
    [InlineData(new[] { @"C:\app\Orders.Api.exe", @"C:\app\Orders.Core.dll" }, "Orders.Api")]
    [InlineData(new[] { "/usr/share/dotnet/dotnet", "/usr/share/dotnet/libcoreclr.so" }, "dotnet")]
    [InlineData(new string[0], null)]
    public void The_process_is_the_executable_wherever_it_is_listed(string[] modules, string? name) =>
        ClrMdDumpLoader.ProcessNameOf(modules).ShouldBe(name);
}
