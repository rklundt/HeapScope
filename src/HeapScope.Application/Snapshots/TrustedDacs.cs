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

using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using HeapScope.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HeapScope.Application.Snapshots;

/// <summary>One DAC the user trusts to load from next to a dump, and why.</summary>
public sealed record TrustedDac(string Sha256, string Path, DateTimeOffset TrustedUtc, string Reason);

/// <summary>
/// The DACs (data access components) trusted to load from next to a dump (sprint 3.6, peer review P2, ADR-035), by
/// SHA-256. A DAC is native code the worker runs; one that came with a dump is the dump maker's code, so it is loaded only
/// once it is trusted here: by the user (the app's question), for one run (the CLI's <c>--trust-dac</c>), or because
/// HeapScope's own capture copied it from the target's installed runtime.
/// <para>
/// The kept entries live in <c>trusted-dacs.json</c> in the user's own HeapScope data folder, never beside a configured
/// session root, which could be shared (review). The file follows the session document's protocol: a change takes
/// <c>trusted-dacs.lock</c> across processes, reads share read, write and delete and retry briefly, and the replace retries.
/// A file this build cannot read (damaged, another format) trusts nothing and is never overwritten silently: it is kept
/// as <c>.bak</c> before a change writes a new one, and the log says so. Thread-safe.
/// </para>
/// </summary>
public sealed class TrustedDacs
{
    public const string FileName = "trusted-dacs.json";

    private const int FormatVersion = 1;

    private static readonly JsonSerializerOptions s_json = new() { WriteIndented = true };
    private static readonly TimeSpan s_patience = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan s_retryDelay = TimeSpan.FromMilliseconds(25);

    private readonly object _gate = new();
    private readonly HashSet<string> _forThisRun = new(StringComparer.Ordinal);
    private readonly ILogger _logger;

    /// <param name="folder">The folder of the file; null for HeapScope's own data folder in the user's profile.</param>
    public TrustedDacs(string? folder = null, ILogger<TrustedDacs>? logger = null)
    {
        Path = System.IO.Path.Combine(folder ?? Bookmarks.BookmarkStore.DefaultFolder, FileName);
        _logger = logger ?? (ILogger)NullLogger.Instance;
    }

    public string Path { get; }

    /// <summary>True for a SHA-256 written as 64 lower-case hex digits.</summary>
    public static bool IsSha256(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigitLower);

    /// <summary>The SHA-256 of a file, lower-case hex.</summary>
    public static string Sha256Of(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    /// <summary>Every trusted hash: the kept ones and this run's.</summary>
    public IReadOnlyList<string> All()
    {
        lock (_gate)
        {
            return [.. Read().Dacs.Select(d => d.Sha256).Concat(_forThisRun).Distinct(StringComparer.Ordinal)];
        }
    }

    /// <summary>The kept entries, for showing and logging.</summary>
    public IReadOnlyList<TrustedDac> Kept()
    {
        lock (_gate)
        {
            return Read().Dacs;
        }
    }

    /// <summary>
    /// Who trusted each hash and when, for the log line of every open (sprint 3.6 review H2): the worker's DAC choice line
    /// names the hash it loaded, and this line, under the same trace id, says where that trust came from.
    /// </summary>
    public string Describe(int max = 20)
    {
        List<TrustedDac> kept;
        string[] run;
        lock (_gate)
        {
            kept = Read().Dacs;
            run = [.. _forThisRun.Where(h => kept.All(d => d.Sha256 != h))];
        }

        if (kept.Count == 0 && run.Length == 0)
        {
            return "none";
        }

        var entries = kept.OrderByDescending(d => d.TrustedUtc).Select(d => string.Create(CultureInfo.InvariantCulture, $"{d.Sha256} at {d.Path}, {d.Reason}, since {d.TrustedUtc:yyyy-MM-dd HH:mm} UTC"))
            .Concat(run.Select(h => $"{h} for this run (--trust-dac)"))
            .ToList();
        var shown = string.Join("; ", entries.Take(max));
        return entries.Count > max ? string.Create(CultureInfo.InvariantCulture, $"{shown}; and {entries.Count - max} more") : shown;
    }

    /// <summary>Trusts <paramref name="sha256"/> for this run only (the CLI's <c>--trust-dac</c>); nothing is written.</summary>
    public void TrustForThisRun(string sha256)
    {
        var normalized = sha256?.Trim().ToLowerInvariant();
        if (!IsSha256(normalized))
        {
            throw new ArgumentException($"{sha256} is not a SHA-256 (64 hex digits).", nameof(sha256));
        }

        lock (_gate)
        {
            _forThisRun.Add(normalized!);
        }

        _logger.LogInformation("DAC {Sha256} trusted for this run (--trust-dac)", normalized);
    }

    /// <summary>Keeps <paramref name="sha256"/> as trusted, with the file it was found at and why.</summary>
    /// <exception cref="IOException">The file could not be changed (held by another program for more than 5 seconds).</exception>
    public void Trust(string sha256, string path, string reason)
    {
        if (!IsSha256(sha256))
        {
            throw new ArgumentException($"{sha256} is not a SHA-256 (64 lower-case hex digits).", nameof(sha256));
        }

        lock (_gate)
        {
            PrivateFiles.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            using var fileLock = AcquireLock();
            var read = Read();
            if (read.Failure is { } failure)
            {
                throw new IOException($"{Path} could not be read, so the trust was not saved: {failure.Message} Close other programs that hold it and try again.", failure);
            }

            if (read.Dacs.Any(d => d.Sha256 == sha256))
            {
                _logger.LogInformation("DAC {Sha256} at {Path} is trusted already ({Reason})", sha256, path, reason);
                return;
            }

            if (read.Problem is { } problem)
            {
                // Never overwrite what this build cannot read: a newer build's list or a damaged one is kept beside it.
                var kept = $"{Path}.{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}.bak";
                File.Copy(Path, kept, overwrite: false);
                _logger.LogWarning("{Path} {Problem}; kept it as {Kept} and started a new list", Path, problem, kept);
            }

            Write([.. read.Dacs, new TrustedDac(sha256, path, DateTimeOffset.UtcNow, reason)]);
        }

        _logger.LogInformation("DAC {Sha256} at {Path} trusted: {Reason}", sha256, path, reason);
    }

    /// <summary>
    /// Trusts the DAC at <paramref name="path"/> when its content equals <paramref name="expectedSha256"/> (a capture's copy
    /// checked against the runtime's file it came from); false, logged, when it cannot be read or differs.
    /// </summary>
    public bool TrustFile(string path, string expectedSha256, string reason)
    {
        try
        {
            var sha256 = Sha256Of(path);
            if (sha256 != expectedSha256)
            {
                _logger.LogWarning("The DAC {Path} differs from the file it was copied from ({Sha256} against {Expected}); not trusted", path, sha256, expectedSha256);
                return false;
            }

            Trust(sha256, path, reason);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "The DAC {Path} could not be trusted", path);
            return false;
        }
    }

