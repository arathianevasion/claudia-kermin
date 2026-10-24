// SPDX-License-Identifier: GPL-3.0-or-later
// Regenerates GameData/ClaudiaKermin/Icons/SuperIntelligence.png: a disc in three sectors
// (pilot blue, engineer orange, scientist green) with a white rim.
// Usage: dotnet run tools/make-trait-icon.cs -- GameData/ClaudiaKermin/Icons/SuperIntelligence.png
#:property LangVersion=latest
#:property TreatWarningsAsErrors=false
using System;
using System.IO;
using System.IO.Compression;
using System.Buffers.Binary;
using System.Collections.Generic;

const int Size = 64;
var colours = new[] { (66, 135, 245), (245, 150, 40), (70, 190, 110) };

var raw = new List<byte>();
for (var y = 0; y < Size; y++)
{
    raw.Add(0);
    for (var x = 0; x < Size; x++)
    {
        double dx = x + 0.5 - Size / 2.0, dy = y + 0.5 - Size / 2.0;
        var r = Math.Sqrt(dx * dx + dy * dy);
        if (r > Size / 2.0 - 1) { raw.AddRange(new byte[] { 0, 0, 0, 0 }); continue; }
        if (r > Size / 2.0 - 5) { raw.AddRange(new byte[] { 255, 255, 255, 255 }); continue; }
        var angle = (Math.Atan2(dy, dx) * 180 / Math.PI + 90 + 360) % 360;
        var (cr, cg, cb) = colours[(int)(angle / 120)];
        raw.AddRange(new byte[] { (byte)cr, (byte)cg, (byte)cb, 255 });
    }
}

byte[] Deflate(byte[] data)
{
    using var ms = new MemoryStream();
    using (var z = new ZLibStream(ms, CompressionLevel.SmallestSize, true)) z.Write(data, 0, data.Length);
    return ms.ToArray();
}

uint Crc(byte[] data)
{
    var crc = 0xFFFFFFFFu;
    foreach (var b in data)
    {
        crc ^= b;
        for (var k = 0; k < 8; k++) crc = (crc & 1) != 0 ? 0xEDB88320u ^ (crc >> 1) : crc >> 1;
    }
    return ~crc;
}

void Chunk(Stream s, string tag, byte[] data)
{
    var len = new byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(len, (uint)data.Length);
    s.Write(len);
    var body = new byte[4 + data.Length];
    System.Text.Encoding.ASCII.GetBytes(tag).CopyTo(body, 0);
    data.CopyTo(body, 4);
    s.Write(body);
    var crc = new byte[4];
    BinaryPrimitives.WriteUInt32BigEndian(crc, Crc(body));
    s.Write(crc);
}

using var output = File.Create(args[0]);
output.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
var header = new byte[13];
BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(0), Size);
BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(4), Size);
header[8] = 8; header[9] = 6;
Chunk(output, "IHDR", header);
Chunk(output, "IDAT", Deflate(raw.ToArray()));
Chunk(output, "IEND", Array.Empty<byte>());
