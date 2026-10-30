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
/// Error codes carried in the JSON-RPC error code of worker errors (ARCHITECTURE.md 11). They are outside the
/// range JSON-RPC and StreamJsonRpc reserve (-32768 to -32000), and never change.
/// </summary>
public enum HeapScopeErrorCode
{
    Unknown = 1000,
    ProtocolMismatch = 1001,
    ResultSetNotFound = 1002,
    InvalidArgument = 1003,
    CapabilityNotSupported = 1004,
    TestHooksDisabled = 1005,

    /// <summary>The snapshot id is not loaded in this worker (for example after a worker restart).</summary>
    SnapshotNotLoaded = 1006,

    /// <summary>The operation would not fit in the worker's memory budget, or ran out of memory.</summary>
    InsufficientMemory = 1007,

    /// <summary>The snapshot file or index cache is not in a readable format.</summary>
    SnapshotFormat = 1008,

    /// <summary>The data access component matching the dumped runtime is not on this machine (ADR-017).</summary>
    RuntimeComponentMissing = 1009,

    /// <summary>The snapshot file could not be read: in use (still being written), access denied or gone.</summary>
    SnapshotUnavailable = 1010,

    /// <summary>A reopen that requires the index cache found it missing or invalid: the file changed since it was opened.</summary>
    SnapshotChanged = 1011,

    /// <summary>The allocation trace is not open in the worker: closed, or the worker restarted (sprint 2.5).</summary>
    TraceNotLoaded = 1012,

    /// <summary>An allocation trace's file changed since it was opened, so it is not read again silently (the 2.5 review).</summary>
    TraceChanged = 1013,

    /// <summary>
    /// The only matching DAC lies next to the dump and is not trusted (sprint 3.6, peer review P2); the error data is a
    /// <see cref="DacTrustDetails"/>.
    /// </summary>
    DacNotTrusted = 1014,
}
