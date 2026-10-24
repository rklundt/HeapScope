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

using HeapScope.Contracts;

namespace HeapScope.Application.Workers;

/// <summary>
/// Access to the analysis worker. Starts it on first use and restarts it transparently after a crash
/// (ARCHITECTURE.md 4.3). Calls translate transport failures into <see cref="WorkerCrashedException"/> and worker
/// errors into <see cref="WorkerOperationException"/>.
/// </summary>
public interface IWorkerClient
{
    WorkerState State { get; }

    /// <summary>Information from the last successful handshake.</summary>
    WorkerInfo? Info { get; }

    /// <summary>
    /// Incremented each time a new worker process is connected. Worker-owned ids (snapshots, result sets) obtained
    /// under an older generation are gone (ARCHITECTURE.md 4.3).
    /// </summary>
    int Generation { get; }

    /// <summary>Raised on any thread when <see cref="State"/> changes.</summary>
    event EventHandler<WorkerStateChangedEventArgs>? StateChanged;

    /// <summary>Starts (or restarts) the worker when it is not running and returns the handshake information.</summary>
    Task<WorkerInfo> EnsureStartedAsync(CancellationToken cancellationToken);

    /// <summary>Calls the worker, starting it first when needed.</summary>
    Task<T> InvokeAsync<T>(Func<IWorkerService, CancellationToken, Task<T>> call, CancellationToken cancellationToken);

    /// <summary>Calls the worker, starting it first when needed.</summary>
    Task InvokeAsync(Func<IWorkerService, CancellationToken, Task> call, CancellationToken cancellationToken);

    /// <summary>Stops the worker (orderly when possible) and starts a new one.</summary>
    Task RestartAsync(CancellationToken cancellationToken);
}
