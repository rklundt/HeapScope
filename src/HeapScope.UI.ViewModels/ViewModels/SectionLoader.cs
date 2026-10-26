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


using HeapScope.Application.Workers;
using HeapScope.Contracts;
using Microsoft.Extensions.Logging;

namespace HeapScope.UI.ViewModels;

/// <summary>
/// Loads one panel of a view (sprint 2.3.7 review M12; was written out four times in <see cref="ObjectViewModel"/>): the
/// query's progress phases show in the panel's status until the answer arrives; leaving the view while it loads ends it
/// quietly (an answer that arrives anyway is discarded); a worker refusal shows its message, which is logged as shown; any
/// other failure is logged with its stack and summarized.
/// </summary>
internal static class SectionLoader
{
    public static async Task RunAsync<T>(
        string section,
        Func<IProgress<JobProgress>, CancellationToken, Task<T>> query,
        Action<T> show,
        Action<string?> setStatus,
        string failure,
        ILogger logger,
        CancellationToken cancellationToken,
        Func<T, Task>? discard = null)
    {
        var finished = false;
        var progress = new Progress<JobProgress>(p =>
        {
            if (!finished && !cancellationToken.IsCancellationRequested)
            {
                setStatus(ReferrersText.Progress(p));
            }
        });
        try
        {
            var result = await query(progress, cancellationToken);
            finished = true;
            if (cancellationToken.IsCancellationRequested)
            {
                if (discard is not null)
                {
                    await discard(result);
                }

                return;
            }

            show(result);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            // Left the history while loading.
        }
        catch (Exception ex) when (ex is WorkerException or OperationCanceledException)
        {
            finished = true;
            logger.LogInformation("{Section} shown as: {Message}", section, ex.Message);
            setStatus(ex.Message);
        }
        catch (Exception ex)
        {
            finished = true;
            logger.LogError(ex, "{Section} failed", section);
            setStatus(failure);
        }
    }
}
