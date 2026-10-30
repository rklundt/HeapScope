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
/// Test hooks: refused with <see cref="HeapScopeErrorCode.TestHooksDisabled"/> unless the worker was started with them. Served by the worker on its one
/// JSON-RPC connection with the other contracts in <see cref="WorkerContracts.All"/>; method names are unique across them
/// (ARCHITECTURE.md 4.4).
/// </summary>
public interface IWorkerTestHooks
{
    /// <summary>Test hook: terminates the worker abruptly. Needs <c>--enable-test-hooks</c>.</summary>
    Task CrashForTestingAsync(CancellationToken cancellationToken);

    /// <summary>Test hook: waits until canceled. Needs <c>--enable-test-hooks</c>.</summary>
    Task HangForTestingAsync(IProgress<JobProgress>? progress, CancellationToken cancellationToken);
}
