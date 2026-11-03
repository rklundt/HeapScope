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

using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Diagnostics.Runtime;

namespace HeapScope.Integration.Tests.Infrastructure;

/// <summary>
/// ClrMD-based checks used to verify the known answers until HeapScope's own loader and analyses exist (sprint 0.4
/// decision 6). Deliberately simple: breadth-first walks over the whole heap of small fixture dumps.
/// </summary>
public sealed class DumpAnalysis : IDisposable
{
    /// <summary>Marks a step reached through a dependent handle in paths returned by <see cref="FindPath"/>.</summary>
    public const string DependentHandleStep = "(dependent handle)";

    private readonly DataTarget _target;

    private DumpAnalysis(DataTarget target, ClrRuntime runtime, string dacPath, LocalImageLocator locator)
    {
        _target = target;
        Runtime = runtime;
        DacPath = dacPath;
        Locator = locator;
    }

    public ClrRuntime Runtime { get; }

    /// <summary>The DAC this check loaded: the one beside the dumped runtime on this machine.</summary>
    public string DacPath { get; }

    /// <summary>What ClrMD asked the file locator for while opening the dump, and whether a local file answered.</summary>
    internal LocalImageLocator Locator { get; }

    public ClrHeap Heap => Runtime.Heap;

    /// <summary>
    /// Opens a fixture dump taken on this machine, independently of HeapScope's loader and DAC lookup, and locally only: the
    /// DAC is the one beside the dumped runtime, and a missing image is looked for only at its recorded local path (2026-11-03:
    /// ClrMD's default locator can fall back to Microsoft's symbol server, and one CI run failed with "Could not find
    /// matching DAC for this runtime" where HeapScope's local-only loader had just opened the same dump). A failure says
    /// what was looked for and found, never dump contents.
    /// </summary>
    public static DumpAnalysis Open(string path) => Open(path, runtimeFolder: null);

    /// <summary>As <see cref="Open(string)"/>, with the DAC taken from <paramref name="runtimeFolder"/> (tests of the failure).</summary>
    internal static DumpAnalysis Open(string path, string? runtimeFolder)
    {
        var locator = new LocalImageLocator();
        var watch = Stopwatch.StartNew();
        // The signature check stays on; recording it shows whether it ran and when (ADR-035 leaves open whether it applies).
        var target = DataTarget.LoadDump(path, new DataTargetOptions
        {
            FileLocator = locator,
            DacSignatureVerificationOverride = dac => locator.RecordSignatureCheck(dac),
        });
        var loadTime = watch.Elapsed;
        try
        {
            if (target.ClrVersions.Length == 0)
            {
                throw new InvalidOperationException($"No .NET runtime found in {path}.");
            }

            var clr = target.ClrVersions[0];
            var folder = runtimeFolder ?? Path.GetDirectoryName(clr.ModuleInfo.FileName) ?? string.Empty;
            var dac = Path.Combine(folder, DacFileName(target.DataReader.TargetPlatform));
            if (!File.Exists(dac))
            {
                throw new InvalidOperationException(Describe($"No DAC beside the dumped runtime: {dac} does not exist.", path, target, clr, dac, locator, loadTime, null, null));
            }

            watch.Restart();
            try
            {
                return new DumpAnalysis(target, clr.CreateRuntime(dac, ignoreMismatch: false), dac, locator);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(Describe("ClrMD could not create the runtime with the local DAC.", path, target, clr, dac, locator, loadTime, watch.Elapsed, ex), ex);
            }
        }
        catch
        {
            target.Dispose();
            throw;
        }
    }

    private static string DacFileName(OSPlatform platform) =>
        platform == OSPlatform.Windows ? "mscordaccore.dll" : platform == OSPlatform.OSX ? "libmscordaccore.dylib" : "libmscordaccore.so";

