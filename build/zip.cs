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

// Zip files for build scripts on any system, without zip or unzip installed (sprint 3.9): creates the Windows release
// archive, unpacks it, and lists the entries of a zip file (a .nupkg) one path per line.
//
// Usage: dotnet run build/zip.cs -- create <folder> <zip file>   (the folder itself is the archive's top entry)
//        dotnet run build/zip.cs -- extract <zip file> <folder>
//        dotnet run build/zip.cs -- list <zip file>

using System.IO.Compression;

switch (args)
{
    case ["create", var folder, var zip]:
        File.Delete(zip);
        ZipFile.CreateFromDirectory(folder, zip, CompressionLevel.Optimal, includeBaseDirectory: true);
        return 0;
    case ["extract", var zip, var folder]:
        ZipFile.ExtractToDirectory(zip, folder, overwriteFiles: false);
        return 0;
    case ["list", var zip]:
        using (var archive = ZipFile.OpenRead(zip))
        {
            foreach (var entry in archive.Entries)
            {
                Console.WriteLine(entry.FullName);
            }
        }

        return 0;
    default:
        Console.Error.WriteLine("Usage: zip.cs create <folder> <zip file> | extract <zip file> <folder> | list <zip file>");
        return 64;
}
