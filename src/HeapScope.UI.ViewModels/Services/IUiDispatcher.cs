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


namespace HeapScope.UI.Services;

/// <summary>
/// Runs work on the UI thread (ARCHITECTURE.md 8). Application raises events on any thread; view models marshal
/// through this abstraction, which tests replace with an immediate implementation. Each UI toolkit has its own
/// implementation (sprint 2.3.6: Avalonia).
/// </summary>
public interface IUiDispatcher
{
    /// <summary>Queues <paramref name="action"/> on the UI thread, or runs it now when already there.</summary>
    void Post(Action action);

    /// <summary>
    /// Continues after the UI's pending render and input work (sprint 1.5): a grid binds its rows after the page that
    /// asked for them has been laid out, so the two are separate UI operations rather than one twice as long. Completes at
    /// once off the UI thread and where the toolkit has no such notion (sprint 2.3.6: was a static hook).
    /// </summary>
    Task YieldToRenderAsync();
}

/// <summary>Runs actions immediately on the calling thread (tests, design time).</summary>
public sealed class ImmediateUiDispatcher : IUiDispatcher
{
    public static ImmediateUiDispatcher Instance { get; } = new();

    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        action();
    }

    public Task YieldToRenderAsync() => Task.CompletedTask;
}
