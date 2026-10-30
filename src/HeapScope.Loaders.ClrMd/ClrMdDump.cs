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
using System.Security.Cryptography;
using HeapScope.Core;
using HeapScope.Core.Loading;
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
    public static ClrRuntime CreateRuntime(ClrInfo clr, string dumpPath, ClrMdLoaderOptions options) =>
        CreateRuntime(clr, dumpPath, options, out _);

    /// <summary>As <see cref="CreateRuntime(ClrInfo, string, ClrMdLoaderOptions)"/>, and says which DAC was used and why (sprint 2.3.7).</summary>
    public static ClrRuntime CreateRuntime(ClrInfo clr, string dumpPath, ClrMdLoaderOptions options, out DacChoice choice)
    {
        ArgumentNullException.ThrowIfNull(clr);
        ArgumentNullException.ThrowIfNull(options);
        // The DAC that runs on this system (sprint 2.3.5): a dump is read with the DAC of the platform HeapScope runs on,
        // so a Linux dump is read on Linux and a Windows dump on Windows (cross-platform DACs are not searched).
        var platform = CurrentPlatform();

        // A dump is read by a worker of its own system and architecture (ADR-025): say so before any DAC is looked for or
        // loaded (sprint 2.3.6 review; a DAC of the wrong architecture would only fail to load with a vaguer message). A
        // dump of another system can still be read with the symbol server's cross-platform DAC when that is allowed.
        if (DacLocator.Mismatch(clr.DataTarget.DataReader.TargetPlatform, clr.DataTarget.DataReader.Architecture, platform, RuntimeInformation.ProcessArchitecture, options.AllowMicrosoftSymbolServer) is { } mismatch)
        {
            throw new RuntimeComponentMissingException(mismatch);
        }

        var path = ResolveDac(clr, dumpPath, options, platform, out var request, out var fileName, out var looked, out var rule);
        if (path is null && !options.AllowMicrosoftSymbolServer)
        {
            throw new RuntimeComponentMissingException(request.RuntimeVersion, fileName, looked, Hint(request));
        }

        choice = new DacChoice(dumpPath, path, rule ?? "downloaded from Microsoft's symbol server", looked);

        try
        {
            // A DAC matched by build id is the exact build (its own id, or its runtime's with the pair checked), which is
            // stronger than ClrMD's version check; that check is skipped then, since ClrMD reads a core file's runtime
            // version as 0.0 at times (sprint 2.3.5, the Linux CI run).
            var runtime = path is null ? clr.CreateRuntime()
                : clr.CreateRuntime(path, ignoreMismatch: request.BuildId is { Length: > 0 });
            options.DacChosen?.Invoke(choice);
            return runtime;
        }
        catch (Exception ex) when (ex is ClrDiagnosticsException or InvalidOperationException or IOException or BadImageFormatException or DllNotFoundException)
        {
            throw new SnapshotFormatException(
                $"The .NET {request.RuntimeVersion} runtime in the dump could not be opened with {path ?? fileName}: {ex.Message} "
                + "Make sure the DAC is the one from that exact runtime version (Symbols:DacFolder), then open the dump again.",
                ex);
        }
    }

    /// <summary>
    /// Checks, without loading anything, that the DAC this dump's runtime needs may be loaded (sprint 3.6 review H1): a
    /// dump opened from its index cache loads no DAC at open, but its field reader will later, and only the open can ask
    /// the user to trust a DAC found next to the dump.
    /// </summary>
    /// <exception cref="UntrustedDacException">The only matching DAC lies next to the dump and is not trusted.</exception>
    public static void CheckDacTrust(string dumpPath, ClrMdLoaderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        try
        {
            using var target = OpenDataTarget(dumpPath, options);
            var platform = CurrentPlatform();
            if (target.ClrVersions.Length > 0
                && DacLocator.Mismatch(target.DataReader.TargetPlatform, target.DataReader.Architecture, platform, RuntimeInformation.ProcessArchitecture, options.AllowMicrosoftSymbolServer) is null)
            {
                _ = ResolveDac(target.ClrVersions[0], dumpPath, options, platform, out _, out _, out _, out _);
            }
        }
        catch (Exception ex) when (ex is not UntrustedDacException and not OperationCanceledException)
        {
            // Only a DAC that needs trust stops a cache open (sprint 3.6 re-review): a missing or unreadable DAC, or a
            // dump ClrMD cannot open, is reported by the field reader when it is used, as before.
        }
    }

    /// <summary>
    /// The DAC to load for <paramref name="clr"/>, or null when none is found locally: trusted folders first, then the
    /// dump's own folders, whose DAC is loaded only once trusted and from a private copy (ADR-035).
    /// </summary>
    /// <exception cref="UntrustedDacException">The only matching DAC lies next to the dump and is not trusted.</exception>
    private static string? ResolveDac(ClrInfo clr, string dumpPath, ClrMdLoaderOptions options, OSPlatform platform, out DacRequest request, out string fileName, out string[] looked, out string? rule)
    {
        // An entry with the DAC's own build id first, then one archived under the runtime's (sprint 2.3.5 review M1).
        var dac = clr.DebuggingLibraries
            .Where(d => d.Kind == DebugLibraryKind.Dac && d.Platform == platform)
            .OrderBy(d => d.IndexBuildId.IsDefaultOrEmpty ? 2 : d.ArchivedUnder == SymbolProperties.Coreclr ? 1 : 0)
            .FirstOrDefault();
        fileName = dac?.FileName is { Length: > 0 } name ? RecordedPath.FileName(name) : DefaultDacFileName(platform);
        var runtimeDirectory = Path.GetDirectoryName(clr.ModuleInfo.FileName);
        request = new DacRequest(
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
        var installedRoots = options.InstalledRuntimeRoots ?? DacLocator.DefaultRuntimeRoots();
        var directories = DacLocator.CandidateDirectories(request, dumpPath, options);
        var path = DacLocator.Find(request, directories, out var candidates, out rule, installedRoots);
        if (path is null)
        {
            // Next to the dump last, and only once trusted (peer review P2): that DAC came with the dump.
            var beside = DacLocator.Find(request, DacLocator.BesideDumpDirectories(request, dumpPath), out var besideCandidates, out var besideRule, installedRoots);
            candidates = [.. candidates, .. besideCandidates];
            if (beside is not null)
            {
                try
                {
                    var sha256 = Sha256Of(beside);
                    if (options.IsTrustedDac?.Invoke(sha256) != true)
                    {
                        // The files the trusted folders held and why they did not match go with the refusal (review H3).
                        throw new UntrustedDacException(beside, sha256, request.RuntimeVersion, [.. candidates.Select(c => c.ToString())]);
                    }

                    path = CopyTrusted(beside, sha256, options.TrustedDacCopyFolder);
                    rule = $"{besideRule}; next to the dump at {beside}, trusted by SHA-256 {sha256} and loaded from a private copy";
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Not the dump's fault (review): say it is the DAC or its private copy.
                    throw new RuntimeComponentMissingException(
                        $"The DAC next to the dump, {beside}, could not be read or copied into HeapScope's private DAC folder: {ex.Message} Close programs that hold it, check the free disk space, or put the DAC in the Symbols:DacFolder folder.",
                        ex);
                }
            }
        }

        looked = [.. candidates.Select(c => c.ToString())];
        return path;
    }

    /// <summary>
    /// What to do when no DAC was found, beyond installing the runtime (sprint 2.3.5 review): a DAC archived under its
    /// runtime library is only accepted together with that library. (A dump of another system or architecture is refused
    /// before the search, <see cref="DacLocator.Mismatch"/>.)
    /// </summary>
    internal static string? Hint(DacRequest request) =>
        request.BuildIdOfRuntime is { } runtime
            ? $"Copy {request.FileName} together with {runtime} from the same runtime folder: the dump identifies the DAC by its runtime library."
            : null;


    /// <summary>The SHA-256 of a file, lower-case hex.</summary>
    internal static string Sha256Of(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    /// <summary>
    /// Copies a trusted DAC into a private folder under its hash and returns the copy, checked against the hash: the file
    /// next to the dump could change between hashing and loading, its copy cannot (peer review P2).
    /// </summary>
    internal static string CopyTrusted(string path, string sha256, string? copyFolder)
    {
        var folder = Path.Combine(
            copyFolder ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HeapScope", "dac"),
            sha256);
        PrivateFiles.CreateDirectory(folder);
        var copy = Path.Combine(folder, Path.GetFileName(path));
        if (!File.Exists(copy) || Sha256Of(copy) != sha256)
        {
            var temp = $"{copy}.{Guid.NewGuid():N}.tmp";
            try
            {
                using (var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var target = PrivateFiles.Open(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    source.CopyTo(target);
                }

                File.Move(temp, copy, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && File.Exists(copy) && Sha256Of(copy) == sha256)
            {
                // Another worker (the app's and the CLI's) made the same copy meanwhile and may have it loaded: use it.
            }
            finally
            {
                File.Delete(temp);
            }
        }

        return Sha256Of(copy) == sha256
            ? copy
            : throw new IOException($"The copy of the trusted DAC {path} in {folder} does not match its SHA-256 {sha256}; the file changed while it was copied. Open the dump again.");
    }

    /// <summary>The platform HeapScope runs on, in ClrMD's terms.</summary>
    internal static OSPlatform CurrentPlatform() =>
        OperatingSystem.IsWindows() ? OSPlatform.Windows : OperatingSystem.IsMacOS() ? OSPlatform.OSX : OSPlatform.Linux;

    /// <summary>The DAC's file name on a platform, when the dump does not name it.</summary>
    internal static string DefaultDacFileName(OSPlatform platform) =>
        platform == OSPlatform.Windows ? "mscordaccore.dll" : platform == OSPlatform.OSX ? "libmscordaccore.dylib" : "libmscordaccore.so";
}
