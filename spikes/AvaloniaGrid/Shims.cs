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
using HeapScope.Contracts;

// Copies of the few texts the linked view models use, which live in WPF-app files with other classes (prototype only;
// the port moves them into a shared view model project).
namespace HeapScope.UI.Wpf.ViewModels;

public static class Format
{
    public static string Bytes(long bytes) => bytes switch
    {
        >= 1L << 30 => string.Create(CultureInfo.CurrentCulture, $"{bytes / (double)(1L << 30):N2} GB"),
        >= 1L << 20 => string.Create(CultureInfo.CurrentCulture, $"{bytes / (double)(1L << 20):N1} MB"),
        >= 1L << 10 => string.Create(CultureInfo.CurrentCulture, $"{bytes / (double)(1L << 10):N1} KB"),
        _ => string.Create(CultureInfo.CurrentCulture, $"{bytes:N0} bytes"),
    };
}

internal static class ReferrersText
{
    public static string Progress(JobProgress progress) =>
        progress.Total is > 0 and var total
            ? string.Create(CultureInfo.CurrentCulture, $"{progress.Phase}... {100.0 * progress.Completed / total:N0}%")
            : progress.Phase + "...";
}

internal static class RetainedText
{
    public static string Source(DominatorIndexInfo dominators) => string.Create(
        CultureInfo.CurrentCulture,
        $"Retained sizes {(dominators.FromSidecar ? "read from the saved index" : "computed")} in {dominators.Seconds:N1} s; {dominators.ReachableObjects:N0} objects are reachable from the roots.")
        + (dominators.Note is { } note ? " " + note : string.Empty);

    public const string RowsAreLowerBounds =
        "A row's retained bytes are at least what its objects keep alive: objects held only by several of them together are not counted.";

    public static string NotShown(string reason) => "Retained sizes are not shown: " + reason;

    public const string ObjectsRetain = "An object's retained bytes are what it alone keeps alive; objects no root reaches retain nothing (blank).";
}
