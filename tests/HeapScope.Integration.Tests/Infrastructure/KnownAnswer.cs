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

using System.Text.Json;

namespace HeapScope.Integration.Tests.Infrastructure;

/// <summary>One file of fixtures/KnownAnswers (format version 1, described in its README).</summary>
internal sealed record KnownAnswer(
    int FormatVersion,
    string Name,
    string Kind,
    string Description,
    IReadOnlyList<KnownType> Types,
    IReadOnlyList<KnownType>? Census,
    IReadOnlyList<KnownRoot>? Roots,
    RuntimeRoot? RuntimeRoot,
    string? GrowingCollection,
    IReadOnlyList<string>? PathShape,
    RetainedBytes? MinRetainedBytesPerItem,
    IReadOnlyList<string>? PresentButUnreachable,
    IReadOnlyList<HeapKindAnswer>? HeapKinds,
    IReadOnlyList<FieldAnswer>? Fields,
    IReadOnlyList<FindingAnswer>? Findings,
    string Notes)
{
    public const int CurrentFormatVersion = 1;

    private static readonly JsonSerializerOptions s_options = new() { PropertyNameCaseInsensitive = true };

    public KnownRoot PrimaryRoot => Roots is { Count: > 0 } roots ? roots[0] : throw new InvalidOperationException($"{Name} has no roots.");

    public static KnownAnswer Load(string name) =>
        JsonSerializer.Deserialize<KnownAnswer>(File.ReadAllText(Path.Combine(TestPaths.KnownAnswers, name + ".json")), s_options)
        ?? throw new InvalidOperationException($"Known answer {name} is empty.");

    public static IEnumerable<string> AllNames() =>
        Directory.EnumerateFiles(TestPaths.KnownAnswers, "*.json").Select(Path.GetFileNameWithoutExtension).Select(n => n!);

    /// <summary>
    /// Matches a path (type names from the root's value object to the item) against a path shape: an ordered
    /// subsequence where each step must be contained in a path type name after generic arguments are removed, steps
    /// ending in "?" are optional, and the first step "Type.field" names the root (README).
    /// </summary>
    public static bool MatchesShape(IReadOnlyList<string> shape, string rootLabel, IReadOnlyList<string> path)
    {
        var names = new List<string> { rootLabel };
        names.AddRange(path.Select(DumpAnalysis.StripGenericArguments));
        var cursor = 0;
        foreach (var rawStep in shape)
        {
            var optional = rawStep.EndsWith('?');
            var step = optional ? rawStep[..^1] : rawStep;
            var found = -1;
            for (var i = cursor; i < names.Count; i++)
            {
                if (names[i].Contains(step, StringComparison.Ordinal))
                {
                    found = i;
                    break;
                }
            }

            if (found < 0)
            {
                if (optional)
                {
                    continue;
                }

                return false;
            }

            cursor = found + 1;
        }

        return true;
    }
}

internal sealed record KnownType(string Type, int PerItem, bool Exact = true, int? ShallowBytes = null);

internal sealed record KnownRoot(string Kind, string DeclaringType, string Field, string? ItemType = null);

internal sealed record RuntimeRoot(string RootKind, string Container);

internal sealed record RetainedBytes(int Base, int PayloadMultiplier);

internal sealed record HeapKindAnswer(string Type, string Field, string HeapKind);

/// <summary>
/// An expected field of an inspected object: its display path and either its value text, its reference type, or (for a
/// long value) the start of its text and its display length.
/// </summary>
internal sealed record FieldAnswer(string Path, string? Value, string? Type, string? ValuePrefix = null, int? Length = null);

/// <summary>What a simple inspection must report (sprint 1.6; README, `findings`).</summary>
internal sealed record FindingAnswer(string Inspection, string Key, long Count, bool Exact = true);
