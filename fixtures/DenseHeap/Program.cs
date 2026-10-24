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
using System.Globalization;
using System.Text;
using Fixtures.Leaks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

// Usage: DenseHeap [--target-mb 3000] [--parent-pid <pid>]
// Grows a dense, realistic managed heap out of real library objects: it generates C# source, parses it, compiles it
// with Roslyn, binds every method body and keeps the syntax trees (fully realized), compilations and symbols alive
// until the managed heap reaches the target. Then it prints "READY <pid> <heap MB>" and waits to be dumped.
// It exits when its parent exits (--parent-pid), so an aborted capture leaves no process behind (sprint 1.2).
var targetMb = int.Parse(Arg("--target-mb") ?? "3000", CultureInfo.InvariantCulture);
var parentPid = Arg("--parent-pid") is { } raw ? int.Parse(raw, CultureInfo.InvariantCulture) : (int?)null;
ParentWatch.ExitWhenParentExits(parentPid);

var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
    .Split(Path.PathSeparator)
    .Where(p => Path.GetFileName(p) is "System.Private.CoreLib.dll" or "System.Runtime.dll" or "System.Linq.dll" or "System.Collections.dll" or "System.Console.dll")
    .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
    .ToList();

var kept = new List<object>();
var stopwatch = Stopwatch.StartNew();
var batch = 0;
long heapMb = 0;
while (heapMb < targetMb)
{
    var trees = new List<SyntaxTree>();
    for (var file = 0; file < 100; file++)
    {
        var tree = CSharpSyntaxTree.ParseText(Source(batch, file), path: $"Batch{batch}/File{file}.cs");

        // Realize every red node and token so the tree holds them (what an IDE does as it walks a file).
        _ = tree.GetRoot().DescendantNodesAndTokensAndSelf(descendIntoTrivia: true).Count();
        trees.Add(tree);
    }

    var compilation = CSharpCompilation.Create(
        $"Batch{batch}",
        trees,
        references,
        new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

    // Binds every method body and fills the compilation's symbol tables.
    var diagnostics = compilation.GetDiagnostics();
    var models = trees.Select(t => compilation.GetSemanticModel(t)).ToList();
    kept.Add(compilation);
    kept.Add(models);
    kept.Add(diagnostics);
    kept.Add(compilation.GlobalNamespace.GetNamespaceMembers().SelectMany(n => n.GetTypeMembers()).SelectMany(t => t.GetMembers()).ToList());

    batch++;
    heapMb = GC.GetTotalMemory(forceFullCollection: true) / (1024 * 1024);
    Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{batch} batches, managed heap {heapMb:N0} MB, {stopwatch.Elapsed.TotalSeconds:N0} s"));
}

GC.Collect();
GC.WaitForPendingFinalizers();
heapMb = GC.GetTotalMemory(forceFullCollection: true) / (1024 * 1024);
Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"READY {Environment.ProcessId} {heapMb}"));
Console.Out.Flush();

// Wait to be dumped; the capture script kills the process afterwards. GC.KeepAlive keeps the graph rooted.
Thread.Sleep(Timeout.Infinite);
GC.KeepAlive(kept);
return 0;

string? Arg(string name)
{
    var index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}

static string Source(int batch, int file)
{
    var builder = new StringBuilder();
    builder.AppendLine("using System; using System.Collections.Generic; using System.Linq;");
    builder.AppendLine(CultureInfo.InvariantCulture, $"namespace Generated.B{batch}.F{file}");
    builder.AppendLine("{");
    for (var c = 0; c < 20; c++)
    {
        builder.AppendLine(CultureInfo.InvariantCulture, $"  public sealed class Service{c}<T> where T : IComparable<T>");
        builder.AppendLine("  {");
        builder.AppendLine("    private readonly Dictionary<string, List<T>> _items = new();");
        builder.AppendLine("    public event EventHandler<string>? Changed;");
        builder.AppendLine(CultureInfo.InvariantCulture, $"    public string Name {{ get; init; }} = \"Service{c}\";");
        for (var m = 0; m < 8; m++)
        {
            builder.AppendLine(CultureInfo.InvariantCulture, $"    public IEnumerable<T> Query{m}(string key, Func<T, bool> filter, int take = {m + 1})");
            builder.AppendLine("    {");
            builder.AppendLine("      if (!_items.TryGetValue(key, out var list)) { Changed?.Invoke(this, key); return Array.Empty<T>(); }");
            builder.AppendLine(CultureInfo.InvariantCulture, $"      var result = list.Where(filter).OrderBy(x => x).Take(take).ToList();");
            builder.AppendLine(CultureInfo.InvariantCulture, $"      for (var i = 0; i < result.Count; i++) {{ if (result[i].CompareTo(result[0]) < 0) {{ throw new InvalidOperationException($\"{{Name}} {m} {{i}}\"); }} }}");
            builder.AppendLine("      return result;");
            builder.AppendLine("    }");
        }

        builder.AppendLine("  }");
    }

    builder.AppendLine("}");
    return builder.ToString();
}
