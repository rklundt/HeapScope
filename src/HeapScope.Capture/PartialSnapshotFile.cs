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

namespace HeapScope.Capture;

/// <summary>
/// A snapshot file being written (sprint 1.9, shared by full dump and gcdump capture): written under a temporary name
/// (<see cref="DumpCapturer.PartialSuffix"/>), renamed to its final name only when complete, never overwriting an existing
/// file, and removed when the capture fails or is canceled, retrying while a writer still holds it.
/// </summary>
public sealed class PartialSnapshotFile
{
    private readonly string _noun;

    private PartialSnapshotFile(string fullPath, string directory, string noun)
    {
        FullPath = fullPath;
        Directory = directory;
        PartialPath = fullPath + DumpCapturer.PartialSuffix;
        _noun = noun;
    }

    /// <summary>The final path.</summary>
    public string FullPath { get; }

    /// <summary>Where the file is written until it is complete.</summary>
    public string PartialPath { get; }

    /// <summary>The folder of both.</summary>
    public string Directory { get; }

    /// <summary>The folder of <paramref name="path"/>, which must be a file path.</summary>
    public static string DirectoryOf(string path, string noun) =>
        Path.GetDirectoryName(Path.GetFullPath(path))
        ?? throw new ArgumentException($"{path} is not a file path; give a file name for the {noun}.", nameof(path));

    /// <summary>
    /// Reserves <paramref name="path"/>: creates its folder and fails with <see cref="DumpFileExistsException"/> when the
    /// file or its partial file exists. <paramref name="noun"/> names the snapshot in messages ("dump", "gcdump").
    /// </summary>
    public static PartialSnapshotFile Reserve(string path, string noun)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var fullPath = Path.GetFullPath(path);
        var file = new PartialSnapshotFile(fullPath, DirectoryOf(fullPath, noun), noun);
        System.IO.Directory.CreateDirectory(file.Directory);
        if (File.Exists(file.FullPath) || File.Exists(file.PartialPath))
        {
            throw new DumpFileExistsException($"{file.FullPath} already exists; HeapScope never overwrites a {noun}. Choose another file name.");
        }

        return file;
    }

    /// <summary>Renames the complete partial file to its final name.</summary>
    /// <exception cref="DumpWriteFailedException">Nothing was written, or the rename failed.</exception>
    /// <exception cref="DumpFileExistsException">A file of the final name appeared meanwhile; the new one is removed.</exception>
    public void Commit()
    {
        if (!File.Exists(PartialPath))
        {
            throw new DumpWriteFailedException($"The capture reported success but no {_noun} was written to {FullPath}.");
        }

        try
        {
            File.Move(PartialPath, FullPath, overwrite: false);
        }
        catch (IOException ex)
        {
            Discard();
            throw File.Exists(FullPath)
                ? new DumpFileExistsException($"{FullPath} appeared while the {_noun} was written; the new {_noun} was removed. Capture again with another name.", ex)
                : new DumpWriteFailedException($"The {_noun} could not be moved to {FullPath}: {ex.Message}", ex);
        }

        try
        {
            // The runtime wrote it with the folder's permissions; a folder the user picked may let others read it, and a
            // dump holds every secret of the process (sprint 3.7, peer review P15).
            HeapScope.Core.PrivateFiles.Protect(FullPath);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            // The target's account owns it (an elevated capture of another user's process): its own permissions stay.
        }
    }

    /// <summary>Deletes the partial file; if a writer still holds it, retries for up to 30 seconds in the background.</summary>
    public void Discard()
    {
        if (TryDelete(PartialPath))
        {
            return;
        }

        var path = PartialPath;
        _ = Task.Run(async () =>
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(250).ConfigureAwait(false);
                if (TryDelete(path))
                {
                    return;
                }
            }
        });
    }

    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return !File.Exists(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
