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
/// Append-only buffer made of fixed-size chunks, so growth never copies a huge array (ARCHITECTURE.md 6.3).
/// </summary>
internal sealed class ChunkedBuffer<T>
    where T : unmanaged
{
    public const int DefaultChunkLength = 1 << 20;

    private readonly int _chunkLength;
    private readonly List<T[]> _chunks = [];
    private T[] _current = [];
    private int _position;

    public ChunkedBuffer(int chunkLength = DefaultChunkLength)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(chunkLength, 1);
        _chunkLength = chunkLength;
    }

    public long Count { get; private set; }

    public void Add(T value)
    {
        if (_position == _current.Length)
        {
            _current = GC.AllocateUninitializedArray<T>(_chunkLength);
            _chunks.Add(_current);
            _position = 0;
        }

        _current[_position++] = value;
        Count++;
    }

    public T this[long index]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Count);
            return _chunks[(int)(index / _chunkLength)][index % _chunkLength];
        }
    }

    /// <summary>Copies every element into <paramref name="destination"/>, which must have room for <see cref="Count"/>.</summary>
    public void CopyTo(Span<T> destination)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(destination.Length, Count, nameof(destination));
        var remaining = Count;
        var offset = 0;
        foreach (var chunk in _chunks)
        {
            var length = (int)Math.Min(remaining, chunk.Length);
            chunk.AsSpan(0, length).CopyTo(destination.Slice(offset, length));
            offset += length;
            remaining -= length;
        }
    }

    /// <summary>Releases all chunks.</summary>
    public void Clear()
    {
        _chunks.Clear();
        _current = [];
        _position = 0;
        Count = 0;
    }
}