    /// <summary>
    /// Everything that explains a failed open from the CI log alone: identities, paths, sizes and times only. Each line is
    /// guarded, so a line that cannot be read never hides the original error.
    /// </summary>
    private static string Describe(string problem, string path, DataTarget target, ClrInfo clr, string dac, LocalImageLocator locator, TimeSpan loadTime, TimeSpan? createTime, Exception? error)
    {
        var text = new StringBuilder(problem).AppendLine();
        Line(text, "Dump", () => $"{path}; {target.DataReader.TargetPlatform} {target.DataReader.Architecture}, pointer size {target.DataReader.PointerSize}");
        Line(text, "Runtime", () => $"{clr.Flavor} {clr.Version}; module {clr.ModuleInfo.FileName}; index time stamp {clr.IndexTimeStamp:x8}, size {clr.IndexFileSize}");
        Line(text, "Debugging library", () => string.Join(" | ", clr.DebuggingLibraries.Select(l =>
            $"{l.Kind} {l.FileName}; {l.Platform} {l.TargetArchitecture}; index time stamp {l.IndexTimeStamp:x8}, size {l.IndexFileSize}; archived under {l.ArchivedUnder}")));
        Line(text, "DAC beside the runtime", () => $"{dac}; {DescribeFile(dac)}");
        Line(text, "Runtimes installed", () => string.Join(" | ", InstalledRuntimeFolders()));
        Line(text, "Locator", () => locator.Requests.Count == 0 ? "asked for nothing" : string.Join(" | ", locator.Requests));
        Line(text, "Times", () => $"LoadDump {loadTime.TotalMilliseconds:F0} ms, CreateRuntime {(createTime is { } created ? created.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture) + " ms" : "not reached")}");
        Line(text, "Test host", () => $"{RuntimeInformation.FrameworkDescription} from {Path.GetDirectoryName(typeof(object).Assembly.Location)}; ClrMD {typeof(DataTarget).Assembly.GetName().Version}");
        Line(text, "Environment", () => $"DOTNET_ROOT {Environment.GetEnvironmentVariable("DOTNET_ROOT") ?? "(not set)"}; _NT_SYMBOL_PATH {(Environment.GetEnvironmentVariable("_NT_SYMBOL_PATH") is null ? "not set" : "set")}; runner image {Environment.GetEnvironmentVariable("ImageOS") ?? "-"} {Environment.GetEnvironmentVariable("ImageVersion") ?? "-"}");
        for (var inner = error; inner is not null; inner = inner.InnerException)
        {
            var current = inner;
            Line(text, "Error", () => $"{current.GetType().FullName} (HResult 0x{current.HResult:x8}): {current.Message}");
        }

        return text.ToString();
    }

    private static void Line(StringBuilder text, string label, Func<string> read)
    {
        string value;
        try
        {
            value = read();
        }
        catch (Exception ex)
        {
            value = $"(could not be read: {ex.GetType().Name})";
        }

        text.Append("  ").Append(label).Append(": ").AppendLine(value);
    }

