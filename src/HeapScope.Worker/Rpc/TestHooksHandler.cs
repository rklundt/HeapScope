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

using System.Diagnostics;
using HeapScope.Contracts;
using Microsoft.Extensions.Logging;

namespace HeapScope.Worker.Rpc;

/// <summary>The crash and hang test hooks (<see cref="IWorkerTestHooks"/>), refused unless the worker started with them on.</summary>
internal sealed class TestHooksHandler(WorkerOptions options, ILogger<TestHooksHandler> logger) : IWorkerTestHooks
{
    public Task CrashForTestingAsync(CancellationToken cancellationToken)
    {
        EnsureTestHooks();
        logger.LogWarning("Crash requested by a test hook");
        using var self = Process.GetCurrentProcess();
        self.Kill();
        return Task.CompletedTask;
    }

    public async Task HangForTestingAsync(IProgress<JobProgress>? progress, CancellationToken cancellationToken)
    {
        EnsureTestHooks();
        progress?.Report(new JobProgress("Hanging", 0, null, "Waiting until canceled"));
        await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
    }

    private void EnsureTestHooks()
    {
        if (!options.EnableTestHooks)
        {
            throw WorkerErrors.Create(HeapScopeErrorCode.TestHooksDisabled, "Test hooks are disabled; start the worker with --enable-test-hooks.");
        }
    }
}
