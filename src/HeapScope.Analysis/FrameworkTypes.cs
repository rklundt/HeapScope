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

namespace HeapScope.Analysis;

/// <summary>
/// One rule for what counts as a framework type (sprint 3.8, peer review P19): the leak detectors leave framework types
/// out, and retention paths fold framework steps between the application's own. A type is the framework's when its name
/// is in the <c>System</c>, <c>Microsoft</c> or <c>Internal</c> namespaces, or, when its module is known, the module is a
/// <c>System</c> or <c>Microsoft</c> assembly or <c>netstandard.dll</c>.
/// </summary>
public static class FrameworkTypes
{
    /// <summary>Whether <paramref name="typeName"/> is a framework type by its namespace.</summary>
    public static bool IsFramework(string typeName)
    {
        ArgumentNullException.ThrowIfNull(typeName);
        return typeName.StartsWith("System.", StringComparison.Ordinal)
            || typeName.StartsWith("Microsoft.", StringComparison.Ordinal)
            || typeName.StartsWith("Internal.", StringComparison.Ordinal);
    }

    /// <summary>Whether a type is a framework type by its namespace or its module (a file name or a recorded path).</summary>
    public static bool IsFramework(string typeName, string? module) => IsFramework(typeName) || IsFrameworkModule(module);

    /// <summary>
    /// Whether a step of a retention path may be folded between the application's own steps: a type of a framework
    /// module, or a <c>System</c> or <c>Microsoft</c> name; an <c>Internal</c> name only when its module is a framework
    /// one or unknown, since an application may have an <c>Internal</c> namespace of its own (3.8 review).
    /// </summary>
    public static bool IsFrameworkStep(string typeName, string? module)
    {
        ArgumentNullException.ThrowIfNull(typeName);
        if (IsFrameworkModule(module))
        {
            return true;
        }

        return typeName.StartsWith("Internal.", StringComparison.Ordinal)
            ? string.IsNullOrEmpty(module)
            : IsFramework(typeName);
    }

    /// <summary>Whether <paramref name="module"/> (a file name or a path) is a framework assembly.</summary>
    public static bool IsFrameworkModule(string? module)
    {
        var file = TypeGroups.ModuleKey(module);
        return file.StartsWith("System.", StringComparison.OrdinalIgnoreCase)
            || file.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase)
            || file.Equals("netstandard.dll", StringComparison.OrdinalIgnoreCase);
    }
}
