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
/// One interval of a process's GC heap (sprint 1.7), in bytes. <see cref="GcHeapBytes"/> is the heap now, garbage
/// allocated since the last GC included; the generation sizes are as of the last GC, so <see cref="HeapAfterGcBytes"/>
/// is what survived it (the rule metric, review of 1.7). Front ends see this, not Capture's counter type.
/// </summary>
public sealed record HeapSample(
    DateTimeOffset At,
    long GcHeapBytes,
    long Gen0Bytes,
    long Gen1Bytes,
    long Gen2Bytes,
    long LargeObjectHeapBytes,
    long PinnedObjectHeapBytes,
    long Gen0Collections,
    long Gen1Collections,
    long Gen2Collections,
    long AllocatedBytes,
    double TimeInGcPercent,
    long WorkingSetBytes)
{
    /// <summary>The heap as of the last GC: every generation, LOH and POH.</summary>
    public long HeapAfterGcBytes => Gen0Bytes + Gen1Bytes + Gen2Bytes + LargeObjectHeapBytes + PinnedObjectHeapBytes;

    /// <summary>
    /// What size rules compare: the heap after the last GC, or, before the process had any GC (all generation sizes still
    /// zero), the heap now, which is all there is to measure then.
    /// </summary>
    public long RuleBytes => HeapAfterGcBytes > 0 ? HeapAfterGcBytes : GcHeapBytes;

    /// <summary>Allocated since the last GC (some of it garbage): the heap now above the after-GC size.</summary>
    public long AllocatedSinceGcBytes => Math.Max(0, GcHeapBytes - HeapAfterGcBytes);

    /// <summary>Whether a GC ran during the interval (only then do the after-GC sizes and the time in GC change).</summary>
    public bool HadGc => Gen0Collections > 0;
}