    /// <summary>The version folders of each .NET install root this machine has (DOTNET_ROOT and the default one).</summary>
    private static IEnumerable<string> InstalledRuntimeFolders()
    {
        string?[] roots = [Environment.GetEnvironmentVariable("DOTNET_ROOT"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet")];
        return roots.OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(root => Path.Combine(root, "shared", "Microsoft.NETCore.App"))
            .Where(Directory.Exists)
            .SelectMany(Directory.EnumerateDirectories);
    }

    private static string DescribeFile(string file)
    {
        if (!File.Exists(file))
        {
            return "missing";
        }

        var info = new FileInfo(file);
        var identity = LocalImageLocator.ReadIdentity(file) is { } id ? $"PE time stamp {id.TimeStamp:x8}, image size {id.ImageSize}" : "not a readable PE image";
        return $"{info.Length} bytes, {identity}, version {FileVersionInfo.GetVersionInfo(file).FileVersion}, written {info.LastWriteTimeUtc:u}";
    }

    /// <summary>Counts instances of each requested type name in one heap walk.</summary>
    public Dictionary<string, long> CountTypes(IEnumerable<string> typeNames)
    {
        var counts = typeNames.Distinct(StringComparer.Ordinal).ToDictionary(n => n, _ => 0L, StringComparer.Ordinal);
        foreach (var obj in Heap.EnumerateObjects())
        {
            if (obj.Type?.Name is { } name && counts.TryGetValue(name, out var count))
            {
                counts[name] = count + 1;
            }
        }

        return counts;
    }

    public IEnumerable<ClrObject> Instances(string typeName) =>
        Heap.EnumerateObjects().Where(o => o.Type?.Name == typeName);

    /// <summary>Reads the 4-byte object header word (the sync block index and flag bits) that precedes the object.</summary>
    public uint ReadObjectHeader(ulong address) =>
        _target.DataReader.Read<uint>(address - 4);

    /// <summary>Reads a static reference field (first app domain where it is set).</summary>
    public ClrObject ReadStatic(string declaringType, string field)
    {
        var type = Heap.GetTypeByName(declaringType)
            ?? throw new InvalidOperationException($"Type {declaringType} not found in the dump.");
        var staticField = type.GetStaticFieldByName(field)
            ?? throw new InvalidOperationException($"Static field {declaringType}.{field} not found.");
        foreach (var domain in Runtime.AppDomains)
        {
            var value = staticField.ReadObject(domain);
            if (!value.IsNull)
            {
                return value;
            }
        }

        throw new InvalidOperationException($"Static field {declaringType}.{field} is null in every app domain.");
    }

    /// <summary>Counts objects of <paramref name="typeName"/> reachable from <paramref name="start"/>.</summary>
    public int CountReachable(ClrObject start, string typeName, bool followDependentHandles) =>
        Walk([start], followDependentHandles, skip: null).Count(o => Heap.GetObject(o).Type?.Name == typeName);

    /// <summary>
    /// Objects reachable from the retaining GC roots (ClrMD's root enumeration; weak handles are not roots) and from every
    /// object in a frozen segment, which is never collected (ADR-024, sprint 2.3 review M12), following dependent handles
    /// only from reachable primaries, never entering <paramref name="skip"/>. Without <paramref name="finalizerQueue"/>, the
    /// objects waiting for their finalizer are not roots: HeapScope's own walk counts them as dead (sprint 3.5).
    /// </summary>
    public HashSet<ulong> ReachableFromRoots(ISet<ulong>? skip = null, bool finalizerQueue = true)
    {
        var frozen = Heap.Segments.Where(s => s.Kind == GCSegmentKind.Frozen).SelectMany(s => s.EnumerateObjects());
        var starts = Heap.EnumerateRoots().Where(r => finalizerQueue || r.RootKind != ClrRootKind.FinalizerQueue).Select(r => r.Object).Concat(frozen).Where(o => !o.IsNull);
        return Walk(starts, followDependentHandles: true, skip);
    }

    /// <summary>
    /// Retained bytes of <paramref name="anchor"/>: the size of everything reachable from it that is no longer reachable
    /// from the roots once the anchor itself is removed (the dominator definition, computed by brute force).
    /// </summary>
    public ulong RetainedBytes(ClrObject anchor)
    {
        var withoutAnchor = ReachableFromRoots(new HashSet<ulong> { anchor.Address });
        ulong total = 0;
        foreach (var address in Walk([anchor], followDependentHandles: true, skip: null))
        {
            if (!withoutAnchor.Contains(address))
            {
                total += Heap.GetObject(address).Size;
            }
        }

        return total;
    }

    /// <summary>
    /// What every object of <paramref name="typeName"/> retains together (sprint 2.4, the 2.4 review), by brute force: the
    /// bytes reachable from the roots now, less those still reachable without entering the set. <paramref name="reachable"/>
    /// is <see cref="ReachableFromRoots"/>, passed in so several sets share one walk.
    /// </summary>
    public ulong SetRetainedBytes(string typeName, HashSet<ulong> reachable) => SetRetainedBytes([typeName], reachable);

    /// <summary>What every object of any of <paramref name="typeNames"/> retains together (sprint 3.1, a set of types).</summary>
    public ulong SetRetainedBytes(IReadOnlyCollection<string> typeNames, HashSet<ulong> reachable)
    {
        ArgumentNullException.ThrowIfNull(typeNames);
        ArgumentNullException.ThrowIfNull(reachable);
        var set = Heap.EnumerateObjects().Where(o => o.Type?.Name is { } name && typeNames.Contains(name)).Select(o => o.Address).ToHashSet();
        var without = ReachableFromRoots(set);
        ulong total = 0;
        foreach (var address in reachable)
        {
            if (!without.Contains(address))
            {
                total += Heap.GetObject(address).Size;
            }
        }

        return total;
    }

    /// <summary>
    /// Shortest path (type names) from <paramref name="start"/> to the first object of <paramref name="targetType"/>,
    /// with <see cref="DependentHandleStep"/> inserted before steps reached through a dependent handle.
    /// </summary>
    public IReadOnlyList<string>? FindPath(ClrObject start, string targetType, bool followDependentHandles)
    {
        var parents = new Dictionary<ulong, (ulong Parent, bool Dependent)> { [start.Address] = (0, false) };
        var queue = new Queue<ClrObject>();
        queue.Enqueue(start);
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (current.Type?.Name == targetType)
            {
                return BuildPath(current.Address, parents);
            }

            var strong = current.EnumerateReferenceAddresses(carefully: false, considerDependantHandles: false).ToHashSet();
            foreach (var reference in current.EnumerateReferences(carefully: false, considerDependantHandles: followDependentHandles))
            {
                if (parents.TryAdd(reference.Address, (current.Address, !strong.Contains(reference.Address))))
                {
                    queue.Enqueue(reference);
                }
            }
        }

        return null;
    }

    /// <summary>For each object in <paramref name="targets"/>: the kind of the GC root it is reached from and the type of its referrer.</summary>
    public Dictionary<ulong, (string RootKind, string? ReferrerType)> RootOrigins(IReadOnlySet<ulong> targets)
    {
        var result = new Dictionary<ulong, (string, string?)>();
        var seen = new HashSet<ulong>();
        foreach (var root in Heap.EnumerateRoots())
        {
            if (root.Object.IsNull)
            {
                continue;
            }

            var queue = new Queue<(ClrObject Object, string? Referrer)>();
            if (seen.Add(root.Object.Address))
            {
                queue.Enqueue((root.Object, null));
            }

            while (queue.Count > 0)
            {
                var (current, referrer) = queue.Dequeue();
                if (targets.Contains(current.Address))
                {
                    result.TryAdd(current.Address, (root.RootKind.ToString(), referrer));
                }

                foreach (var reference in current.EnumerateReferences(carefully: false, considerDependantHandles: true))
                {
                    if (seen.Add(reference.Address))
                    {
                        queue.Enqueue((reference, current.Type?.Name));
                    }
                }
            }
        }

        return result;
    }

    public void Dispose()
    {
        Runtime.Dispose();
        _target.Dispose();
    }

    /// <summary>Removes generic argument lists (but keeps the empty "&lt;&gt;" of compiler-generated names).</summary>
    public static string StripGenericArguments(string name)
    {
        var builder = new StringBuilder(name.Length);
        var depth = 0;
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (c == '<' && i + 1 < name.Length && name[i + 1] != '>')
            {
                depth++;
            }
            else if (c == '>' && depth > 0)
            {
                depth--;
            }
            else if (depth == 0)
            {
                builder.Append(c);
            }
        }

        return builder.ToString();
    }

