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

using HeapScope.Worker.Snapshots;

namespace HeapScope.Worker.Tests;

/// <summary>The 2.3 review M13: a shared computation nobody waits for is canceled; one somebody waits for runs on.</summary>
public sealed class DerivedDataTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_last_caller_leaving_cancels_a_running_computation_and_the_next_one_starts_again()
    {
        var derived = new DerivedData(TimeSpan.Zero);
        var computation = new Computation();

        var first = derived.JoinGroupRetention(Analysis.GroupKey.Type, computation.Start);
        var second = derived.JoinGroupRetention(Analysis.GroupKey.Type, computation.Start);
        first.Dispose();
        computation.Tokens[0].IsCancellationRequested.ShouldBeFalse("one caller still waits");
        second.Dispose();

        computation.Tokens[0].IsCancellationRequested.ShouldBeTrue("nobody waits any more");
        await Should.ThrowAsync<OperationCanceledException>(() => first.Task);
        using var again = derived.JoinGroupRetention(Analysis.GroupKey.Type, computation.Start);
        computation.Tokens.Count.ShouldBe(2, "an abandoned computation is started again");
        again.Task.ShouldNotBeSameAs(first.Task);
    }

    [Fact]
    public async Task A_caller_who_joins_within_the_grace_period_keeps_the_computation()
    {
        var derived = new DerivedData(TimeSpan.FromMilliseconds(300));
        var computation = new Computation();

        derived.JoinGroupRetention(Analysis.GroupKey.Type, computation.Start).Dispose();
        using var rejoined = derived.JoinGroupRetention(Analysis.GroupKey.Type, computation.Start);
        await Task.Delay(600, Ct);

        computation.Tokens.ShouldHaveSingleItem().IsCancellationRequested.ShouldBeFalse("a re-query joined in time");
        computation.Finish([7UL]);
        (await rejoined.Task).ShouldBe([7UL]);
    }

    [Fact]
    public async Task Nobody_joining_within_the_grace_period_cancels_after_it()
    {
        var derived = new DerivedData(TimeSpan.FromMilliseconds(100));
        var computation = new Computation();

        derived.JoinGroupRetention(Analysis.GroupKey.Namespace, computation.Start).Dispose();

        computation.Tokens[0].IsCancellationRequested.ShouldBeFalse("not before the grace period ends");
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!computation.Tokens[0].IsCancellationRequested && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20, Ct);
        }

        computation.Tokens[0].IsCancellationRequested.ShouldBeTrue();
    }

    [Fact]
    public async Task A_finished_computation_is_kept_when_its_callers_leave_and_closing_drops_it()
    {
        var derived = new DerivedData(TimeSpan.Zero);
        var computation = new Computation();
        using (var waiter = derived.JoinGroupRetention(Analysis.GroupKey.Type, computation.Start))
        {
            computation.Finish([1UL]);
            (await waiter.Task).ShouldBe([1UL]);
        }

        using (var later = derived.JoinGroupRetention(Analysis.GroupKey.Type, computation.Start))
        {
            (await later.Task).ShouldBe([1UL]);
        }

        computation.Tokens.ShouldHaveSingleItem().IsCancellationRequested.ShouldBeFalse();
        derived.Close();
        using var closed = derived.JoinGroupRetention(Analysis.GroupKey.Type, computation.Start);
        computation.Tokens.Count.ShouldBe(2, "closing drops the result");
        computation.Tokens[1].IsCancellationRequested.ShouldBeTrue("a computation started after close is canceled at once");
    }

    /// <summary>A computation that waits for its result or its cancellation, recording each start's token.</summary>
    private sealed class Computation
    {
        private TaskCompletionSource<ulong[]> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<CancellationToken> Tokens { get; } = [];

        public Task<ulong[]> Start(CancellationToken token)
        {
            Tokens.Add(token);
            _result = new TaskCompletionSource<ulong[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            var result = _result;
            token.Register(() => result.TrySetCanceled(token));
            return result.Task;
        }

        public void Finish(ulong[] value) => _result.TrySetResult(value);
    }
}
