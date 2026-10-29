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

namespace HeapScope.Integration.Tests.Infrastructure;

internal static class TestPaths
{
#if DEBUG
    public const string Configuration = "Debug";
#else
    public const string Configuration = "Release";
#endif

    public static string RepoRoot => global::HeapScope.Tests.Shared.RepoRoot.Path;

    public static string WorkerExe => Path.Combine(AppContext.BaseDirectory, "worker", "HeapScope.Worker.exe");

    public static string CliOutput => Path.Combine(RepoRoot, "src", "HeapScope.Cli", "bin", Configuration, "net10.0");

    public static string CliExe => Path.Combine(CliOutput, "heapscope-cli.exe");

    public static string UiOutput => Path.Combine(RepoRoot, "src", "HeapScope.UI", "bin", Configuration, "net10.0");

    public static string UiExe => Path.Combine(UiOutput, "HeapScope.exe");

    public static string LeakyConsoleExe => Path.Combine(RepoRoot, "fixtures", "LeakyConsole", "bin", Configuration, "net10.0", "LeakyConsole.exe");

    public static string LeakyApiExe => Path.Combine(RepoRoot, "fixtures", "LeakyApi", "bin", Configuration, "net10.0", "LeakyApi.exe");

    public static string LoadGenExe => Path.Combine(RepoRoot, "fixtures", "LoadGen", "bin", Configuration, "net10.0", "LoadGen.exe");

    public static string DenseHeapExe => Path.Combine(RepoRoot, "fixtures", "DenseHeap", "bin", Configuration, "net10.0", "DenseHeap.exe");

    public static string KnownAnswers => Path.Combine(RepoRoot, "fixtures", "KnownAnswers");
}
