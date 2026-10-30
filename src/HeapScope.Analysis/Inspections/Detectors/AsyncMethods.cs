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

/// <summary>Names of async methods from the runtime's state machine boxes (sprint 3.5).</summary>
public static class AsyncMethods
{
    /// <summary>
    /// The runtime's box of an async method's state machine: <c>...+AsyncStateMachineBox&lt;TStateMachine&gt;</c>, or
    /// <c>DebugFinalizableAsyncStateMachineBox</c> while async method tracking is on.
    /// </summary>
    public const string BoxMarker = "AsyncStateMachineBox<";

    /// <summary>
    /// The async method a state machine box runs, as <c>Namespace.Type.Method</c> (from the compiler's
    /// <c>&lt;Method&gt;d__N</c> state machine), or the state machine's name when it does not follow that pattern; null for
    /// other types. Pooled <c>ValueTask</c> boxes (<c>StateMachineBox&lt;T&gt;</c>) have no task state and are not matched.
    /// </summary>
    public static string? MethodOf(string typeName)
    {
        ArgumentNullException.ThrowIfNull(typeName);
        var at = typeName.IndexOf(BoxMarker, StringComparison.Ordinal);
        var nested = at > 0 && (typeName[at - 1] == '+' || typeName.AsSpan(0, at).EndsWith("+DebugFinalizable", StringComparison.Ordinal));
        if (!nested || !typeName.EndsWith('>'))
        {
            return null;
        }

        var machine = typeName[(at + BoxMarker.Length)..^1];
        var plus = machine.LastIndexOf("+<", StringComparison.Ordinal);
        var end = machine.LastIndexOf(">d__", StringComparison.Ordinal);
        return plus >= 0 && end > plus + 2 ? machine[..plus] + "." + machine[(plus + 2)..end] : machine;
    }
}
