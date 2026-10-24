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
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace HeapScope.Worker;

/// <summary>Stops the worker when its parent process exits, so a crashed UI never leaves an orphaned worker.</summary>
internal sealed class ParentProcessWatcher(
    WorkerOptions options,
    IHostApplicationLifetime lifetime,
    ILogger<ParentProcessWatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (options.ParentPid is not int parentPid)
        {
            logger.LogInformation("No parent process given; the worker will not watch a parent");
            return;
        }

        Process parent;
        try
        {
            parent = Process.GetProcessById(parentPid);
        }
        catch (ArgumentException)
        {
            logger.LogWarning("Parent process {ParentPid} is not running; stopping", parentPid);
            lifetime.StopApplication();
            return;
        }

        using (parent)
        {
            try
            {
                await parent.WaitForExitAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        logger.LogWarning("Parent process {ParentPid} exited; stopping", parentPid);
        lifetime.StopApplication();
    }
}
