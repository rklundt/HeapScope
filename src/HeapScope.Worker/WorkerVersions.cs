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


using System.Globalization;
using HeapScope.Analysis.Inspections;
using HeapScope.Analysis.Retention;
using HeapScope.Contracts;
using HeapScope.Loaders.ClrMd;
using HeapScope.Loaders.GcDump;
using HeapScope.Storage;

namespace HeapScope.Worker;

/// <summary>
/// The versions of every rule and format the worker's results depend on, logged once at start (sprint 2.3.7 review M5):
/// the protocol, the loaders, the index cache format, the retention semantics, the inspections and the ClrMD library.
/// </summary>
internal static class WorkerVersions
{
    public static string Describe() => string.Join(
        "; ",
        [
            $"protocol {WorkerProtocol.Version}",
            $"full dump loader {ClrMdDumpLoader.CurrentVersion}",
            $"gcdump loader {GcDumpLoader.CurrentVersion}",
            $"index cache format {IndexCacheFormat.Version}",
            $"root paths {PathRoots.SemanticsVersion}",
            $"dominator tree {DominatorTree.SemanticsVersion}",
            $"ClrMD {ClrMdDumpLoader.LibraryVersion}",
            "inspections " + string.Join(", ", InspectionCatalog.All.Select(i => string.Create(CultureInfo.InvariantCulture, $"{i.Name} v{i.Version}"))),
        ]);
}
