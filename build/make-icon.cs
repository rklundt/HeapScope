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


// Draws logo A (ADR-040) with no graphics library: the app's icon file with its sizes, and a PNG for the README.
// The logo's vector form is in src/HeapScope.UI/Themes/Icons.axaml and docs/design/logo-options/logo-a.svg; the shapes here match.
//
// Usage: dotnet run build/make-icon.cs -- <icon.ico> <logo.png>

using System.IO.Compression;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: make-icon.cs <icon.ico> <logo.png>");
    return 64;
}

int[] sizes = [16, 24, 32, 48, 64, 256];
var images = sizes.Select(size => (Size: size, Png: Png(size))).ToArray();
using (var icon = new BinaryWriter(File.Create(args[0])))
{
    icon.Write((ushort)0);
    icon.Write((ushort)1);
    icon.Write((ushort)images.Length);
    var offset = 6 + (16 * images.Length);
    foreach (var (size, png) in images)
    {
        icon.Write((byte)(size == 256 ? 0 : size));
        icon.Write((byte)(size == 256 ? 0 : size));
        icon.Write((byte)0);
        icon.Write((byte)0);
        icon.Write((ushort)1);
        icon.Write((ushort)32);
        icon.Write(png.Length);
        icon.Write(offset);
        offset += png.Length;
    }

    foreach (var (_, png) in images)
    {
        icon.Write(png);
    }
}

File.WriteAllBytes(args[1], Png(256));
return 0;

// One size: 20 px and under use the simplified lens, as the app's Logo.Small does.
static byte[] Png(int size)
{
    Func<double, double, double> Background = (x, y) => RoundedBox(x, y, 0, 0, 64, 64, 14);
    (uint Color, Func<double, double, double> Distance)[] shapes = size <= 20
        ?
        [
            (0xFF4F46E5, Background),
            (0xFFFFFFFF, (x, y) => Ring(x, y, 30, 28, 15, 8)),
            (0xFFFFFFFF, (x, y) => Capsule(x, y, 42, 40, 52, 50, 4.5)),
        ]
        :
        [
            (0xFF4F46E5, Background),
            (0xFFFFFFFF, (x, y) => RoundedBox(x, y, 14, 16, 20, 5, 1.5)),
            (0xFFC9CEF7, (x, y) => RoundedBox(x, y, 14, 24, 20, 5, 1.5)),
            (0xFFFFFFFF, (x, y) => RoundedBox(x, y, 14, 32, 20, 5, 1.5)),
            (0xFFFFFFFF, (x, y) => Ring(x, y, 35, 34, 13, 4)),
            (0xFFFFFFFF, (x, y) => Capsule(x, y, 45, 44, 52, 51, 2.5)),
        ];
    var unit = 64.0 / size;
    var row = new byte[1 + (size * 4)];
    using var pixels = new MemoryStream();
    using (var zlib = new ZLibStream(pixels, CompressionLevel.SmallestSize, leaveOpen: true))
    {
        for (var py = 0; py < size; py++)
        {
            for (var px = 0; px < size; px++)
            {
                var (x, y) = ((px + 0.5) * unit, (py + 0.5) * unit);
                double r = 0, g = 0, b = 0, a = 0;
                foreach (var (color, distance) in shapes)
                {
                    // Coverage of this pixel: the distance to the edge, across one pixel's width.
                    var cover = Math.Clamp(0.5 - (distance(x, y) / unit), 0, 1);
                    r += (((color >> 16) & 255) - r) * cover;
                    g += (((color >> 8) & 255) - g) * cover;
                    b += ((color & 255) - b) * cover;
                    a += (255 - a) * cover;
                }

                row[1 + (px * 4)] = (byte)r;
                row[2 + (px * 4)] = (byte)g;
                row[3 + (px * 4)] = (byte)b;
                row[4 + (px * 4)] = (byte)a;
            }

            zlib.Write(row);
        }
    }

    using var png = new MemoryStream();
    png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
    var header = new byte[13];
    System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header, size);
    System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), size);
    header[8] = 8;
    header[9] = 6;
    Chunk(png, "IHDR", header);
    Chunk(png, "IDAT", pixels.ToArray());
    Chunk(png, "IEND", []);
    return png.ToArray();
}

static double RoundedBox(double x, double y, double left, double top, double width, double height, double radius)
{
    var dx = Math.Abs(x - (left + (width / 2))) - ((width / 2) - radius);
    var dy = Math.Abs(y - (top + (height / 2))) - ((height / 2) - radius);
    return Math.Sqrt((Math.Max(dx, 0) * Math.Max(dx, 0)) + (Math.Max(dy, 0) * Math.Max(dy, 0))) + Math.Min(Math.Max(dx, dy), 0) - radius;
}

static double Ring(double x, double y, double cx, double cy, double radius, double thickness) =>
    Math.Abs(Math.Sqrt(((x - cx) * (x - cx)) + ((y - cy) * (y - cy))) - radius) - (thickness / 2);

static double Capsule(double x, double y, double ax, double ay, double bx, double by, double radius)
{
    var (dx, dy) = (bx - ax, by - ay);
    var t = Math.Clamp((((x - ax) * dx) + ((y - ay) * dy)) / ((dx * dx) + (dy * dy)), 0, 1);
    var (ex, ey) = (x - ax - (t * dx), y - ay - (t * dy));
    return Math.Sqrt((ex * ex) + (ey * ey)) - radius;
}

static void Chunk(Stream png, string type, byte[] data)
{
    var name = System.Text.Encoding.ASCII.GetBytes(type);
    Span<byte> number = stackalloc byte[4];
    System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(number, data.Length);
    png.Write(number);
    png.Write(name);
    png.Write(data);
    var crc = 0xFFFFFFFFu;
    foreach (var value in name.Concat(data))
    {
        crc ^= value;
        for (var bit = 0; bit < 8; bit++)
        {
            crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
        }
    }

    System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(number, ~crc);
    png.Write(number);
}
