using System.Runtime.InteropServices;
using HeapScope.Core;

namespace HeapScope.Loaders.ClrMd;

/// <summary>
/// The fixed header of a Windows minidump (MINIDUMP_HEADER): signature, time stamp and the MINIDUMP_TYPE flags. Read
/// before ClrMD opens the file, so a file that is not a minidump, or a minidump without the managed heap, fails with a
/// clear message instead of producing an empty or partial graph (sprint 1.2 review).
/// </summary>
public readonly record struct MinidumpHeader(uint Signature, DateTimeOffset? CapturedAtUtc, ulong Flags)
{
    /// <summary>"MDMP" in little-endian.</summary>
    public const uint MinidumpSignature = 0x504D444D;

    /// <summary>MiniDumpWithFullMemory: every readable page of the process.</summary>
    public const ulong WithFullMemory = 0x2;

    /// <summary>MiniDumpWithPrivateReadWriteMemory: private read-write pages, which hold the GC heap (heap dumps).</summary>
    public const ulong WithPrivateReadWriteMemory = 0x200;

    private const int Length = 32;

    /// <summary>True when the dump contains the pages the GC heap lives in.</summary>
    public bool HasHeapMemory => (Flags & (WithFullMemory | WithPrivateReadWriteMemory)) != 0;

    /// <summary>Reads and checks the header.</summary>
    /// <exception cref="SnapshotFormatException">Not a minidump, or a minidump without the managed heap.</exception>
    public static MinidumpHeader Read(string path)
    {
        Span<byte> bytes = stackalloc byte[Length];
        int read;
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            read = stream.ReadAtLeast(bytes, Length, throwOnEndOfStream: false);
        }

        var signature = read >= 4 ? MemoryMarshal.Read<uint>(bytes) : 0;
        if (read < Length || signature != MinidumpSignature)
        {
            throw new SnapshotFormatException(
                $"{path} is not a Windows memory dump. HeapScope reads full dumps (.dmp) of .NET processes; gcdump files are planned for a later version.");
        }

        var seconds = MemoryMarshal.Read<uint>(bytes[20..]);
        var header = new MinidumpHeader(signature, seconds == 0 ? null : DateTimeOffset.FromUnixTimeSeconds(seconds), MemoryMarshal.Read<ulong>(bytes[24..]));
        if (!header.HasHeapMemory)
        {
            throw new SnapshotFormatException(
                $"{path} is a minidump without the managed heap, so there are no objects to analyze. Capture a full dump instead: " +
                "dotnet-dump collect --type Full (or Heap), procdump -ma, or Task Manager's \"Create dump file\".");
        }

        return header;
    }
}
