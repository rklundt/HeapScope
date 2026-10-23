using System.Security.Cryptography;
using System.Text;

namespace HeapScope.Storage;

/// <summary>
/// Decides where a snapshot's index cache lives (ARCHITECTURE.md 7): next to the snapshot
/// (<c>&lt;snapshot&gt;.hsidx</c>) when that folder accepts a new file, otherwise under a fallback root
/// (<c>%LOCALAPPDATA%\HeapScope\cache</c> by default) named after a hash of the snapshot's full path.
/// </summary>
public sealed class IndexCacheLocator
{
    private readonly Func<string, bool> _canWrite;

    public IndexCacheLocator(string? fallbackRoot = null, Func<string, bool>? canWriteDirectory = null)
    {
        FallbackRoot = fallbackRoot ?? DefaultFallbackRoot;
        _canWrite = canWriteDirectory ?? CanWriteDirectory;
    }

    public static string DefaultFallbackRoot { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HeapScope", "cache");

    public string FallbackRoot { get; }

    public string GetCachePath(string snapshotPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(snapshotPath);
        var full = Path.GetFullPath(snapshotPath);
        var directory = Path.GetDirectoryName(full)!;
        if (_canWrite(directory))
        {
            return full + IndexCacheFormat.Extension;
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full.ToUpperInvariant())));
        return Path.Combine(FallbackRoot, hash + IndexCacheFormat.Extension);
    }

    /// <summary>True when a probe file can be created (and is removed) in <paramref name="directory"/>.</summary>
    public static bool CanWriteDirectory(string directory)
    {
        try
        {
            var probe = Path.Combine(directory, $".heapscope-probe-{Guid.NewGuid():N}");
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
            {
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }
}
