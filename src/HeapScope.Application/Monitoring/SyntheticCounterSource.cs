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
using HeapScope.Application.Sessions;
using HeapScope.Capture;
using HeapScope.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using HeapScope.Core;
using HeapScope.Application.Events;

namespace HeapScope.Application.Monitoring;

/// <summary>
/// Samples made up without a process (diagnostics and the UI smoke run, like the synthetic job): a heap that grows and
/// is collected back in a saw tooth, <paramref name="count"/> samples one interval apart in sample time, handed out at
/// <paramref name="perSecond"/> a second.
/// </summary>
public sealed class SyntheticCounterSource(int count, int perSecond) : ICounterSource
{
    public async Task RunAsync(int processId, TimeSpan interval, Action<HeapSample> onSample, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(onSample);
        const long Mb = 1024 * 1024;
        var at = DateTimeOffset.UtcNow;
        var delay = TimeSpan.FromSeconds(1.0 / Math.Max(1, perSecond));
        for (var i = 0; i < count && !cancellationToken.IsCancellationRequested; i++)
        {
            var tooth = i % 600;
            var gen2 = (200 + tooth) * Mb;
            onSample(new HeapSample(at, gen2 + ((70 + (i % 10 * 8)) * Mb), 0, 10 * Mb, gen2, 50 * Mb, 10 * Mb, 3, 1, tooth == 0 ? 1 : 0, 64 * Mb, tooth % 100 < 20 ? 15 : 2, gen2 + (300 * Mb)));
            at += interval;
            try
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
