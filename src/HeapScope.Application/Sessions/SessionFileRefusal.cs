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

namespace HeapScope.Application.Sessions;

/// <summary>Why a path a session names is not used as one of its files (sprint 3.8, 3.7 review M2).</summary>
public enum SessionFileRefusal
{
    /// <summary>The session records no such file.</summary>
    NotRecorded,

    /// <summary>A network or device path: never looked at.</summary>
    NotLocal,

    /// <summary>Outside the session folder.</summary>
    OutsideFolder,

    /// <summary>Reached through a link (a folder or the file itself).</summary>
    ThroughLink,

    /// <summary>Not there any more.</summary>
    Missing,

    /// <summary>Not the kind of file expected (a timeline must be a <c>.csv</c> file).</summary>
    WrongKind,
}