    private (List<TrustedDac> Dacs, string? Problem, Exception? Failure) Read()
    {
        var watch = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                byte[] bytes;
                using (var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    bytes = new byte[stream.Length];
                    stream.ReadExactly(bytes);
                }

                var file = JsonSerializer.Deserialize<TrustedDacFile>(bytes, s_json);
                if (file is not { FormatVersion: FormatVersion, Dacs: { } dacs })
                {
                    var problem = file is null ? "holds no list" : string.Create(CultureInfo.InvariantCulture, $"has format version {file.FormatVersion}, which this build does not read");
                    _logger.LogWarning("{Path} {Problem}; no kept DAC is trusted", Path, problem);
                    return ([], problem, null);
                }

                var valid = dacs.Where(d => IsSha256(d.Sha256)).ToList();
                if (valid.Count < dacs.Count)
                {
                    _logger.LogWarning("{Path} has {Count} entries without a valid SHA-256; they are not trusted", Path, dacs.Count - valid.Count);
                }

                return (valid, null, null);
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return ([], null, null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && watch.Elapsed < TimeSpan.FromSeconds(1))
            {
                Thread.Sleep(s_retryDelay);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Held or denied: trusting nothing is the safe reading, but the list itself is fine, so a change must not
                // start a new one over it (re-review): it is refused instead.
                _logger.LogWarning(ex, "{Path} could not be read; no kept DAC is trusted", Path);
                return ([], null, ex);
            }
            catch (JsonException ex)
            {
                // Trusting nothing is the safe reading of a damaged file: the user is asked again.
                _logger.LogWarning(ex, "{Path} is damaged; no kept DAC is trusted", Path);
                return ([], $"is damaged ({ex.Message})", null);
            }
        }
    }

    private FileStream AcquireLock()
    {
        var path = System.IO.Path.ChangeExtension(Path, ".lock");
        var watch = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                // A lasting lock file: deleting it on close lets a waiter lock an unlinked file on Linux and macOS.
                return PrivateFiles.Open(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1);
            }
            catch (IOException) when (watch.Elapsed < s_patience)
            {
                Thread.Sleep(s_retryDelay);
            }
        }
    }

    private void Write(IReadOnlyList<TrustedDac> dacs)
    {
        var temp = $"{Path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = PrivateFiles.Open(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, new TrustedDacFile(FormatVersion, dacs), s_json);
                stream.Flush(flushToDisk: true);
            }

            var watch = Stopwatch.StartNew();
            while (true)
            {
                try
                {
                    File.Move(temp, Path, overwrite: true);
                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && watch.Elapsed < s_patience)
                {
                    Thread.Sleep(s_retryDelay);
                }
            }
        }
        catch
        {
            try
            {
                File.Delete(temp);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }

            throw;
        }
    }

    private sealed record TrustedDacFile(int FormatVersion, IReadOnlyList<TrustedDac> Dacs);
}
