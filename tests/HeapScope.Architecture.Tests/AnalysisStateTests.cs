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

using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace HeapScope.Architecture.Tests;

/// <summary>
/// Analysis returns values and keeps nothing (P20): what a snapshot derives lives in the worker's derived slots, computed
/// once, counted against its memory budget and dropped when the snapshot closes. A static cache in Analysis would outlive
/// the snapshot, sit outside that budget and be computed twice by callers that race.
/// </summary>
public class AnalysisStateTests
{
    [Fact]
    public void Analysis_keeps_no_static_caches_or_mutable_static_state()
    {
        var offenders = new List<string>();
        foreach (var type in typeof(HeapScope.Analysis.TypeAggregation).Assembly.GetTypes())
        {
            // Compiler-generated types hold the lambdas' cached delegates, not data.
            if (type.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false) || type.Name.StartsWith('<'))
            {
                continue;
            }

            foreach (var field in type.GetFields(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (field.IsLiteral)
                {
                    continue;
                }

                if (!field.IsInitOnly)
                {
                    offenders.Add($"{type.FullName}.{field.Name}: a static field that is not read-only");
                }
                else if (IsCache(field.FieldType))
                {
                    offenders.Add($"{type.FullName}.{field.Name}: a static {field.FieldType.Name}");
                }
            }
        }

        offenders.ShouldBeEmpty();
    }

    // A table of weak keys, or a collection that can grow (constant arrays and immutable collections are fine).
    private static bool IsCache(Type type)
    {
        if (type.IsArray || type.Namespace is "System.Collections.Immutable" or "System.Collections.Frozen")
        {
            return false;
        }

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(ConditionalWeakTable<,>))
        {
            return true;
        }

        var interfaces = type.IsInterface ? type.GetInterfaces().Append(type) : type.GetInterfaces();
        return typeof(IDictionary).IsAssignableFrom(type)
            || typeof(ICollection).IsAssignableFrom(type)
            || interfaces.Any(i => i.IsGenericType && i.GetGenericTypeDefinition() is var open
                && (open == typeof(ICollection<>) || open == typeof(ISet<>) || open == typeof(IDictionary<,>)));
    }
}
