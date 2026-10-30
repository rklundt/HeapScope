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

using HeapScope.Analysis.Retention;
using HeapScope.Core;
using HeapScope.Core.Graph;

namespace HeapScope.Analysis.Inspections;

/// <summary>Reads the few runtime fields the detectors need, tolerating missing ones (another runtime version).</summary>
internal static class Fields
{
    /// <summary>An integer field; native ints (<c>IntPtr</c>) arrive as pointers (re-review N1).</summary>
    public static long? Integer(IObjectReader reader, ulong address, string path) =>
        reader.ReadField(address, path) is { } value
            ? value.Kind switch
            {
                ObjectValueKind.Integer => value.Integer,
                ObjectValueKind.Unsigned or ObjectValueKind.Pointer => (long)value.Unsigned,
                _ => null,
            }
            : null;

    public static bool? Boolean(IObjectReader reader, ulong address, string path) =>
        reader.ReadField(address, path) is { Kind: ObjectValueKind.Boolean } value ? value.Boolean : null;

    /// <summary>A flag stored as a bool, or as an integer set with Interlocked (non-zero is set; review M10); null when missing.</summary>
    public static bool? Flag(IObjectReader reader, ulong address, string path) =>
        reader.ReadField(address, path) is { } value
            ? value.Kind switch
            {
                ObjectValueKind.Boolean => value.Boolean,
                ObjectValueKind.Integer => value.Integer != 0,
                ObjectValueKind.Unsigned => value.Unsigned != 0,
                _ => null,
            }
            : null;

    /// <summary>The object a reference field points at: its address and runtime type; null for a null or missing field.</summary>
    public static (ulong Address, string Type)? Reference(IObjectReader reader, ulong address, string path) =>
        reader.ReadField(address, path) is { Kind: ObjectValueKind.Reference or ObjectValueKind.Array } value && value.Address != 0
            ? (value.Address, value.TypeName ?? "?")
            : null;

    /// <summary>Whether the delegate at <paramref name="address"/> is a multicast one: its invocation list is an array.</summary>
    public static bool IsMulticast(IObjectReader reader, ulong address) =>
        Reference(reader, address, RuntimeLayouts.Delegates.InvocationList) is { Type: var list } && list.EndsWith("[]", StringComparison.Ordinal);

    /// <summary>
    /// The object a single delegate calls its method on; null for a static method (review M4): the runtime stores the
    /// delegate itself in <c>_target</c> for a static or open delegate, and marks it in <c>_methodPtrAux</c>.
    /// </summary>
    public static (ulong Address, string Type)? DelegateTarget(IObjectReader reader, ulong address) =>
        Reference(reader, address, RuntimeLayouts.Delegates.Target) is { } target && target.Address != address && (Integer(reader, address, RuntimeLayouts.Delegates.MethodPtrAux) ?? 0) == 0
            ? target
            : null;

    /// <summary>
    /// The targets of a delegate: each one of its invocation list's (a multicast delegate), the wrapped delegate's, or
    /// its own. A static method's delegate has no target (null in the list).
    /// </summary>
    public static List<(ulong Address, string Type)?> DelegateTargets(IObjectReader reader, ulong address, int max, CancellationToken cancellationToken)
    {
        var targets = new List<(ulong Address, string Type)?>();
        if (Reference(reader, address, RuntimeLayouts.Delegates.InvocationList) is { } list)
        {
            if (list.Type.EndsWith("[]", StringComparison.Ordinal))
            {
                var count = (int)Math.Min(Integer(reader, address, RuntimeLayouts.Delegates.InvocationCount) ?? max, max);
                foreach (var element in reader.ReadFields(list.Address, count, 0, cancellationToken))
                {
                    if (element.Value is { Kind: ObjectValueKind.Reference, Address: not 0 } item)
                    {
                        targets.Add(DelegateTarget(reader, item.Address));
                    }
                }

                return targets;
            }

            // A wrapper delegate holds the delegate it calls in its invocation list.
            targets.Add(DelegateTarget(reader, list.Address));
            return targets;
        }

        targets.Add(DelegateTarget(reader, address));
        return targets;
    }
}
