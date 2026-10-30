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

/// <summary>
/// A path a session names, checked as one of its files: the full path when it is a regular file inside the session
/// folder, reached through no link (and of the expected kind), or why not.
/// </summary>
public sealed record SessionFileCheck(string? Path, SessionFileRefusal? Refusal)
{
    /// <summary>A refusal in words for a status line, naming <paramref name="what"/> (for example "This run's timeline").</summary>
    public string Describe(string what) => Refusal switch
    {
        null => $"{what} is there.",
        SessionFileRefusal.NotRecorded => $"{what} was not recorded.",
        SessionFileRefusal.NotLocal => $"{what} is on another machine; HeapScope opens only local files.",
        SessionFileRefusal.OutsideFolder => $"{what} is outside its session folder, so HeapScope does not open it.",
        SessionFileRefusal.ThroughLink => $"{what} is reached through a link, so HeapScope does not open it.",
        SessionFileRefusal.Missing => $"{what} file is not there any more.",
        SessionFileRefusal.WrongKind => $"{what} is not a CSV file, so HeapScope does not open it.",
        _ => $"{what} cannot be used.",
    };
}
