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

namespace HeapScope.Core.Graph;

/// <summary>
/// A read-only column of unmanaged values (ADR-005, ADR-015). Backed by a managed array or by native memory
/// owned by the graph (a <see cref="System.Buffers.MemoryManager{T}"/> over a mapped view). Algorithms take
/// <see cref="Span"/> once and work on the span; spans must not outlive the query that obtained them.
/// </summary>
public readonly struct Column<T> : IEquatable<Column<T>>
    where T : unmanaged
{
    private readonly ReadOnlyMemory<T> _memory;

    public Column(ReadOnlyMemory<T> memory)
    {
        _memory = memory;
    }

    public static Column<T> Empty => default;

    public int Length => _memory.Length;

    public ReadOnlySpan<T> Span => _memory.Span;

    public ReadOnlyMemory<T> Memory => _memory;

    /// <summary>Bytes held by this column.</summary>
    public long ByteLength => (long)_memory.Length * System.Runtime.CompilerServices.Unsafe.SizeOf<T>();

    /// <summary>Single element access. Convenient but slower than taking <see cref="Span"/> once in loops.</summary>
    public T this[int index] => _memory.Span[index];

    public static Column<T> FromArray(T[] array) => new(array);

    public static bool operator ==(Column<T> left, Column<T> right) => left.Equals(right);

    public static bool operator !=(Column<T> left, Column<T> right) => !left.Equals(right);

    public bool Equals(Column<T> other) => _memory.Equals(other._memory);

    public override bool Equals(object? obj) => obj is Column<T> other && Equals(other);

    public override int GetHashCode() => _memory.GetHashCode();
}
