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

using HeapScope.Core.Graph;

namespace HeapScope.Loaders.GcDump;

/// <summary>
/// The root kind a gcdump pseudo node stands for. The writer names them (DotNetHeapDumpGraphReader): "[local vars]",
/// "[static vars]" with one "[static var Type.field]" per static, "[thread static vars]", and below "[other roots]" the
/// handles: "[strong Handles]", "[pinning Handles]", "[finalizer Handles]", "[COM/WinRT Objects]", "[Dependent Handles]",
/// "[other Handles]". Weak handles are not written. A pseudo node with another name inherits its parent's kind.
/// </summary>
internal readonly record struct RootClass(RootKind Kind, RootFlags Flags, string? Detail)
{
    public static readonly RootClass Other = new(RootKind.Other, RootFlags.Retaining, null);

    private const string StaticVarPrefix = "[static var ";

    public static RootClass Of(string? pseudoNodeName, RootClass inherited)
    {
        if (pseudoNodeName is null)
        {
            return inherited;
        }

        if (pseudoNodeName.StartsWith(StaticVarPrefix, StringComparison.Ordinal) && pseudoNodeName.EndsWith(']'))
        {
            var field = StaticDetail(pseudoNodeName[StaticVarPrefix.Length..^1]);
            return inherited.Detail == ThreadStatic
                ? new(RootKind.Other, RootFlags.Retaining, $"{ThreadStatic} {field}")
                : new(RootKind.StaticField, RootFlags.Retaining, field);
        }

        if (pseudoNodeName.StartsWith("[CCW ", StringComparison.Ordinal))
        {
            // A COM callable wrapper: the native side holds the managed object through a ref-counted handle.
            return new(RootKind.RefCountedHandle, RootFlags.Retaining, null);
        }

        return pseudoNodeName switch
        {
            "[local vars]" => new(RootKind.ThreadStack, RootFlags.Retaining, null),
            "[static vars]" => new(RootKind.StaticField, RootFlags.Retaining, null),
            "[thread static vars]" => new(RootKind.Other, RootFlags.Retaining, ThreadStatic),
            "[strong Handles]" => new(RootKind.StrongHandle, RootFlags.Retaining, null),
            "[pinning Handles]" => new(RootKind.PinnedHandle, RootFlags.Retaining | RootFlags.Pinned, null),
            "[finalizer Handles]" => new(RootKind.FinalizerQueue, RootFlags.Retaining, null),
            "[COM/WinRT Objects]" => new(RootKind.RefCountedHandle, RootFlags.Retaining, null),

            // A dependent handle's value lives while its key does. The gcdump writes the value only when the key was
            // alive and drops the key, and every object in a gcdump survived its collection, so the value is recorded as
            // retained (review M11): a non-retaining root would make it look like garbage to reachability analyses.
            "[Dependent Handles]" => new(RootKind.Other, RootFlags.Retaining, "dependent handle value (key not recorded)"),
            "[other Handles]" => new(RootKind.Other, RootFlags.Retaining, "handle"),
            _ => inherited,
        };
    }

    private const string ThreadStatic = "thread static";

    /// <summary>
    /// "Type.field" as the writer names a static: the type is the field's own type (the runtime's static root event carries
    /// the value's type, not the declaring type), so the detail reads "field (Type)" (sprint 1.8 review M12).
    /// </summary>
    internal static string StaticDetail(string recorded)
    {
        var depth = 0;
        for (var i = recorded.Length - 1; i >= 0; i--)
        {
            switch (recorded[i])
            {
                case '>' or ']':
                    depth++;
                    break;
                case '<' or '[':
                    depth--;
                    break;
                case '.' when depth == 0:
                    return $"{recorded[(i + 1)..]} ({TypeNames.Normalize(recorded[..i])})";
            }
        }

        return recorded;
    }
}
