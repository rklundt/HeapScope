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
