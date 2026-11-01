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

/// <summary>The runtime's EventCounters over EventPipe (ADR-021).</summary>
public sealed class EventPipeCounterSource : ICounterSource
{
    public Task RunAsync(int processId, TimeSpan interval, Action<HeapSample> onSample, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(onSample);
        return CounterMonitor.RunAsync(
            processId,
            interval,
            c => onSample(new HeapSample(
                c.At, c.GcHeapBytes, c.Gen0Bytes, c.Gen1Bytes, c.Gen2Bytes, c.LargeObjectHeapBytes, c.PinnedObjectHeapBytes,
                c.Gen0Collections, c.Gen1Collections, c.Gen2Collections, c.AllocatedBytes, c.TimeInGcPercent, c.WorkingSetBytes)),
            cancellationToken);
    }
}
