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
using System.Text.Json;
using System.Text.Json.Serialization;
using HeapScope.Core;

namespace HeapScope.Application.Sessions;

/// <summary>What reading a session folder's <c>session.json</c> found.</summary>
public enum SessionReadStatus
{
    /// <summary>A well-formed document of this format version.</summary>
    Read,

    /// <summary>No folder or no document: nothing to read.</summary>
    Missing,

    /// <summary>A document exists but could not be read or is not one this build understands.</summary>
    Unreadable,
}

/// <summary>
/// The result of reading a session folder; <see cref="Reason"/> says why it is unreadable and <see cref="NextStep"/> what
/// to do about it (sprint 3.6 review: held, damaged and newer documents need different steps).
/// </summary>
public readonly record struct SessionRead(SessionReadStatus Status, Session? Session, string? Reason, string? NextStep = null);

/// <summary>A session's document exists but cannot be used; the message says why and what to do.</summary>
public sealed class SessionUnreadableException(string message, Exception? inner = null) : IOException(message, inner);

/// <summary>
/// The file protocol of <c>session.json</c>, shared by every reader and writer (peer review P1). Writers hold
/// <c>session.lock</c> and replace the document atomically; readers take no lock, so they open the document sharing read,
/// write and delete (a reader never makes a writer's replace fail) and retry briefly when another program holds it. The
/// replace itself retries for a bounded time, for readers of older builds, virus scanners and indexers.
/// </summary>
internal static class SessionDocumentFile
{
    public const string DocumentName = "session.json";

    private static readonly JsonSerializerOptions s_json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private const string Untouched = "The dumps in that folder are untouched.";

    private const string Elsewhere = "Open another session (the app's Home page, or the CLI's --session), or restart HeapScope to start a new one.";

    private static readonly TimeSpan s_readPatience = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan s_replacePatience = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan s_retryDelay = TimeSpan.FromMilliseconds(25);

    /// <summary>Reads the document of <paramref name="folder"/>.</summary>
    public static SessionRead Read(string folder)
    {
        var path = Path.Combine(folder, DocumentName);
        var watch = Stopwatch.StartNew();
        while (true)
        {
            try
            {
                byte[] bytes;
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    bytes = new byte[stream.Length];
                    stream.ReadExactly(bytes);
                }

                var document = JsonSerializer.Deserialize<SessionDocument>(bytes, s_json);
                if (document is not { Name: not null, Snapshots: not null })
                {
                    return Unreadable("its session.json is not a HeapScope session document", $"{Elsewhere} {Untouched}");
                }

                if (document.FormatVersion != SessionDocument.CurrentFormatVersion)
                {
                    return Unreadable(
                        $"its session.json has format version {document.FormatVersion}; this build reads version {SessionDocument.CurrentFormatVersion}",
                        $"Use the HeapScope build that wrote it, or {Elsewhere.ToLowerInvariant()[..1]}{Elsewhere[1..]} {Untouched}");
                }

                return document.Snapshots.All(s => s is { File.Length: > 0 })
                    ? new SessionRead(SessionReadStatus.Read, new Session(folder, document), null)
                    : Unreadable("its session.json lists a snapshot without a file", $"{Elsewhere} {Untouched}");
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return new SessionRead(SessionReadStatus.Missing, null, null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && watch.Elapsed < s_readPatience)
            {
                // Held by another program without sharing (an older HeapScope writing, a scanner): try again shortly.
                Thread.Sleep(s_retryDelay);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return Unreadable($"its session.json could not be read ({ex.Message})", "Close programs that may hold it (another HeapScope, a backup or sync tool) and try again.");
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                return Unreadable($"its session.json is damaged ({ex.Message})", $"{Elsewhere} {Untouched}");
            }
        }
    }

    /// <summary>
    /// Writes a new document; fails with an <see cref="IOException"/> when <paramref name="folder"/> has one already
    /// (claiming a new folder). Written whole beside it and moved into place without overwriting (sprint 3.6 review), so a
    /// reader never sees a half-written or empty document.
    /// </summary>
    public static void CreateNew(string folder, SessionDocument document)
    {
        var path = Path.Combine(folder, DocumentName);
        var temp = WriteTemp(path, document);
        try
        {
            File.Move(temp, path, overwrite: false);
        }
        finally
        {
            TryDelete(temp);
        }
    }

    /// <summary>Replaces the document of <paramref name="session"/>'s folder atomically. The caller holds the session lock.</summary>
    public static void Save(Session session)
    {
        var path = Path.Combine(session.Folder, DocumentName);
        var temp = WriteTemp(path, session.Document);
        var watch = Stopwatch.StartNew();
        try
        {
            while (true)
            {
                try
                {
                    File.Move(temp, path, overwrite: true);
                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && ex is not DirectoryNotFoundException && watch.Elapsed < s_replacePatience)
                {
                    Thread.Sleep(s_retryDelay);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"{path} could not be replaced after {watch.Elapsed.TotalSeconds:N0} s of retries: {ex.Message}", ex);
        }
        finally
        {
            TryDelete(temp);
        }
    }

    private static string WriteTemp(string path, SessionDocument document)
    {
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using var stream = PrivateFiles.Open(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            JsonSerializer.Serialize(stream, document, s_json);
            stream.Flush(flushToDisk: true);
            return temp;
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    private static SessionRead Unreadable(string reason, string nextStep) => new(SessionReadStatus.Unreadable, null, reason, nextStep);

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
