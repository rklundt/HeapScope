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


using System.Runtime.CompilerServices;

namespace HeapScope.Tests.Shared;

/// <summary>
/// Points every test process, and every worker or front end it starts, at a data folder of its own before anything reads
/// it: nothing a test does reaches the user's sessions, logs, cache key, trusted DACs or bookmarks
/// (<c>HeapScopeFolders.EnvironmentVariable</c>, sprint 3.8).
/// </summary>
internal static class TestDataFolder
{
    public static string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "HeapScopeTests", "data");

#pragma warning disable CA2255 // Test assemblies only: the folder must be set before any test touches HeapScope's folders.
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Set()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("HEAPSCOPE_DATA_FOLDER")))
        {
            Environment.SetEnvironmentVariable("HEAPSCOPE_DATA_FOLDER", Path);
        }
    }
}
