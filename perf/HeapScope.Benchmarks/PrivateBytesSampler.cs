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

namespace HeapScope.Benchmarks;

/// <summary>Samples the process's private bytes every 100 ms and keeps the peak.</summary>
internal sealed class PrivateBytesSampler : IDisposable
{
    private readonly Timer _timer;
    private long _peak;

    public PrivateBytesSampler() => _timer = new Timer(_ => Sample(), null, TimeSpan.Zero, TimeSpan.FromMilliseconds(100));

    public long Peak
    {
        get
        {
            Sample();
            return Interlocked.Read(ref _peak);
        }
    }

    public void Dispose() => _timer.Dispose();

    private void Sample()
    {
        using var process = Process.GetCurrentProcess();
        var value = process.PrivateMemorySize64;
        long current;
        while (value > (current = Interlocked.Read(ref _peak)) && Interlocked.CompareExchange(ref _peak, value, current) != current)
        {
        }
    }
}