    private HashSet<ulong> Walk(IEnumerable<ClrObject> starts, bool followDependentHandles, ISet<ulong>? skip)
    {
        var visited = new HashSet<ulong>();
        var queue = new Queue<ClrObject>();
        foreach (var start in starts)
        {
            if ((skip is null || !skip.Contains(start.Address)) && visited.Add(start.Address))
            {
                queue.Enqueue(start);
            }
        }

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var reference in current.EnumerateReferences(carefully: false, considerDependantHandles: followDependentHandles))
            {
                if ((skip is null || !skip.Contains(reference.Address)) && visited.Add(reference.Address))
                {
                    queue.Enqueue(reference);
                }
            }
        }

        return visited;
    }

    private List<string> BuildPath(ulong last, Dictionary<ulong, (ulong Parent, bool Dependent)> parents)
    {
        var steps = new List<string>();
        var current = last;
        while (current != 0)
        {
            var (parent, dependent) = parents[current];
            steps.Add(Heap.GetObject(current).Type?.Name ?? "?");
            if (dependent)
            {
                steps.Add(DependentHandleStep);
            }

            current = parent;
        }

        steps.Reverse();
        return steps;
    }
}

/// <summary>
/// A file locator for fixture dumps taken on this machine: an image is the file at its recorded path when its PE time
/// stamp and size match, never a download. It records each request, so a test can show that nothing would have gone
/// to a symbol server. Separate from HeapScope's own locator on purpose (this is the independent check).
/// </summary>
internal sealed class LocalImageLocator : IFileLocator
{
    private readonly List<string> _requests = [];

    public IReadOnlyList<string> Requests
    {
        get
        {
            lock (_requests)
            {
                return [.. _requests];
            }
        }
    }

    /// <summary>
    /// The requests no local file answered. Empty means nothing the check needed so far was missing locally; it does not
    /// show what ClrMD's default locator would do, only that this check never needs anything but local files.
    /// </summary>
    public IReadOnlyList<string> Unanswered => [.. Requests.Where(r => r.EndsWith(": not found locally", StringComparison.Ordinal))];

    public string? FindPEImage(string fileName, int buildTimeStamp, int imageSize, bool checkProperties)
    {
        var found = File.Exists(fileName) && ReadIdentity(fileName) is { } id && id.TimeStamp == buildTimeStamp && id.ImageSize == imageSize;
        Record($"{fileName} (time stamp {buildTimeStamp:x8}, size {imageSize}): {(found ? "found" : "not found locally")}");
        return found ? fileName : null;
    }

    public string? FindPEImage(string fileName, SymbolProperties archivedUnder, ImmutableArray<byte> buildIdOrUUID, OSPlatform originalPlatform, bool checkProperties)
    {
        Record($"{fileName} (build id, {originalPlatform}, archived under {archivedUnder}): not found locally");
        return null;
    }

    /// <summary>Records that ClrMD asked whether to check a DAC's signature, and keeps the check on.</summary>
    public bool RecordSignatureCheck(string dac)
    {
        Record($"signature check of {dac}: on");
        return true;
    }

    internal static (int TimeStamp, int ImageSize)? ReadIdentity(string file)
    {
        try
        {
            using var stream = File.OpenRead(file);
            using var reader = new PEReader(stream);
            var headers = reader.PEHeaders;
            return headers.PEHeader is { } pe ? (headers.CoffHeader.TimeDateStamp, pe.SizeOfImage) : null;
        }
        catch (Exception ex) when (ex is IOException or BadImageFormatException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private void Record(string request)
    {
        lock (_requests)
        {
            _requests.Add(request);
        }
    }
}
