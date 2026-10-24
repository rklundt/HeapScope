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

namespace Fixtures.Leaks;

/// <summary>
/// One object with fields of every kind and known values, for field inspection (sprint 1.5, known answer
/// fixtures/KnownAnswers/Inspection.json). Properties are auto-properties, as in real code, so their fields are compiler
/// backing fields that the inspection shows under the property name.
/// </summary>
public static class InspectionFixtures
{
    private static InspectionSample? s_sample;

    /// <summary>Creates the sample and keeps it alive (rooted by a static field).</summary>
    public static InspectionSample Create() => s_sample ??= new InspectionSample();

    public static bool Created => s_sample is not null;
}

public enum InspectionColor
{
    Red = 1,
    Green = 2,
    Blue = 4,
}

[Flags]
public enum InspectionAccess
{
    None = 0,
    Read = 1,
    Write = 2,
    Execute = 4,
}

public readonly record struct InspectionPoint(int X, int Y);

public readonly record struct InspectionBox(InspectionPoint TopLeft, InspectionPoint BottomRight, string Label);

public readonly record struct InspectionPair(int Key, string Value);

public sealed class InspectionSample
{
    public int Number { get; } = 42;

    public long Big { get; } = 9_000_000_000;

    public double Ratio { get; } = 0.25;

    public bool Flag { get; } = true;

    public char Letter { get; } = 'Q';

    public byte Small { get; } = 7;

    public InspectionColor Color { get; } = InspectionColor.Green;

    public string Text { get; } = "HeapScope inspection";

    public string? Missing { get; }

    public InspectionBox Box { get; } = new(new InspectionPoint(1, 2), new InspectionPoint(30, 40), "box");

    public int[] Numbers { get; } = [3, 1, 4, 1, 5];

    public string[] Words { get; } = ["alpha", "beta"];

    public InspectionChild Child { get; } = new(99);

    /// <summary>A multi-dimensional array: elements are named [row,column] (sprint 1.5 review).</summary>
    public int[,] Grid { get; } = new int[,] { { 1, 2, 3 }, { 4, 5, 6 } };

    /// <summary>An array of structs holding references: references are named [i].Value.</summary>
    public InspectionPair[] Pairs { get; } = [new(1, "one"), new(2, "two")];

    /// <summary>Ten slots holding one object: its references name the first slots and count the rest.</summary>
    public InspectionChild[] Repeated { get; } = Enumerable.Repeat(new InspectionChild(7), 10).ToArray();

    /// <summary>A boxed primitive in an object field (sprint 1.6): shown with its value.</summary>
    public object Boxed { get; } = 12345;

    /// <summary>A [Flags] combination: shown by its flag names.</summary>
    public InspectionAccess Access { get; } = InspectionAccess.Read | InspectionAccess.Write;

    /// <summary>A string longer than the field display cut (1,000 characters).</summary>
    public string LongText { get; } = new('x', 1_500);

    /// <summary>An array of enums.</summary>
    public InspectionColor[] Colors { get; } = [InspectionColor.Red, InspectionColor.Blue];
}

public sealed class InspectionChild(int id)
{
    public int Id { get; } = id;
}
