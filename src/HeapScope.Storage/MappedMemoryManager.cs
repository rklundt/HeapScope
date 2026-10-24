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

using System.Buffers;
using System.IO.MemoryMappedFiles;

namespace HeapScope.Storage;

/// <summary>
/// Exposes part of a read-only memory-mapped view as <see cref="Memory{T}"/>, so a <see cref="Core.Graph.Column{T}"/>
/// can be backed by the index cache file (ADR-015). The view's lifetime is owned by <see cref="MappedIndexFile"/>;
/// this manager neither frees nor pins anything.
/// </summary>
internal sealed unsafe class MappedMemoryManager<T>(byte* pointer, int length) : MemoryManager<T>
    where T : unmanaged
{
    public override Span<T> GetSpan() => new(pointer, length);

    public override MemoryHandle Pin(int elementIndex = 0) => new((T*)pointer + elementIndex);

    public override void Unpin()
    {
    }

    protected override void Dispose(bool disposing)
    {
    }
}

/// <summary>
/// Owns a read-only mapping of an index cache file, created from the same handle the reader validated, and the
/// pointer acquired on its view. Disposal is idempotent and thread safe.
/// </summary>
internal sealed unsafe class MappedIndexFile : IDisposable
{
    private readonly MemoryMappedFile _file;
    private readonly MemoryMappedViewAccessor _view;
    private int _disposed;

    /// <summary>Takes ownership of <paramref name="stream"/> (it is disposed with the mapping).</summary>
    public MappedIndexFile(FileStream stream)
    {
        _file = MemoryMappedFile.CreateFromFile(stream, mapName: null, capacity: 0, MemoryMappedFileAccess.Read, HandleInheritability.None, leaveOpen: false);
        try
        {
            _view = _file.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            byte* pointer = null;
            _view.SafeMemoryMappedViewHandle.AcquirePointer(ref pointer);
            Pointer = pointer + _view.PointerOffset;
            Length = (long)_view.SafeMemoryMappedViewHandle.ByteLength - _view.PointerOffset;
        }
        catch
        {
            _view?.Dispose();
            _file.Dispose();
            throw;
        }
    }

    public byte* Pointer { get; }

    /// <summary>Bytes addressable from <see cref="Pointer"/>.</summary>
    public long Length { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _view.SafeMemoryMappedViewHandle.ReleasePointer();
        _view.Dispose();
        _file.Dispose();
    }
}
