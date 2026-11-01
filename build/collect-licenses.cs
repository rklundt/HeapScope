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

// Copies the license texts into a published build (sprint 3.6, peer review P3): HeapScope's own (LICENSE,
// ADDITIONAL-TERMS.md, COMMERCIAL.md, THIRD-PARTY-NOTICES.md) at the top, and every shipped package's own license and
// notice files under licenses/<package>.<version>/, read from the restore's project.assets.json of each published
// project. The .NET runtime a self-contained build carries is a package too (its runtime pack).
//
// Usage: dotnet run build/collect-licenses.cs -- <publish folder> <runtime identifier> <project folder>...

using System.Text.Json;

if (args.Length < 3)
{
    Console.Error.WriteLine("Usage: collect-licenses.cs <publish folder> <runtime identifier> <project folder>...");
    return 64;
}

var output = Path.GetFullPath(args[0]);
var rid = args[1];
var repository = Path.GetFullPath(Path.Combine(AppContext.GetData("EntryPointFileDirectoryPath") as string ?? ".", ".."));
string[] own = ["LICENSE", "ADDITIONAL-TERMS.md", "COMMERCIAL.md", "THIRD-PARTY-NOTICES.md"];
foreach (var file in own)
{
    File.Copy(Path.Combine(repository, file), Path.Combine(output, file), overwrite: true);
}

// Where packages may be: the restore's package folders, and the SDK's packs folder for runtime packs installed with it.
var dotnetRoot = Path.GetFullPath(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", ".."));
var packages = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
foreach (var project in args.Skip(2))
{
    var assetsPath = Path.Combine(project, "obj", "project.assets.json");
    using var assets = JsonDocument.Parse(File.ReadAllBytes(assetsPath));
    var root = assets.RootElement;
    string[] folders = [.. root.GetProperty("packageFolders").EnumerateObject().Select(p => p.Name), Path.Combine(dotnetRoot, "packs")];
    var target = root.GetProperty("targets").EnumerateObject().FirstOrDefault(t => t.Name.EndsWith("/" + rid, StringComparison.Ordinal));
    if (target.Value.ValueKind != JsonValueKind.Object)
    {
        Console.Error.WriteLine($"{assetsPath} has no restore for {rid}; publish with -r {rid} first.");
        return 1;
    }

    var libraries = root.GetProperty("libraries");
    foreach (var library in target.Value.EnumerateObject())
    {
        if (library.Value.GetProperty("type").GetString() == "package" && libraries.GetProperty(library.Name).TryGetProperty("path", out var path))
        {
            packages[library.Name] = Find(folders, path.GetString()!) ?? "";
        }
    }

    // A self-contained build carries the .NET runtime: its runtime pack is a download dependency, not a library.
    foreach (var framework in root.GetProperty("project").GetProperty("frameworks").EnumerateObject())
    {
        if (framework.Value.TryGetProperty("downloadDependencies", out var downloads))
        {
            foreach (var download in downloads.EnumerateArray())
            {
                var name = download.GetProperty("name").GetString()!;
                var version = download.GetProperty("version").GetString()!.Trim('[', ']').Split(',')[0].Trim();
                if (name.StartsWith("Microsoft.NETCore.App.Runtime.", StringComparison.Ordinal))
                {
                    packages[$"{name}/{version}"] = Find(folders, $"{name.ToLowerInvariant()}/{version}") ?? Find(folders, $"{name}/{version}") ?? "";
                }
            }
        }
    }
}

var licenses = Path.Combine(output, "licenses");
Directory.CreateDirectory(licenses);
var missing = new List<string>();
foreach (var (package, folder) in packages)
{
    var files = folder.Length == 0 ? [] : Directory.EnumerateFiles(folder).Where(IsLicenseFile).ToArray();
    if (files.Length == 0)
    {
        missing.Add(package);
        continue;
    }

    var into = Path.Combine(licenses, package.Replace('/', '.'));
    Directory.CreateDirectory(into);
    foreach (var file in files)
    {
        File.Copy(file, Path.Combine(into, Path.GetFileName(file)), overwrite: true);
    }
}

Console.WriteLine($"Copied HeapScope's license files and the license files of {packages.Count - missing.Count} of {packages.Count} packages to {output}.");
if (missing.Count > 0)
{
    // Their licenses are named in THIRD-PARTY-NOTICES.md; these packages carry no file of their own.
    Console.WriteLine($"No license file in: {string.Join(", ", missing)}");
}

return 0;

static string? Find(IEnumerable<string> folders, string relative) =>
    folders.Select(f => Path.Combine(f, relative)).FirstOrDefault(Directory.Exists);

static bool IsLicenseFile(string path)
{
    var name = Path.GetFileName(path).ToUpperInvariant();
    return name.StartsWith("LICENSE", StringComparison.Ordinal) || name.StartsWith("LICENCE", StringComparison.Ordinal)
        || name.StartsWith("THIRD-PARTY-NOTICES", StringComparison.Ordinal) || name.StartsWith("THIRDPARTYNOTICES", StringComparison.Ordinal)
        || name.StartsWith("NOTICE", StringComparison.Ordinal);
}
