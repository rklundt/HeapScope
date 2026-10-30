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

namespace HeapScope.Contracts;

/// <summary>
/// Result sets: the pages of a large answer, and releasing it. Served by the worker on its one
/// JSON-RPC connection with the other contracts in <see cref="WorkerContracts.All"/>; method names are unique across them
/// (ARCHITECTURE.md 4.4).
/// </summary>
public interface IResultSetService
{
    /// <summary>
    /// Returns rows [offset, offset + count) of a result set; count is capped at
    /// <see cref="WorkerProtocol.MaxPageSize"/>. Unknown, released or evicted sets fail with
    /// <see cref="HeapScopeErrorCode.ResultSetNotFound"/>.
    /// </summary>
    Task<ResultPage> GetRowsAsync(Guid resultSetId, int offset, int count, CancellationToken cancellationToken);

    /// <summary>Frees a result set. Releasing an unknown set is not an error.</summary>
    Task ReleaseResultSetAsync(Guid resultSetId, CancellationToken cancellationToken);
}
