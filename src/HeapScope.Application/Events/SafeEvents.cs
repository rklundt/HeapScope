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

using Microsoft.Extensions.Logging;

namespace HeapScope.Application.Events;

/// <summary>
/// Raises an event so that a listener that throws is logged and changes nothing else: not a job's outcome, not an open,
/// not a worker host call. Raisers call it after their state is final and outside their locks (peer review P9).
/// </summary>
public static class SafeEvents
{
    // How often each listener of each event failed: the first failure is logged with its stack, later ones at Debug with
    // their count, so a listener that fails on every sample cannot fill the log (3.8 review).
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int> s_failures = new(StringComparer.Ordinal);

    /// <summary>Calls each listener of <paramref name="handler"/> in turn; one that throws is logged as a warning.</summary>
    public static void Raise<T>(EventHandler<T>? handler, object sender, T value, ILogger logger, string eventName)
    {
        ArgumentNullException.ThrowIfNull(logger);
        if (handler is null)
        {
            return;
        }

        foreach (var listener in handler.GetInvocationList().Cast<EventHandler<T>>())
        {
            try
            {
                listener(sender, value);
            }
            catch (Exception ex)
            {
                LogFailure(logger, eventName, listener.Method, ex);
            }
        }
    }

    /// <summary>Calls each listener of <paramref name="handler"/> in turn; one that throws is logged as a warning.</summary>
    public static void Raise(EventHandler? handler, object sender, ILogger logger, string eventName)
    {
        ArgumentNullException.ThrowIfNull(logger);
        if (handler is null)
        {
            return;
        }

        foreach (var listener in handler.GetInvocationList().Cast<EventHandler>())
        {
            try
            {
                listener(sender, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                LogFailure(logger, eventName, listener.Method, ex);
            }
        }
    }

    private static void LogFailure(ILogger logger, string eventName, System.Reflection.MethodInfo listener, Exception ex)
    {
        var who = $"{listener.DeclaringType?.FullName}.{listener.Name}";
        var count = s_failures.AddOrUpdate(eventName + "|" + who, 1, (_, n) => n + 1);
        if (count == 1)
        {
            logger.LogWarning(ex, "A listener of {Event} ({Listener}) failed; the change it reported stands", eventName, who);
        }
        else
        {
            logger.LogDebug("A listener of {Event} ({Listener}) failed again ({Count} times): {Error}", eventName, who, count, ex.GetType().Name);
        }
    }
}
