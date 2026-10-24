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

namespace HeapScope.Core.Loading;

/// <summary>
/// Tells snapshot files apart by their first bytes, never by their extension (sprint 1.8): a minidump starts with
/// <c>MDMP</c>; a gcdump is a FastSerialization stream (a length-prefixed <c>!FastSerialization.1</c>) whose first object
/// is a <c>GCHeapDump</c>.
/// </summary>
public static class SnapshotFileSignature
{
    /// <summary>Bytes read from the start of the file; the gcdump's first type name lies well within them.</summary>
    public const int ProbeLength = 256;

    private static readonly byte[] s_minidump = "MDMP"u8.ToArray();
    private static readonly byte[] s_fastSerialization = "!FastSerialization.1"u8.ToArray();
    private static readonly byte[] s_gcHeapDump = "GCHeapDump"u8.ToArray();

    /// <summary>The kind of snapshot file at <paramref name="path"/>, or null when it is neither kind.</summary>
    public static SnapshotKind? Detect(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        Span<byte> buffer = stackalloc byte[ProbeLength];
        int read;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        }

        return Detect(buffer[..read]);
    }

    /// <summary>The kind of snapshot whose file starts with <paramref name="start"/>, or null.</summary>
    public static SnapshotKind? Detect(ReadOnlySpan<byte> start)
    {
        if (start.StartsWith(s_minidump))
        {
            return SnapshotKind.FullDump;
        }

        if (start.Length >= 4 + s_fastSerialization.Length
            && BitConverter.ToInt32(start) == s_fastSerialization.Length
            && start[4..].StartsWith(s_fastSerialization)
            && start.IndexOf(s_gcHeapDump) >= 0)
        {
            return SnapshotKind.GcDump;
        }

        return null;
    }

    /// <summary>What to tell the user about a file that is not a snapshot.</summary>
    public static string DescribeUnknown(string path) =>
        $"{path} is neither a full memory dump nor a gcdump, so it cannot be opened. Open a .dmp written by HeapScope, dotnet-dump, procdump or Task Manager, or a .gcdump written by HeapScope, dotnet-gcdump or PerfView.";
}
