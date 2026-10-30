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
using System.Diagnostics.Tracing;
using System.Globalization;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;

namespace HeapScope.Benchmarks;

/// <summary>
/// The live sources spike of sprint 3.8 (peer review P6, ADR-037): can a process other than the one recording read the
/// allocation trace while it grows? One process records an EventPipe session's stream to a <c>.nettrace</c> file (as the
/// front end does today); a reader tails that file with TraceEvent's <c>EventPipeEventSource</c> (as the worker would).
/// Reports the events read, the delay from an event's timestamp to its arrival at the reader, lost events, and the
/// reader's memory. The target is a copy of this program allocating in a loop (<c>live-tail --allocate</c>). Measurement
/// only: nothing here ships.
/// </summary>
internal static class LiveTailSpike
{
    public static int Run(BenchmarkArgs args)
    {
        if (args.Get("allocate") is not null)
        {
            return Allocate(args.GetInt("seconds", 30), args.GetInt("pause-every", 0));
        }

        var seconds = args.GetInt("seconds", 10);
        var pauseEvery = args.GetInt("pause-every", 0);
        var buffer = args.GetInt("buffer", 1 << 16);
        var folder = Directory.CreateTempSubdirectory("heapscope-live-tail-").FullName;
        var path = Path.Combine(folder, "live.nettrace");
        using var target = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, ["live-tail", "--allocate", "true", "--seconds", (seconds + 10).ToString(CultureInfo.InvariantCulture), "--pause-every", pauseEvery.ToString(CultureInfo.InvariantCulture)]) { UseShellExecute = false })!;
        try
        {
            Thread.Sleep(1500);
            var client = new DiagnosticsClient(target.Id);
            var providers = new[]
            {
                // The allocations profile HeapScope records (TraceRecorder): GC, Loader and JIT at verbose, with rundown.
                new EventPipeProvider("Microsoft-Windows-DotNETRuntime", EventLevel.Verbose, (long)(ClrTraceEventParser.Keywords.GC | ClrTraceEventParser.Keywords.Loader | ClrTraceEventParser.Keywords.Jit)),
            };
            using var session = client.StartEventPipeSession(providers, requestRundown: true, circularBufferMB: 256);

            // The recorder writes through a 64 KB buffer; --buffer 1 writes every chunk at once.
            using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, Math.Max(1, buffer));
            var recording = Task.Run(() => session.EventStream.CopyTo(file));

            var done = false;
            var read = 0L;
            var lagsMs = new List<double>();
            long lost = 0;
            var reader = Task.Run(() =>
            {
                using var tail = new TailStream(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite), () => Volatile.Read(ref done));
                using var source = new EventPipeEventSource(tail);
                source.Clr.GCAllocationTick += e =>
                {
                    read++;
                    if (read % 16 == 0)
                    {
                        lagsMs.Add((DateTime.UtcNow - e.TimeStamp.ToUniversalTime()).TotalMilliseconds);
                    }
                };
                source.Process();
                lost = source.EventsLost;
            });

            var started = Stopwatch.StartNew();
            Thread.Sleep(TimeSpan.FromSeconds(seconds));
            session.Stop();
            recording.Wait(TimeSpan.FromSeconds(30));
            file.Flush(flushToDisk: true);
            Volatile.Write(ref done, true);
            var finished = reader.Wait(TimeSpan.FromSeconds(60));
            var wall = started.Elapsed;

            lagsMs.Sort();
            double Percentile(double p) => lagsMs.Count == 0 ? double.NaN : lagsMs[(int)Math.Min(lagsMs.Count - 1, Math.Round(p * (lagsMs.Count - 1)))];
            using var self = Process.GetCurrentProcess();
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"live-tail: {seconds} s recorded (pause every {pauseEvery} allocations, write buffer {buffer} bytes), reader {(finished ? "finished" : "did not finish")} after {wall.TotalSeconds:N1} s"));
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  allocation ticks read {read:N0}, events lost {lost:N0}, file {new FileInfo(path).Length / 1024.0 / 1024.0:N1} MB"));
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  delay from event to reader: p50 {Percentile(0.5):N0} ms, p95 {Percentile(0.95):N0} ms, max {Percentile(1):N0} ms ({lagsMs.Count:N0} sampled)"));
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  this process: peak working set {self.PeakWorkingSet64 / 1024.0 / 1024.0:N0} MB, GC heap {GC.GetTotalMemory(false) / 1024.0 / 1024.0:N0} MB"));
            return finished ? 0 : 1;
        }
        finally
        {
            try
            {
                target.Kill();
            }
            catch (InvalidOperationException)
            {
            }

            Directory.Delete(folder, recursive: true);
        }
    }

    /// <summary>The target: allocates objects of a few types in a loop, keeping some, until its time is up.</summary>
    private static int Allocate(int seconds, int pauseEvery)
    {
        var kept = new List<object>();
        var until = Stopwatch.StartNew();
        var n = 0;
        while (until.Elapsed < TimeSpan.FromSeconds(seconds))
        {
            object item = (n % 3) switch
            {
                0 => new byte[1024],
                1 => new string('x', 200),
                _ => new List<int>(64),
            };
            if (n % 100 == 0)
            {
                kept.Add(item);
            }

            if (kept.Count > 100_000)
            {
                kept.Clear();
            }

            n++;
            if (pauseEvery > 0 && n % pauseEvery == 0)
            {
                Thread.Sleep(1);
            }
        }

        return kept.Count >= 0 ? 0 : 1;
    }

    /// <summary>A read-only view of a file another process is still writing: at its end it waits for more until done.</summary>
    private sealed class TailStream(FileStream inner, Func<bool> done) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => inner.Position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            while (true)
            {
                var n = inner.Read(buffer, offset, count);
                if (n > 0)
                {
                    return n;
                }

                if (done())
                {
                    // One more read after the writer finished, in case it wrote between the two checks.
                    return inner.Read(buffer, offset, count);
                }

                Thread.Sleep(20);
            }
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
