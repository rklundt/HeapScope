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


namespace HeapScope.Core.Loading;

/// <summary>
/// Which data access component (DAC) read a full dump, and why it was trusted (sprint 2.3.7 review M3): a wrong DAC is
/// the main way a dump is misread, and a download from the symbol server is a privacy-relevant event (ADR-017).
/// </summary>
/// <param name="DumpPath">The dump it reads.</param>
/// <param name="Path">The DAC file loaded; null when ClrMD took it from Microsoft's symbol server.</param>
/// <param name="Rule">Why the file was accepted (for example "own build id", "time stamp and image size").</param>
/// <param name="Candidates">Every file looked at, with why each was or was not used.</param>
public sealed record DacChoice(string DumpPath, string? Path, string Rule, IReadOnlyList<string> Candidates)
{
    /// <summary>True when the DAC came from the symbol server.</summary>
    public bool Downloaded => Path is null;
}
