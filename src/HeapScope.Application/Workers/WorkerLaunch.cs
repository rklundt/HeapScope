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

namespace HeapScope.Application.Workers;

/// <summary>
/// How the worker starts: <paramref name="FileName"/> runs, with <paramref name="Assembly"/> as its first argument when the
/// worker's assembly runs through the dotnet host (sprint 3.9, ADR-039).
/// </summary>
public sealed record WorkerLaunch(string FileName, string? Assembly)
{
    /// <summary>The worker's own file: its executable, or its assembly.</summary>
    public string WorkerFile => Assembly ?? FileName;
}
