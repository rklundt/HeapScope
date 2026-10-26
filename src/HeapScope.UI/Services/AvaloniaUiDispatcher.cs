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


using Avalonia.Threading;

namespace HeapScope.UI.Services;

/// <summary>The Avalonia UI thread's dispatcher (sprint 2.3.6).</summary>
public sealed class AvaloniaUiDispatcher(Dispatcher dispatcher) : IUiDispatcher
{
    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (dispatcher.CheckAccess())
        {
            action();
        }
        else
        {
            dispatcher.Post(action, DispatcherPriority.Normal);
        }
    }

    // Background priority runs after layout, render and input (sprint 1.5); off the UI thread there is nothing to yield to.
    public Task YieldToRenderAsync() =>
        dispatcher.CheckAccess() ? dispatcher.InvokeAsync(static () => { }, DispatcherPriority.Background).GetTask() : Task.CompletedTask;
}
