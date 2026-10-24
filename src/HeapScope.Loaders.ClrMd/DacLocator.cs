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

using System.Reflection.PortableExecutable;

namespace HeapScope.Loaders.ClrMd;

/// <summary>What the dump says about the data access component (DAC) its runtime needs.</summary>
/// <param name="FileName">DAC file name, for example <c>mscordaccore.dll</c>.</param>
/// <param name="TimeStamp">PE time stamp the dump records for the DAC; 0 when unknown.</param>
/// <param name="ImageSize">PE image size the dump records for the DAC; 0 when unknown.</param>
/// <param name="RuntimeVersion">Version of the dumped runtime, for example <c>10.0.13</c>.</param>
/// <param name="RuntimeDirectory">Folder of the runtime in the dumped process (a path on the machine that took the dump).</param>
public sealed record DacRequest(string FileName, int TimeStamp, int ImageSize, string RuntimeVersion, string? RuntimeDirectory);

/// <summary>
/// Finds the DAC on this machine only (ADR-017): the dumped runtime's own folder when it exists here, the matching
/// installed shared runtime, the dump's folder and the user's DAC folder. A file is accepted only when its PE time
/// stamp and image size match what the dump records, so a DAC of another build is never loaded.
/// </summary>
public static class DacLocator
{
    /// <summary>The folders searched, in order, without duplicates.</summary>
    public static IReadOnlyList<string> CandidateDirectories(DacRequest request, string dumpPath, ClrMdLoaderOptions options)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(options);
        var result = new List<string>();
        void Add(string? directory)
        {
            // Only folders on local fixed drives: a path recorded in the dump must never make the worker reach a
            // network share or load native code from one (ADR-017, section 12).
            if (!string.IsNullOrEmpty(directory) && IsLocalFixedPath(directory) && !result.Contains(directory, StringComparer.OrdinalIgnoreCase))
            {
                result.Add(directory);
            }
        }

        var roots = options.InstalledRuntimeRoots ?? DefaultRuntimeRoots();

        // The dumped runtime's own folder is a path chosen by the dump: it is searched only when it is an installed
        // shared runtime folder on this machine (for example a runtime with a different folder name than its version).
        if (options.SearchDumpedRuntimeDirectory && request.RuntimeDirectory is { } runtimeDirectory && IsUnderAny(runtimeDirectory, roots))
        {
            Add(runtimeDirectory);
        }

        foreach (var root in roots)
        {
            Add(Path.Combine(root, request.RuntimeVersion));
        }

        // The dump records the runtime's file version (10.0.1326.50504); installed folders use the product version
        // (10.0.13). Every installed runtime is a candidate; the PE match picks the right one.
        foreach (var root in roots)
        {
            foreach (var installed in EnumerateDirectories(root))
            {
                Add(installed);
            }
        }

        var dumpFolder = Path.GetDirectoryName(Path.GetFullPath(dumpPath));
        Add(dumpFolder);

        // HeapScope's capture keeps the target's DAC under dac\<file version> next to the dump (sprint 1.1).
        if (dumpFolder is not null)
        {
            Add(Path.Combine(dumpFolder, "dac", request.RuntimeVersion));
        }
        Add(options.DacFolder);
        return result;
    }

    /// <summary>Returns the path of a matching DAC, or null, and the files that were looked at.</summary>
    public static string? Find(DacRequest request, IReadOnlyList<string> directories, out IReadOnlyList<string> searched)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(directories);
        var looked = new List<string>();
        searched = looked;
        foreach (var directory in directories)
        {
            var candidate = Path.Combine(directory, request.FileName);
            looked.Add(candidate);
            if (File.Exists(candidate) && Matches(candidate, request.TimeStamp, request.ImageSize, request.RuntimeVersion))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// True when the PE file is the expected image: its time stamp and image size equal the dump's index. When the dump
    /// records neither (both 0), the file's version must equal <paramref name="runtimeVersion"/> instead, so a DAC of
    /// another build is never accepted. A file that is not a readable PE image never matches.
    /// </summary>
    public static bool Matches(string path, int timeStamp, int imageSize, string? runtimeVersion = null)
    {
        try
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var reader = new PEReader(stream))
            {
                var headers = reader.PEHeaders;
                if (headers.PEHeader is not { } peHeader)
                {
                    return false;
                }

                if (timeStamp != 0 || imageSize != 0)
                {
                    return (timeStamp == 0 || headers.CoffHeader.TimeDateStamp == timeStamp)
                        && (imageSize == 0 || peHeader.SizeOfImage == imageSize);
                }
            }

            var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
            return Version.TryParse(runtimeVersion, out var expected)
                && new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, info.FilePrivatePart) == expected;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException or FileNotFoundException)
        {
            return false;
        }
    }

    /// <summary>
    /// True for a fully qualified path on a local fixed drive; false for UNC paths (<c>\\server\share</c>, <c>\\?\UNC</c>),
    /// device paths and network or removable drives.
    /// </summary>
    public static bool IsLocalFixedPath(string path)
    {
        if (string.IsNullOrEmpty(path) || path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal)
            || !Path.IsPathFullyQualified(path))
        {
            return false;
        }

        try
        {
            return Path.GetPathRoot(path) is { Length: > 0 } root && new DriveInfo(root).DriveType == DriveType.Fixed;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static IEnumerable<string> EnumerateDirectories(string root)
    {
        try
        {
            return Directory.Exists(root) ? Directory.GetDirectories(root).Order(StringComparer.OrdinalIgnoreCase).ToArray() : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static bool IsUnderAny(string directory, IReadOnlyList<string> roots)
    {
        var full = Path.TrimEndingDirectorySeparator(directory);
        foreach (var root in roots)
        {
            var prefix = Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar;
            if (full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && !full.Contains("..", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Shared runtime folders of the dotnet installations on this machine.</summary>
    public static IReadOnlyList<string> DefaultRuntimeRoots()
    {
        var roots = new List<string>();
        foreach (var dotnet in new[]
        {
            Environment.GetEnvironmentVariable("DOTNET_ROOT"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet"),
            Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(typeof(object).Assembly.Location))),
        })
        {
            if (string.IsNullOrEmpty(dotnet))
            {
                continue;
            }

            // The last candidate is already the shared folder's parent (...\shared) for the running runtime.
            var shared = dotnet.EndsWith("shared", StringComparison.OrdinalIgnoreCase)
                ? Path.Combine(dotnet, "Microsoft.NETCore.App")
                : Path.Combine(dotnet, "shared", "Microsoft.NETCore.App");
            if (Directory.Exists(shared) && !roots.Contains(shared, StringComparer.OrdinalIgnoreCase))
            {
                roots.Add(shared);
            }
        }

        return roots;
    }
}
