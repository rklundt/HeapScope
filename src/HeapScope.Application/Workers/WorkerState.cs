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

/// <summary>Lifecycle state of the analysis worker (ARCHITECTURE.md 4.2, 4.3).</summary>
public enum WorkerState
{
    NotStarted,
    Starting,
    Running,
    Crashed,
    Stopped,
}

/// <summary>Raised when the worker changes state; <see cref="Message"/> explains crashes.</summary>
public sealed class WorkerStateChangedEventArgs(WorkerState state, string? message) : EventArgs
{
    public WorkerState State { get; } = state;

    public string? Message { get; } = message;
}
