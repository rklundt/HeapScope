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
    [InlineData(new[] { @"C:\app\ORDERS.EXE", @"C:\app\Plugin.exe" }, "ORDERS")]
    [InlineData(new[] { "/usr/share/dotnet/dotnet", "/usr/share/dotnet/libcoreclr.so" }, "dotnet")]
    [InlineData(new[] { "/usr/lib/libc.so.6", "/app/libcoreclr.so", "/app/Orders.Core.dll", "/app/Orders.Api" }, "Orders.Api")]
    [InlineData(new[] { "/usr/local/share/dotnet/libhostfxr.dylib", "/usr/local/share/dotnet/dotnet" }, "dotnet")]
    [InlineData(new[] { "", "/app/Orders.Api" }, "Orders.Api")]
    [InlineData(new[] { @"C:\app\Orders.Core.dll", "/usr/lib/libc.so.6" }, null)]
    [InlineData(new[] { "" }, null)]
    [InlineData(new string[0], null)]
    public void The_process_is_the_executable_wherever_it_is_listed(string[] modules, string? name) =>
        ClrMdDumpLoader.ProcessNameOf(modules).Name.ShouldBe(name);

    [Theory]
    [InlineData(new[] { @"C:\app\Orders.exe", @"C:\app\Orders.Core.dll" }, "the only .exe of 2 modules")]
    [InlineData(new[] { @"C:\app\Orders.exe", @"C:\app\Plugin.exe", "" }, "the first of 2 .exe modules of 2")]
    [InlineData(new[] { "/app/libcoreclr.so", "/app/Orders.Api" }, "the first of 2 modules that is not a library (no .exe)")]
    [InlineData(new[] { "/app/libcoreclr.so" }, "unknown: no .exe or executable among 1 modules")]
    public void The_rule_that_chose_the_name_is_stated_for_the_log(string[] modules, string source) =>
        ClrMdDumpLoader.ProcessNameOf(modules).Source.ShouldBe(source);
}
