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

using System.Security.Cryptography;
using System.Text.Json;
using HeapScope.Core;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace HeapScope.Application.Snapshots;

/// <summary>One DAC the user trusts to load from next to a dump, and why.</summary>
public sealed record TrustedDac(string Sha256, string Path, DateTimeOffset TrustedUtc, string Reason);

/// <summary>
/// The DACs (data access components) trusted to load from next to a dump (sprint 3.6, peer review P2), by SHA-256. A DAC
/// is native code the worker runs; one that came with a dump is the dump maker's code, so it is loaded only once it is
/// trusted here: by the user (the app's question, kept in <c>trusted-dacs.json</c> beside the session library), for one
/// run (the CLI's <c>--trust-dac</c>), or because HeapScope's own capture copied it from the target's runtime.
/// Thread-safe.
/// </summary>
public sealed class TrustedDacs
{
    public const string FileName = "trusted-dacs.json";

    private const int FormatVersion = 1;

    private static readonly JsonSerializerOptions s_json = new() { WriteIndented = true };

    private readonly object _gate = new();
    private readonly HashSet<string> _forThisRun = new(StringComparer.Ordinal);
    private readonly ILogger _logger;

    /// <param name="folder">The folder of the file; null for HeapScope's data folder.</param>
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
            return [.. Read().Select(d => d.Sha256).Concat(_forThisRun).Distinct(StringComparer.Ordinal)];
        }
    }

    /// <summary>The kept entries, for showing and logging.</summary>
    public IReadOnlyList<TrustedDac> Kept()
    {
        lock (_gate)
        {
            return Read();
        }
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

        _logger.LogInformation("DAC {Sha256} trusted for this run", normalized);
    }

    /// <summary>Keeps <paramref name="sha256"/> as trusted, with the file it was found at and why.</summary>
    public void Trust(string sha256, string path, string reason)
    {
        if (!IsSha256(sha256))
        {
            throw new ArgumentException($"{sha256} is not a SHA-256 (64 lower-case hex digits).", nameof(sha256));
        }

        lock (_gate)
        {
            var kept = Read();
            if (kept.Any(d => d.Sha256 == sha256))
            {
                return;
            }

            Write([.. kept, new TrustedDac(sha256, path, DateTimeOffset.UtcNow, reason)]);
        }

        _logger.LogInformation("DAC {Sha256} at {Path} trusted: {Reason}", sha256, path, reason);
    }

    /// <summary>Trusts the DAC at <paramref name="path"/> by its hash (HeapScope's own capture copied it); false when unreadable.</summary>
    public bool TrustFile(string path, string reason)
    {
        try
        {
            Trust(Sha256Of(path), path, reason);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "The DAC {Path} could not be trusted", path);
            return false;
        }
    }

    private List<TrustedDac> Read()
    {
        try
        {
            if (!File.Exists(Path))
            {
                return [];
            }

            var file = JsonSerializer.Deserialize<TrustedDacFile>(File.ReadAllBytes(Path), s_json);
            return file is { FormatVersion: FormatVersion, Dacs: { } dacs } ? [.. dacs.Where(d => IsSha256(d.Sha256))] : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Trusting nothing is the safe reading of a file that cannot be read: the user is asked again.
            _logger.LogWarning(ex, "{Path} could not be read; no kept DAC is trusted", Path);
            return [];
        }
    }

    private void Write(IReadOnlyList<TrustedDac> dacs)
    {
        PrivateFiles.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var temp = $"{Path}.{Guid.NewGuid():N}.tmp";
        using (var stream = PrivateFiles.Open(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, new TrustedDacFile(FormatVersion, dacs), s_json);
        }

        File.Move(temp, Path, overwrite: true);
    }

    private sealed record TrustedDacFile(int FormatVersion, IReadOnlyList<TrustedDac> Dacs);
}
