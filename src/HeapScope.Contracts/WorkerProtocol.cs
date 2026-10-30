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

/// <summary>Protocol constants shared by the worker and its clients.</summary>
public static class WorkerProtocol
{
    /// <summary>Bumped on any incompatible change to <see cref="IWorkerService"/> or its DTOs.</summary>
    /// Version 2 (sprint 1.2): snapshots (probe, open, close, type summary) and the snapshot error codes.
    /// Version 3 (sprint 1.3): type summary queries (grouping, filter, sort, heap kinds), new column kinds and the key flag.
    /// Version 4 (sprint 1.4): type diffs.
    /// Version 5 (sprint 1.5): instances, object details and references; hidden key columns; sort by column name.
    /// Version 6 (sprint 1.6): inspections (list and run), instance scopes and SameValueAs; progress on object and
    /// references calls.
    /// Version 7 (sprint 2.1): referrers of an object and of a type; the descriptor's cache identity.
    /// Version 8 (sprint 2.2): retention paths of an object and of a type.
    /// Version 9 (sprint 2.3): retained sizes (an object's retained size and dominator; retained columns on type summaries
    /// and instances).
    /// Version 10 (sprint 2.3.6): the descriptor says when a capture time is only the file's time.
    /// Version 11 (sprint 2.3.7): an instances result says what its filter could not test; requests carry the client's
    /// trace context (StreamJsonRpc activity tracing).
    /// Version 12 (sprint 2.4): the dominator tree query, exact set retention, columns that say whether their values are
    /// lower bounds. ARCHITECTURE.md 4.4 has the details.
    /// Version 13 (sprint 2.5): allocation traces (open, close, timeline, types, stacks, call tree and back traces), the
    /// estimate value bound and the trace error code.
    /// Version 14 (sprint 2.6): inspection kinds (the leak detectors) and a baseline snapshot on inspection requests.
    /// Version 15 (sprint 2.7): result columns say whether they are hidden and whether they carry values.
    /// Version 16 (sprint 3.1): object sets (a set on instances, type summaries and set retention; a set's dominators).
    /// Version 17 (sprint 3.2): Similar Retention, a set's key paths, interface grouping, interface and retention group sets.
    /// Version 18 (sprint 3.3): object identity by address in diffs (New and Dead columns, the Compared set kind), retained
    /// bytes and per-kind deltas in diffs, and trends across several snapshots.
    /// Version 19 (sprint 3.5): the Unreachable set kind; the async, finalizer backlog and type growth detectors.
    /// Version 20 (sprint 3.6): trusted DACs on open (<see cref="OpenSnapshotRequest.TrustedDacs"/>) and the
    /// <see cref="HeapScopeErrorCode.DacNotTrusted"/> refusal with <see cref="DacTrustDetails"/>.
    public const int Version = 20;

    /// <summary>Maximum rows returned by one <see cref="IWorkerService.GetRowsAsync"/> call.</summary>
    public const int MaxPageSize = 10_000;

    /// <summary>
    /// Prefix of worker pipe names; the rest is 24 random hex digits. Off Windows a pipe is a Unix domain socket in the
    /// temp folder, whose path macOS limits to 104 characters with a long temp folder, so the name stays short (sprint
    /// 2.3.5).
    /// </summary>
    public const string PipeNamePrefix = "HeapScope.W.";
}
