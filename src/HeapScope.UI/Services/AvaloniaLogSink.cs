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


using Avalonia.Logging;
using Microsoft.Extensions.Logging;

namespace HeapScope.UI.Services;

/// <summary>
/// Sends Avalonia's warnings and errors (binding failures, platform problems) to the HeapScope log (sprint 2.3.7 review
/// M18); before the host exists they go to the debug trace. Only the area, the message template and the source's type
/// are written: Avalonia's property values can be a bound cell's text, which may come from a dump (ARCHITECTURE.md 12).
/// </summary>
public sealed class AvaloniaLogSink : ILogSink
{
    /// <summary>The logger, set once the host is built.</summary>
    public static Microsoft.Extensions.Logging.ILogger? Target { get; set; }

    public bool IsEnabled(LogEventLevel level, string area) => level >= LogEventLevel.Warning;

    public void Log(LogEventLevel level, string area, object? source, string messageTemplate) => Write(level, area, source, messageTemplate);

    public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues) =>
        Write(level, area, source, messageTemplate);

    private void Write(LogEventLevel level, string area, object? source, string messageTemplate)
    {
        if (!IsEnabled(level, area))
        {
            return;
        }

        if (Target is { } target)
        {
            target.Log(
                level >= LogEventLevel.Error ? LogLevel.Error : LogLevel.Warning,
                "Avalonia {Area}: {Template} (source {Source})",
                area,
                messageTemplate,
                source?.GetType().Name ?? "none");
        }
        else
        {
            System.Diagnostics.Trace.WriteLine($"Avalonia {area}: {messageTemplate}");
        }
    }
}
