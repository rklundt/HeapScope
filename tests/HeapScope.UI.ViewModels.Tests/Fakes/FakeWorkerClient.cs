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

using System.Globalization;
using System.IO;
using HeapScope.Application.Workers;
using HeapScope.Contracts;

namespace HeapScope.UI.ViewModels.Tests;

/// <summary>A worker client that calls an in-process fake service.</summary>
internal sealed class FakeWorkerClient : IWorkerClient
{
    public FakeWorkerService Service { get; } = new();

    public WorkerState State { get; private set; } = WorkerState.NotStarted;

    public WorkerInfo? Info { get; } = new(Guid.NewGuid(), WorkerProtocol.Version, "0.0.0", 1234, true, 0, 0, 0, false);

    public int RestartCount { get; private set; }

    public int Generation { get; private set; } = 1;

    public event EventHandler<WorkerStateChangedEventArgs>? StateChanged;

    public Task<WorkerInfo> EnsureStartedAsync(CancellationToken cancellationToken)
    {
        State = WorkerState.Running;
        return Task.FromResult(Info!);
    }

    public Task<T> InvokeAsync<T>(Func<IWorkerService, CancellationToken, Task<T>> call, CancellationToken cancellationToken) =>
        call(Service, cancellationToken);

    public Task InvokeAsync(Func<IWorkerService, CancellationToken, Task> call, CancellationToken cancellationToken) =>
        call(Service, cancellationToken);

    /// <summary>Simulates a worker restart that sessions see on their next call (they reopen their snapshots).</summary>
    public void NewGeneration() => Generation++;

    public Task RestartAsync(CancellationToken cancellationToken)
    {
        RestartCount++;
        Raise(WorkerState.Running, null);
        return Task.CompletedTask;
    }

    public void Raise(WorkerState state, string? message)
    {
        State = state;
        StateChanged?.Invoke(this, new WorkerStateChangedEventArgs(state, message));
    }
}
