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

using System.Runtime.InteropServices;
using HeapScope.Core;
using Microsoft.Diagnostics.Runtime;

namespace HeapScope.Loaders.ClrMd;

/// <summary>Opens a dump with ClrMD the same way for the loader and the object reader (ADR-017 DAC lookup).</summary>
internal static class ClrMdDump
{
    /// <summary>Opens the dump with the memory-mapped reader and the local-only file locator unless downloads are on.</summary>
    /// <exception cref="SnapshotFormatException">The file is not a readable memory dump.</exception>
    public static DataTarget OpenDataTarget(string path, ClrMdLoaderOptions options)
    {
        var targetOptions = new DataTargetOptions();
        targetOptions.CacheOptions.MaxDumpCacheSize = options.MaxDumpCacheBytes;
        targetOptions.UseLockFreeMemoryMapReader = true;
        if (!options.AllowMicrosoftSymbolServer)
        {
            targetOptions.FileLocator = LocalOnlyFileLocator.Instance;
        }

        try
        {
            return DataTarget.LoadDump(path, targetOptions);
        }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or IOException && ex is not FileNotFoundException)
        {
            throw new SnapshotFormatException($"{path} is not a readable memory dump: {ex.Message}", ex);
        }
    }

    /// <summary>The dump's first .NET runtime.</summary>
    /// <exception cref="SnapshotFormatException">The dump holds no .NET runtime.</exception>
    public static ClrInfo FirstRuntime(DataTarget target, string path) =>
        target.ClrVersions.Length > 0
            ? target.ClrVersions[0]
            : throw new SnapshotFormatException($"No .NET runtime was found in {path}. HeapScope reads dumps of .NET (Core) processes.");

    /// <summary>
    /// Creates the runtime with a local DAC (ADR-017), or a downloaded one when allowed. A DAC that is found but fails to
    /// load (wrong build, damaged file) is reported with a next step (from the 1.2 review).
    /// </summary>
    /// <exception cref="RuntimeComponentMissingException">The matching DAC is not on this machine.</exception>
    /// <exception cref="SnapshotFormatException">The runtime could not be opened with the DAC found.</exception>
    public static ClrRuntime CreateRuntime(ClrInfo clr, string dumpPath, ClrMdLoaderOptions options)
    {
        // The DAC that runs on this system (sprint 2.3.5): a dump is read with the DAC of the platform HeapScope runs on,
        // so a Linux dump is read on Linux and a Windows dump on Windows (cross-platform DACs are not searched).
        var platform = CurrentPlatform();
        var dac = clr.DebuggingLibraries.FirstOrDefault(d =>
            d.Kind == DebugLibraryKind.Dac && d.Platform == platform);
        var fileName = dac?.FileName is { Length: > 0 } name ? RecordedPath.FileName(name) : DefaultDacFileName(platform);
        var runtimeDirectory = Path.GetDirectoryName(clr.ModuleInfo.FileName);
        var request = new DacRequest(
            fileName,
            dac?.IndexTimeStamp ?? 0,
            dac?.IndexFileSize ?? 0,
            DacLocator.RuntimeVersion(clr.Version, runtimeDirectory),
            runtimeDirectory)
        {
            // A Linux or macOS DAC is archived under its own build id or under the runtime library's; when the dump lists
            // none, the runtime library's own id still identifies the exact runtime the DAC ships with.
            BuildId = dac is { IndexBuildId.IsDefaultOrEmpty: false } ? [.. dac.IndexBuildId]
                : platform != OSPlatform.Windows && !clr.ModuleInfo.BuildId.IsDefaultOrEmpty ? [.. clr.ModuleInfo.BuildId]
                : null,
            BuildIdOfRuntime = platform == OSPlatform.Windows ? null
                : dac is { IndexBuildId.IsDefaultOrEmpty: false } && dac.ArchivedUnder != SymbolProperties.Coreclr ? null
                : RecordedPath.FileName(clr.ModuleInfo.FileName),
        };
        var directories = DacLocator.CandidateDirectories(request, dumpPath, options);
        var path = DacLocator.Find(request, directories, out var searched);
        if (path is null && !options.AllowMicrosoftSymbolServer)
        {
            throw new RuntimeComponentMissingException(request.RuntimeVersion, fileName, searched);
        }

        try
        {
            // A DAC matched by build id is the exact one (stronger than a version); ClrMD's own version check is skipped
            // then, since it reads a core file's runtime version as 0.0 at times (sprint 2.3.5, the Linux CI run).
            return path is null ? clr.CreateRuntime()
                : clr.CreateRuntime(path, ignoreMismatch: request.BuildId is { Length: > 0 } && clr.Version.Major == 0);
        }
        catch (Exception ex) when (ex is ClrDiagnosticsException or InvalidOperationException or IOException or BadImageFormatException or DllNotFoundException)
        {
            throw new SnapshotFormatException(
                $"The .NET {request.RuntimeVersion} runtime in the dump could not be opened with {path ?? fileName}: {ex.Message} "
                + "Make sure the DAC is the one from that exact runtime version (Symbols:DacFolder), then open the dump again.",
                ex);
        }
    }

    /// <summary>The platform HeapScope runs on, in ClrMD's terms.</summary>
    internal static OSPlatform CurrentPlatform() =>
        OperatingSystem.IsWindows() ? OSPlatform.Windows : OperatingSystem.IsMacOS() ? OSPlatform.OSX : OSPlatform.Linux;

    /// <summary>The DAC's file name on a platform, when the dump does not name it.</summary>
    internal static string DefaultDacFileName(OSPlatform platform) =>
        platform == OSPlatform.Windows ? "mscordaccore.dll" : platform == OSPlatform.OSX ? "libmscordaccore.dylib" : "libmscordaccore.so";
}
