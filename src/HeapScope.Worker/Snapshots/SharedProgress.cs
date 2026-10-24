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

namespace HeapScope.Worker.Snapshots;

/// <summary>
/// Progress of a shared computation sent to everyone waiting for it (sprint 2.1 review M7): each waiter subscribes for as
/// long as it waits, gets the latest report at once, and stops receiving when it unsubscribes (it finished waiting or
/// canceled), so a computation never reports into a call that has already returned.
/// </summary>
internal sealed class SharedProgress<T> : IProgress<T>
{
    private readonly Lock _lock = new();
    private readonly List<IProgress<T>> _subscribers = [];
    private T? _latest;
    private bool _hasLatest;

    /// <summary>Adds a waiter until the result is disposed; null subscribes nothing.</summary>
    public IDisposable Subscribe(IProgress<T>? subscriber)
    {
        if (subscriber is null)
        {
            return Unsubscriber.None;
        }

        T? latest;
        bool hasLatest;
        lock (_lock)
        {
            _subscribers.Add(subscriber);
            (latest, hasLatest) = (_latest, _hasLatest);
        }

        if (hasLatest)
        {
            subscriber.Report(latest!);
        }

        return new Unsubscriber(() =>
        {
            lock (_lock)
            {
                _subscribers.Remove(subscriber);
            }
        });
    }

    public void Report(T value)
    {
        IProgress<T>[] subscribers;
        lock (_lock)
        {
            (_latest, _hasLatest) = (value, true);
            subscribers = [.. _subscribers];
        }

        foreach (var subscriber in subscribers)
        {
            subscriber.Report(value);
        }
    }

    private sealed class Unsubscriber(Action? remove) : IDisposable
    {
        public static readonly Unsubscriber None = new(null);

        public void Dispose() => Interlocked.Exchange(ref remove, null)?.Invoke();
    }
}
