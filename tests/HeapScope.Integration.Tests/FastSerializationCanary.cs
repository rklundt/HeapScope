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

using FastSerialization;

namespace HeapScope.Integration.Tests;

/// <summary>
/// A type a crafted gcdump names (sprint 1.9): it counts its constructions, so a test can tell whether the gcdump reader
/// creates types it was not told about.
/// </summary>
public sealed class FastSerializationCanary : IFastSerializable
{
    public FastSerializationCanary() => Interlocked.Increment(ref s_created);

    private static int s_created;

    public static int Created
    {
        get => Volatile.Read(ref s_created);
        set => Volatile.Write(ref s_created, value);
    }

    public void ToStream(Serializer serializer)
    {
    }

    public void FromStream(Deserializer deserializer)
    {
    }
}
