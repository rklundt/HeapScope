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

using System.Globalization;
using HeapScope.Application.Sessions;
using Microsoft.Extensions.Logging;

namespace HeapScope.UI.ViewModels;

/// <summary>
/// What a deletion shows before it asks and writes to the log after (sprint 3.4 review H4): the files by path, never
/// their contents, so support can tell exactly what a confirmed deletion removed, what failed and what was kept.
/// </summary>
internal static class DeletionText
{
    private const int ShownPaths = 8;

    /// <summary>The files a planned deletion removes, the first few by path, for its confirmation.</summary>
    public static string Files(Deletion plan)
    {
        var shown = string.Join(Environment.NewLine, plan.Files.Take(ShownPaths).Select(f => "  " + f));
        var more = plan.Files.Count > ShownPaths ? string.Create(CultureInfo.CurrentCulture, $"{Environment.NewLine}  and {plan.Files.Count - ShownPaths} more files") : string.Empty;
        var kept = plan.Kept is { Count: > 0 } k
            ? Environment.NewLine + "Kept, because they are outside the session folder:" + Environment.NewLine + string.Join(Environment.NewLine, k.Select(f => "  " + f))
            : string.Empty;
        return shown + more + kept;
    }

    /// <summary>Logs a deletion's outcome: every file deleted, every failure with its reason, every file kept.</summary>
    public static void Log(ILogger? logger, string what, Deletion deletion)
    {
        logger?.LogInformation(
            "Deleted {What}: {Count} files, {Bytes} bytes: {Files}",
            what,
            deletion.Files.Count,
            deletion.Bytes,
            string.Join("; ", deletion.Files));
        if (!deletion.Complete)
        {
            logger?.LogWarning("Deleting {What} left {Count} files: {Failed}", what, deletion.Failed!.Count, string.Join("; ", deletion.Failed));
        }

        if (deletion.Kept is { Count: > 0 } kept)
        {
            logger?.LogInformation("Deleting {What} kept {Count} files outside the session folder: {Kept}", what, kept.Count, string.Join("; ", kept));
        }
    }
}
