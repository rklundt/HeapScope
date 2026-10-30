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


using HeapScope.Application.Monitoring;
using HeapScope.Application.Sessions;
using HeapScope.Capture;
using Microsoft.Extensions.Logging.Abstractions;

namespace HeapScope.Application.Tests;

/// <summary>A monitoring run's allocation trace, apart from the run (sprint 3.8, peer review P11).</summary>
public sealed class AllocationRecordingTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("heapscope-recording-").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Disposing_waits_for_the_watch_which_waits_for_running_captures_before_stopping_the_trace()
    {
        static async Task<TraceResult> Recorder(int pid, string path, CancellationToken ct)
        {
            var partial = TraceRecorder.PartialName(path);
            await File.WriteAllBytesAsync(partial, new byte[4096], CancellationToken.None);
            await Task.Delay(Timeout.Infinite, ct).ContinueWith(_ => { }, TaskScheduler.Default);
            File.Move(partial, path);
            return new TraceResult(path, 4096, TimeSpan.FromSeconds(1), StoppedEarly: true);
        }

        var capture = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var problems = new List<string>();
        var limitReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recording = AllocationRecording.Start(
            _folder,
            Guid.NewGuid(),
            42,
            limitBytes: 1024,
            Recorder,
            TimeProvider.System,
            NullLogger.Instance,
            () => [capture.Task],
            (message, _) =>
            {
                lock (problems)
                {
                    problems.Add(message);
                }

                limitReached.TrySetResult();
            });

        await limitReached.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        var disposing = recording.DisposeAsync().AsTask();
        await Task.Delay(300, Ct);
        disposing.IsCompleted.ShouldBeFalse("the watch waits for the running capture before it stops the trace");

        capture.SetResult();
        await disposing.WaitAsync(TimeSpan.FromSeconds(30), Ct);
        problems.ShouldHaveSingleItem().ShouldContain("limit");
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);
}
