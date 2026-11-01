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
/// Something the worker holds for a client and loses when it restarts: an open snapshot or an allocation trace. The
/// client keeps the worker's id for it and the worker generation that id belongs to, and reopens it through
/// <see cref="WorkerResources{T}"/>.
/// </summary>
internal interface IWorkerResource
{
    /// <summary>The file the worker read.</summary>
    string Path { get; }

    /// <summary>Closed by the client: never reopened.</summary>
    bool IsClosed { get; }

    /// <summary>The worker generation <see cref="WorkerId"/> belongs to.</summary>
    int Generation { get; }

    /// <summary>The worker's id for it in that generation.</summary>
    Guid WorkerId { get; }

    /// <summary>Held while it is reopened, so concurrent callers reopen it once.</summary>
    SemaphoreSlim Reopening { get; }
}
