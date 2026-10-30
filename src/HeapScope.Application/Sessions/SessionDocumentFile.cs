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

/// <summary>The result of reading a session folder; <see cref="Reason"/> says why it is unreadable.</summary>
public readonly record struct SessionRead(SessionReadStatus Status, Session? Session, string? Reason);

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

    private static readonly TimeSpan s_readPatience = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan s_replacePatience = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan s_retryDelay = TimeSpan.FromMilliseconds(25);

    /// <summary>Reads the document of <paramref name="folder"/>.</summary>
    public static SessionRead Read(string folder)
    {
        var path = Path.Combine(folder, DocumentName);
        var deadline = DateTime.UtcNow + s_readPatience;
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
                    return Unreadable("its session.json is not a HeapScope session document");
                }

                if (document.FormatVersion != SessionDocument.CurrentFormatVersion)
                {
                    return Unreadable($"its session.json has format version {document.FormatVersion}; this build reads version {SessionDocument.CurrentFormatVersion}");
                }

                return document.Snapshots.All(s => s is { File.Length: > 0 })
                    ? new SessionRead(SessionReadStatus.Read, new Session(folder, document), null)
                    : Unreadable("its session.json lists a snapshot without a file");
            }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                return new SessionRead(SessionReadStatus.Missing, null, null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && DateTime.UtcNow < deadline)
            {
                // Held by another program without sharing (an older HeapScope writing, a scanner): try again shortly.
                Thread.Sleep(s_retryDelay);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
            {
                return Unreadable($"its session.json could not be read ({ex.Message})");
            }
        }
    }

    /// <summary>Writes a new document; fails when <paramref name="folder"/> has one already (claiming a new folder).</summary>
    public static void CreateNew(string folder, SessionDocument document)
    {
        using var stream = PrivateFiles.Open(Path.Combine(folder, DocumentName), FileMode.CreateNew, FileAccess.Write, FileShare.None);
        JsonSerializer.Serialize(stream, document, s_json);
        stream.Flush(flushToDisk: true);
    }

    /// <summary>Replaces the document of <paramref name="session"/>'s folder atomically. The caller holds the session lock.</summary>
    public static void Save(Session session)
    {
        var path = Path.Combine(session.Folder, DocumentName);
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        using (var stream = PrivateFiles.Open(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, session.Document, s_json);
            stream.Flush(flushToDisk: true);
        }

        var deadline = DateTime.UtcNow + s_replacePatience;
        while (true)
        {
            try
            {
                File.Move(temp, path, overwrite: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && DateTime.UtcNow < deadline)
            {
                Thread.Sleep(s_retryDelay);
            }
            catch
            {
                TryDelete(temp);
                throw;
            }
        }
    }

    private static SessionRead Unreadable(string reason) => new(SessionReadStatus.Unreadable, null, reason);

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
