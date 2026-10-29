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

namespace Fixtures.Leaks;

// The leaks sprint 3.5's detectors name (fixtures/KnownAnswers PendingAsync and FinalizerBacklog), in LeakCatalog.Detected
// beside 2.6's: enabled with LeakyApi's Leaks:Detected and LeakyConsole's --detected.

/// <summary>
/// Leak 8: async methods awaiting tasks nothing completes. Each waiting call's state machine, boxed by the runtime, stays
/// reachable through the TaskCompletionSource kept in a static list (its task's continuation), with its locals.
/// </summary>
public sealed class PendingAsyncLeak : ILeak
{
    private static readonly List<TaskCompletionSource> s_signals = [];
    private static readonly object s_gate = new();

    public string Name => "PendingAsync";

    public int ItemCount
    {
        get
        {
            lock (s_gate)
            {
                return s_signals.Count;
            }
        }
    }

    public void Grow(int items, int payloadBytes)
    {
        lock (s_gate)
        {
            for (var i = 0; i < items; i++)
            {
                var signal = new TaskCompletionSource();
                s_signals.Add(signal);
                _ = WaitForSignalAsync(signal.Task, new PendingWork(new byte[payloadBytes]));
            }
        }
    }

    private static async Task WaitForSignalAsync(Task signal, PendingWork work)
    {
        await signal.ConfigureAwait(false);
        work.Complete();
    }
}

/// <summary>What a waiting async call keeps alive in its state machine.</summary>
public sealed class PendingWork(byte[] state)
{
    public byte[] State { get; } = state;

    public bool Done { get; private set; }

    public void Complete() => Done = true;
}

/// <summary>
/// Leak 9: a finalizer backlog. The first growth queues an object whose finalizer blocks the finalizer thread for good;
/// every finalizable item dropped after it waits on the finalizer queue, alive with its payload, and each growth runs a
/// full collection so the items are queued when the dump is taken.
/// </summary>
public sealed class FinalizerBacklogLeak : ILeak
{
    private static readonly object s_gate = new();
    private static int s_count;
    private static bool s_blocked;

    public string Name => "FinalizerBacklog";

    public int ItemCount => Volatile.Read(ref s_count);

    public void Grow(int items, int payloadBytes)
    {
        lock (s_gate)
        {
            if (!s_blocked)
            {
                FinalizerBlocker.Block();
                s_blocked = true;
            }

            Drop(items, payloadBytes);
            s_count += items;
            GC.Collect();
        }
    }

    // Not inlined, so no item stays referenced from this frame when the collection runs.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void Drop(int items, int payloadBytes)
    {
        for (var i = 0; i < items; i++)
        {
            _ = new FinalizableItem(new byte[payloadBytes]);
        }
    }
}

/// <summary>An item with a finalizer, dropped at once: it waits on the finalizer queue behind the blocker.</summary>
public sealed class FinalizableItem(byte[] state)
{
    public byte[] State { get; } = state;

    ~FinalizableItem()
    {
        // Never runs while the finalizer thread is blocked.
        GC.KeepAlive(State);
    }
}

/// <summary>Blocks the finalizer thread for the rest of the process (the cause of the backlog).</summary>
public sealed class FinalizerBlocker
{
    private static readonly ManualResetEventSlim s_entered = new();
    private static readonly ManualResetEventSlim s_never = new();

    ~FinalizerBlocker()
    {
        s_entered.Set();
        s_never.Wait();
    }

    /// <summary>Drops a blocker and waits until the finalizer thread is stuck in its finalizer.</summary>
    public static void Block()
    {
        Drop();
        GC.Collect();
        if (!s_entered.Wait(TimeSpan.FromSeconds(30)))
        {
            throw new InvalidOperationException("The finalizer thread did not run the blocker.");
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void Drop() => _ = new FinalizerBlocker();
}
