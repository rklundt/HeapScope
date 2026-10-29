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

using HeapScope.Core;
using HeapScope.Core.Graph;

namespace HeapScope.Analysis;

/// <summary>
/// The interfaces of each type of a snapshot (sprint 3.2, ADR-031), read from the dump through up to
/// <see cref="TriesPerType"/> objects of each type. A type whose interfaces could not be read is kept apart
/// (<see cref="IsUnread"/>, the <see cref="TypeGroups.Unread"/> row), never shown as implementing nothing (the 3.2 review).
/// </summary>
public sealed class TypeInterfaces
{
    /// <summary>Objects of one type tried before its interfaces count as unread.</summary>
    public const int TriesPerType = 3;

    private readonly string[]?[] _byType;

    public TypeInterfaces(string[]?[] byType)
    {
        _byType = byType ?? throw new ArgumentNullException(nameof(byType));
        UnreadTypes = byType.Count(t => t is null);
    }

    /// <summary>Types present whose interfaces could not be read.</summary>
    public int UnreadTypes { get; }

    /// <summary>Type entries of the table.</summary>
    public int TypeCount => _byType.Length;

    public bool IsUnread(int typeIndex) => typeIndex >= _byType.Length || _byType[typeIndex] is null;

    /// <summary>The rows a type counts under: its interfaces, <see cref="TypeGroups.NoInterface"/>, or <see cref="TypeGroups.Unread"/>.</summary>
    public IReadOnlyList<string> Keys(int typeIndex) => IsUnread(typeIndex)
        ? [TypeGroups.Unread]
        : _byType[typeIndex] is { Length: > 0 } names ? names : [TypeGroups.NoInterface];

    /// <summary>
    /// Reads the table: for each type present, its interfaces through its first objects (up to <see cref="TriesPerType"/>,
    /// the next one tried when a read fails or the reader cannot tell). <paramref name="failures"/> gets the types left
    /// unread with the last error's type name ("no answer" when the reader returned nothing). Types with no object get an
    /// empty list (they are on no row).
    /// </summary>
    public static TypeInterfaces Read(HeapGraph graph, IObjectReader reader, List<(int Type, string Reason)> failures, IProgress<long>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(failures);
        var typeCount = graph.Types.Count;
        var examples = new int[typeCount * TriesPerType];
        Array.Fill(examples, -1);
        var found = new byte[typeCount];
        var typeIndices = graph.TypeIndices.Span;
        for (var o = 0; o < typeIndices.Length; o++)
        {
            if ((o & 0xFFFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }

            var t = typeIndices[o];
            if (found[t] < TriesPerType)
            {
                examples[(t * TriesPerType) + found[t]] = o;
                found[t]++;
            }
        }

        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        var byType = new string[]?[typeCount];
        for (var t = 0; t < typeCount; t++)
        {
            if ((t & 0xFF) == 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(t);
            }

            if (found[t] == 0)
            {
                byType[t] = [];
                continue;
            }

            var reason = "no answer";
            for (var i = 0; i < found[t] && byType[t] is null; i++)
            {
                try
                {
                    if (reader.ReadInterfaces(graph.Addresses[examples[(t * TriesPerType) + i]]) is { } read)
                    {
                        // One string per interface name across the table.
                        byType[t] = [.. read.Select(n => names.TryGetValue(n, out var known) ? known : names[n] = n)];
                    }
                }
                catch (Exception e) when (e is ArgumentException or InvalidOperationException or IOException)
                {
                    reason = e.GetType().Name;
                }
            }

            if (byType[t] is null)
            {
                failures.Add((t, reason));
            }
        }

        return new TypeInterfaces(byType);
    }
}
