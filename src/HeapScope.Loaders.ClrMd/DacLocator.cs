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
using HeapScope.Core;

namespace HeapScope.Loaders.ClrMd;

/// <summary>What the dump says about the data access component (DAC) its runtime needs.</summary>
/// <param name="FileName">DAC file name, for example <c>mscordaccore.dll</c>.</param>
/// <param name="TimeStamp">PE time stamp the dump records for the DAC; 0 when unknown.</param>
/// <param name="ImageSize">PE image size the dump records for the DAC; 0 when unknown.</param>
/// <param name="RuntimeVersion">Version of the dumped runtime, for example <c>10.0.13</c>.</param>
/// <param name="RuntimeDirectory">Folder of the runtime in the dumped process (a path on the machine that took the dump).</param>
/// <param name="BuildId">The identity a Linux or macOS dump records for the DAC (ELF build id, Mach-O UUID; sprint
/// 2.3.5); null for a Windows DAC, which is matched by <paramref name="TimeStamp"/> and <paramref name="ImageSize"/>.</param>
/// <param name="BuildIdOfRuntime">When set, <paramref name="BuildId"/> is the identity of this runtime library (for example
/// <c>libcoreclr.so</c>), under which the DAC is archived, not the DAC's own: the DAC is accepted from a folder whose
/// runtime library has that identity, the exact runtime it ships with.</param>
public sealed record DacRequest(
    string FileName, int TimeStamp, int ImageSize, string RuntimeVersion, string? RuntimeDirectory, byte[]? BuildId = null, string? BuildIdOfRuntime = null);

/// <summary>
/// Finds the DAC on this machine only (ADR-017): the dumped runtime's own folder when it exists here, the matching
/// installed shared runtime, the dump's folder and the user's DAC folder. A file is accepted only when its PE time
/// stamp and image size match what the dump records, so a DAC of another build is never loaded.
/// </summary>
public static class DacLocator
{
    /// <summary>
    /// The dumped runtime's version. ClrMD reads 0.0 when a core file does not hold the runtime's version string; the
    /// runtime's folder name (for example <c>10.0.14</c>) names it then (sprint 2.3.5).
    /// </summary>
    public static string RuntimeVersion(Version version, string? runtimeDirectory)
    {
        ArgumentNullException.ThrowIfNull(version);
        var folder = RecordedPath.FileName(runtimeDirectory ?? "");
        var dash = folder.IndexOf('-', StringComparison.Ordinal);
        return version.Major == 0 && version.Minor == 0 && Version.TryParse(dash > 0 ? folder[..dash] : folder, out var named)
            ? named.ToString()
            : version.ToString();
    }

    // Paths are compared without case on Windows only; Linux file systems are case sensitive (sprint 2.3.5 review).
    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

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
            if (!string.IsNullOrEmpty(directory) && IsLocalFixedPath(directory) && !result.Contains(directory, PathComparer))
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

    /// <summary>
    /// Returns the path of a matching DAC, or null, and the files that were looked at. A DAC found through its runtime
    /// library's identity (<see cref="DacRequest.BuildIdOfRuntime"/>) is accepted from an installed runtime folder (under
    /// <paramref name="installedRoots"/>, where the two ship together), or anywhere else only when both files embed the
    /// same version string (sprint 2.3.5 review M1: a folder filled by hand can pair a runtime with another build's DAC).
    /// </summary>
    public static string? Find(DacRequest request, IReadOnlyList<string> directories, out IReadOnlyList<string> searched, IReadOnlyList<string>? installedRoots = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(directories);
        var looked = new List<string>();
        searched = looked;
        foreach (var directory in directories)
        {
            var candidate = Path.Combine(directory, request.FileName);
            looked.Add(candidate);
            if (!IsReadableImage(candidate))
            {
                continue;
            }

            bool matches;
            if (request.BuildId is not { Length: > 0 } buildId)
            {
                matches = Matches(candidate, request.TimeStamp, request.ImageSize, request.RuntimeVersion);
            }
            else if (request.BuildIdOfRuntime is { } runtimeName)
            {
                var runtime = Path.Combine(directory, runtimeName);
                matches = IsReadableImage(runtime)
                    && ImageIdentity.Matches(runtime, buildId)
                    && (IsUnderAny(directory, installedRoots ?? [])
                        || (ImageIdentity.ReadVersionString(candidate) is { } dac && dac == ImageIdentity.ReadVersionString(runtime)));
            }
            else
            {
                matches = ImageIdentity.Matches(candidate, buildId);
            }

            if (matches)
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// True for a local file that can hold an image: on a local disk (<see cref="IsLocalFixedPath"/>) and at least a
    /// header long, which leaves out pipes, devices and <c>/proc</c> files (their size is 0), so opening one never blocks.
    /// </summary>
    public static bool IsReadableImage(string path)
    {
        try
        {
            return IsLocalFixedPath(path) && new FileInfo(path) is { Exists: true, Length: >= 64 };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
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
    /// device paths and network or removable drives. On Linux and macOS, the mount holding the path decides, after
    /// symbolic links are resolved (<see cref="UnixLocalPath"/>, sprint 2.3.5 review H1).
    /// </summary>
    public static bool IsLocalFixedPath(string path) =>
        OperatingSystem.IsWindows() ? IsLocalFixedWindowsPath(path) : UnixLocalPath.IsLocal(path, UnixLocalPath.CurrentMounts());

    private static bool IsLocalFixedWindowsPath(string path)
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
            return Directory.Exists(root) ? Directory.GetDirectories(root).Order(PathComparer).ToArray() : [];
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
            if (full.StartsWith(prefix, PathComparison) && !full.Contains("..", StringComparison.Ordinal))
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
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string?[] candidates = OperatingSystem.IsWindows()
            ? [Environment.GetEnvironmentVariable("DOTNET_ROOT"), Path.Combine(programFiles, "dotnet")]
            // The usual install folders on Linux and macOS (sprint 2.3.5 review): distribution packages, Microsoft's
            // installers and the install script's default.
            : [
                Environment.GetEnvironmentVariable("DOTNET_ROOT"), "/usr/share/dotnet", "/usr/lib/dotnet", "/usr/lib64/dotnet",
                "/usr/local/share/dotnet", "/opt/dotnet", home.Length > 0 ? Path.Combine(home, ".dotnet") : null,
            ];
        foreach (var dotnet in candidates.Append(
            Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(typeof(object).Assembly.Location)))))
        {
            if (string.IsNullOrEmpty(dotnet) || !Path.IsPathFullyQualified(dotnet))
            {
                continue;
            }

            // The last candidate is already the shared folder's parent (...\shared) for the running runtime.
            var shared = dotnet.EndsWith("shared", StringComparison.OrdinalIgnoreCase)
                ? Path.Combine(dotnet, "Microsoft.NETCore.App")
                : Path.Combine(dotnet, "shared", "Microsoft.NETCore.App");
            if (Directory.Exists(shared) && !roots.Contains(shared, PathComparer))
            {
                roots.Add(shared);
            }
        }

        return roots;
    }
}
