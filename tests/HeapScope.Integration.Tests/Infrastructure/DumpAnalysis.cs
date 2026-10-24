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

    private DumpAnalysis(DataTarget target, ClrRuntime runtime)
    {
        _target = target;
        Runtime = runtime;
    }

    public ClrRuntime Runtime { get; }

    public ClrHeap Heap => Runtime.Heap;

    public static DumpAnalysis Open(string path)
    {
        var target = DataTarget.LoadDump(path);
        try
        {
            if (target.ClrVersions.Length == 0)
            {
                throw new InvalidOperationException($"No .NET runtime found in {path}.");
            }

            return new DumpAnalysis(target, target.ClrVersions[0].CreateRuntime());
        }
        catch
        {
            target.Dispose();
            throw;
        }
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
    /// Objects reachable from the retaining GC roots (ClrMD's root enumeration; weak handles are not roots), following
    /// dependent handles only from reachable primaries, never entering <paramref name="skip"/>.
    /// </summary>
    public HashSet<ulong> ReachableFromRoots(ISet<ulong>? skip = null)
    {
        var starts = Heap.EnumerateRoots().Select(r => r.Object).Where(o => !o.IsNull);
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
