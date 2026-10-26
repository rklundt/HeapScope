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
using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace HeapScope.Worker.Rpc;

/// <summary>
/// A request or a result as one log line (sprint 2.3.7 review H3): what was asked and what was answered, so a disputed
/// result can be traced to its inputs. Numbers, flags, enums, ids, times and the names below are written as they are;
/// lists by their count; any other text by its length only, so field values, string contents and the user's filter text
/// never reach the log (ARCHITECTURE.md 11 and 12).
/// </summary>
internal static class Provenance
{
    // Texts HeapScope or the runtime's metadata produce, never values read from the dump or typed by the user.
    private static readonly HashSet<string> s_namedTexts = new(StringComparer.Ordinal)
    {
        "TypeName", "Name", "Module", "Path", "CachePath", "CacheDetail", "RuntimeVersion", "ProcessName", "Note",
        "FieldsNote", "UnreachableNote", "Unavailable", "FieldsUnavailable", "DominatorType", "TargetType", "Title",
        "Phase", "Field", "ProductVersion", "Inspection", "Status", "SortColumn", "Column",
    };

    private static readonly ConcurrentDictionary<Type, PropertyInfo[]> s_properties = new();

    /// <summary>The value for the log, at most two levels of nested records deep.</summary>
    public static string Describe(object? value)
    {
        var text = new StringBuilder();
        Append(text, value, depth: 0);
        return text.ToString();
    }

    private static void Append(StringBuilder text, object? value, int depth)
    {
        switch (value)
        {
            case null:
                text.Append("null");
                return;
            case string s:
                text.Append('(').Append(s.Length.ToString(CultureInfo.InvariantCulture)).Append(" chars)");
                return;
            case bool or Enum or Guid:
                text.Append(value);
                return;
            case IFormattable formattable:
                text.Append(formattable.ToString(null, CultureInfo.InvariantCulture));
                return;
            case ICollection collection:
                text.Append('[').Append(collection.Count.ToString(CultureInfo.InvariantCulture)).Append(']');
                return;
            case IEnumerable:
                text.Append("[...]");
                return;
        }

        var type = value.GetType();
        text.Append(type.Name);
        if (depth >= 2)
        {
            return;
        }

        text.Append(" {");
        var first = true;
        foreach (var property in s_properties.GetOrAdd(type, static t => [.. t.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.GetIndexParameters().Length == 0 && p.Name != "EqualityContract")]))
        {
            text.Append(first ? " " : ", ").Append(property.Name).Append('=');
            first = false;
            var propertyValue = property.GetValue(value);
            if (propertyValue is string named && s_namedTexts.Contains(property.Name))
            {
                text.Append(named);
            }
            else
            {
                Append(text, propertyValue, depth + 1);
            }
        }

        text.Append(" }");
    }
}
