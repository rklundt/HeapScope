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

/// <summary>Sprint 2.3.5: a path recorded in a snapshot keeps the separators of the system that took it.</summary>
public sealed class RecordedPathTests
{
    [Theory]
    [InlineData(@"C:\app\Plugin.dll", "Plugin.dll")]
    [InlineData("/usr/share/dotnet/shared/Microsoft.NETCore.App/10.0.13/System.Private.CoreLib.dll", "System.Private.CoreLib.dll")]
    [InlineData(@"\\server\share/mixed\Lib.dll", "Lib.dll")]
    [InlineData("Plain.dll", "Plain.dll")]
    [InlineData("", "")]
    public void The_file_name_is_found_on_every_system(string path, string expected) =>
        RecordedPath.FileName(path).ShouldBe(expected);
}
