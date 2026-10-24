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
/// Provides the memory for the final, exact-size columns of a graph being built (ARCHITECTURE.md 6.3). The
/// managed allocator returns arrays; a file-backed allocator can write columns straight into index cache sections
/// without changing the builder.
/// </summary>
public interface IColumnAllocator
{
    /// <summary>Allocates a writable column. Contents are unspecified; the builder overwrites every element.</summary>
    Memory<T> Allocate<T>(string columnName, int length)
        where T : unmanaged;

    /// <summary>
    /// Called once when the graph is built. Returns the object that owns the allocated memory (the graph disposes
    /// it), or null when the memory is garbage collected.
    /// </summary>
    IDisposable? DetachOwner();
}

/// <summary>Allocates columns as managed arrays.</summary>
public sealed class ManagedColumnAllocator : IColumnAllocator
{
    public static ManagedColumnAllocator Instance { get; } = new();

    public Memory<T> Allocate<T>(string columnName, int length)
        where T : unmanaged
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        return length == 0 ? Memory<T>.Empty : GC.AllocateUninitializedArray<T>(length);
    }

    public IDisposable? DetachOwner() => null;
}
