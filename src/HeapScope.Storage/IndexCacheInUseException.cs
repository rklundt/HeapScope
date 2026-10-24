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

namespace HeapScope.Storage;

/// <summary>
/// The index cache could not be replaced because it is open (a mapped graph keeps its file open until disposed).
/// The previous cache is intact and no temporary file is left behind.
/// </summary>
public sealed class IndexCacheInUseException : IOException
{
    public IndexCacheInUseException()
    {
    }

    public IndexCacheInUseException(string message)
        : base(message)
    {
    }

    public IndexCacheInUseException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
