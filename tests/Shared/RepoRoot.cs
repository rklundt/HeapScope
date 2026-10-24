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

namespace HeapScope.Tests.Shared;

/// <summary>
/// The repository root, found from a test's output folder by the solution file (sprint 1.9: one copy, compiled into
/// every test project by tests/Directory.Build.props).
/// </summary>
internal static class RepoRoot
{
    public static string Path { get; } = Find();

    private static string Find()
    {
        var directory = new System.IO.DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !System.IO.File.Exists(System.IO.Path.Combine(directory.FullName, "HeapScope.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("HeapScope.sln not found above the test output folder.");
    }
}
