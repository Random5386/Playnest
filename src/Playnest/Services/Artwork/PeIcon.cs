using System.Buffers.Binary;
using System.IO.Compression;

namespace Playnest.Services.Artwork;

/// <summary>
/// Extracts the largest icon image from a Windows .exe/.dll (or .ico file) by reading the PE resources directly.
/// Modern games embed a 256×256 PNG; older ones store bitmaps, which are converted to PNG here.
/// Pure managed code: no GDI handles, works under NativeAOT and is unit-testable on any OS.
/// </summary>
public static class PeIcon
{
    private const int RT_ICON = 3, RT_GROUP_ICON = 14;
    private static readonly byte[] PngSig = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>Returns PNG bytes of the best icon, or null. Also accepts .ico files.</summary>
    public static byte[]? ExtractLargestPng(string path, int maxFileBytes = 512 * 1024 * 1024)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists || fi.Length > maxFileBytes) return null;
            bool small = fi.Length <= 16 * 1024 * 1024;
            if (path.EndsWith(".ico", StringComparison.OrdinalIgnoreCase)) return small ? FromIcoBytes(File.ReadAllBytes(path)) : null;
            if (path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) return small ? File.ReadAllBytes(path) : null;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return FromPe(fs);
        }
        catch
        {
            return null;
        }
    }

    // ---------------------------------------------------------------- PE parsing

    private sealed record Section(uint Va, uint VSize, uint RawPtr, uint RawSize);

    private static byte[]? FromPe(Stream s)
    {
        var br = new BinaryReader(s);
        if (br.ReadUInt16() != 0x5A4D) return null; // MZ
        s.Position = 0x3C;
        int peOff = br.ReadInt32();
        s.Position = peOff;
        if (br.ReadUInt32() != 0x00004550) return null; // PE\0\0
        br.ReadUInt16(); // machine
        int nSections = br.ReadUInt16();
        s.Position += 12;
        int optSize = br.ReadUInt16();
        br.ReadUInt16(); // characteristics
        long optStart = s.Position;
        ushort magic = br.ReadUInt16();
        int dirOffset = magic == 0x20B ? 112 : 96; // PE32+ : PE32
        s.Position = optStart + dirOffset + 2 * 8; // data directory #2 = resources
        uint resRva = br.ReadUInt32();
        uint resSize = br.ReadUInt32();
        if (resRva == 0 || resSize == 0) return null;

        s.Position = optStart + optSize;
        var sections = new List<Section>();
        for (int i = 0; i < nSections; i++)
        {
            s.Position += 8; // name
            uint vsize = br.ReadUInt32(), va = br.ReadUInt32(), rawSize = br.ReadUInt32(), rawPtr = br.ReadUInt32();
            s.Position += 16;
            sections.Add(new Section(va, vsize, rawPtr, rawSize));
        }

        long RvaToOffset(uint rva)
        {
            foreach (var sec in sections)
                if (rva >= sec.Va && rva < sec.Va + Math.Max(sec.VSize, sec.RawSize))
                    return rva - sec.Va + sec.RawPtr;
            return -1;
        }

        long resBase = RvaToOffset(resRva);
        if (resBase < 0) return null;

        // Level 1: types
        var types = ReadDir(br, resBase, resBase);
        var group = types.FirstOrDefault(t => t.Id == RT_GROUP_ICON);
        var icons = types.FirstOrDefault(t => t.Id == RT_ICON);
        if (group.Offset == 0 || icons.Offset == 0) return null;

        // Level 2: first icon group (Windows uses the first one as the application icon)
        var groups = ReadDir(br, resBase, resBase + group.Offset);
        if (groups.Count == 0) return null;
        var firstGroup = groups.OrderBy(g => g.IsName ? 0 : 1).ThenBy(g => g.Id).First();
        var grpData = ReadLeaf(br, resBase, firstGroup, RvaToOffset);
        if (grpData is null || grpData.Length < 6) return null;

        // GRPICONDIR: reserved, type, count, then 14-byte entries
        int count = BinaryPrimitives.ReadUInt16LittleEndian(grpData.AsSpan(4));
        int bestId = -1, bestW = 0, bestBits = 0;
        for (int i = 0; i < count && 6 + i * 14 + 14 <= grpData.Length; i++)
        {
            var e = grpData.AsSpan(6 + i * 14, 14);
            int w = e[0] == 0 ? 256 : e[0];
            int bits = BinaryPrimitives.ReadUInt16LittleEndian(e[6..]);
            int id = BinaryPrimitives.ReadUInt16LittleEndian(e[12..]);
            if (w > bestW || (w == bestW && bits > bestBits)) { bestW = w; bestBits = bits; bestId = id; }
        }
        if (bestId < 0) return null;

        var iconEntries = ReadDir(br, resBase, resBase + icons.Offset);
        var target = iconEntries.FirstOrDefault(x => !x.IsName && x.Id == bestId);
        if (target.Offset == 0) return null;
        var data = ReadLeaf(br, resBase, target, RvaToOffset);
        return data is null ? null : ImageToPng(data);
    }

    private readonly record struct DirEntry(bool IsName, int Id, uint Offset, bool IsDir);

    private static List<DirEntry> ReadDir(BinaryReader br, long resBase, long dirPos)
    {
        var list = new List<DirEntry>();
        br.BaseStream.Position = dirPos + 12;
        int named = br.ReadUInt16(), ids = br.ReadUInt16();
        for (int i = 0; i < named + ids && i < 4096; i++)
        {
            uint name = br.ReadUInt32(), off = br.ReadUInt32();
            bool isName = (name & 0x80000000) != 0;
            list.Add(new DirEntry(isName, isName ? 0 : (int)name, off & 0x7FFFFFFF, (off & 0x80000000) != 0));
        }
        return list;
    }

    /// <summary>Descends through name/language levels until a data entry is reached.</summary>
    private static byte[]? ReadLeaf(BinaryReader br, long resBase, DirEntry entry, Func<uint, long> rvaToOffset)
    {
        var e = entry;
        for (int depth = 0; e.IsDir && depth < 4; depth++)
        {
            var children = ReadDir(br, resBase, resBase + e.Offset);
            if (children.Count == 0) return null;
            e = children[0];
        }
        br.BaseStream.Position = resBase + e.Offset;
        uint dataRva = br.ReadUInt32(), size = br.ReadUInt32();
        if (size == 0 || size > 16 * 1024 * 1024) return null;
        long off = rvaToOffset(dataRva);
        if (off < 0) return null;
        br.BaseStream.Position = off;
        return br.ReadBytes((int)size);
    }

    // ---------------------------------------------------------------- ICO files

    public static byte[]? FromIcoBytes(byte[] ico)
    {
        try { return FromIcoBytesCore(ico); }
        catch { return null; } // malformed file — never let it crash artwork loading
    }

    private static byte[]? FromIcoBytesCore(byte[] ico)
    {
        if (ico.Length < 6 || BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(2)) != 1) return null;
        int count = BinaryPrimitives.ReadUInt16LittleEndian(ico.AsSpan(4));
        int bestW = 0, bestBits = 0, bestOff = -1, bestSize = 0;
        for (int i = 0; i < count && 6 + i * 16 + 16 <= ico.Length; i++)
        {
            var e = ico.AsSpan(6 + i * 16, 16);
            int w = e[0] == 0 ? 256 : e[0];
            int bits = BinaryPrimitives.ReadUInt16LittleEndian(e[6..]);
            int size = BinaryPrimitives.ReadInt32LittleEndian(e[8..]);
            int off = BinaryPrimitives.ReadInt32LittleEndian(e[12..]);
            if (size <= 0 || off < 0 || (long)off + size > ico.Length) continue;
            if (w > bestW || (w == bestW && bits > bestBits)) { bestW = w; bestBits = bits; bestOff = off; bestSize = size; }
        }
        if (bestOff < 0) return null;
        return ImageToPng(ico.AsSpan(bestOff, bestSize).ToArray());
    }

    // ---------------------------------------------------------------- DIB → PNG

    private static byte[]? ImageToPng(byte[] data)
    {
        if (data.Length >= 8 && data.AsSpan(0, 8).SequenceEqual(PngSig)) return data;
        return DibToPng(data);
    }

    /// <summary>Converts an icon DIB (BITMAPINFOHEADER + XOR bitmap + AND mask) to PNG.</summary>
    internal static byte[]? DibToPng(byte[] d)
    {
        if (d.Length < 40) return null;
        int hdr = BinaryPrimitives.ReadInt32LittleEndian(d);
        int w = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(4));
        int h = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(8)) / 2; // XOR + AND
        int bpp = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(14));
        int clrUsed = BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(32));
        if (w <= 0 || h <= 0 || w > 1024 || h > 1024) return null;

        int paletteCount = bpp <= 8 ? (clrUsed != 0 ? clrUsed : 1 << bpp) : 0;
        int palOff = hdr;
        int xorOff = palOff + paletteCount * 4;
        int xorStride = ((w * bpp + 31) / 32) * 4;
        int andOff = xorOff + xorStride * h;
        int andStride = ((w + 31) / 32) * 4;
        if (xorOff + xorStride * h > d.Length) return null;
        bool hasAnd = andOff + andStride * h <= d.Length;

        var rgba = new byte[w * h * 4];
        bool anyAlpha = false;
        for (int y = 0; y < h; y++)
        {
            int srcRow = xorOff + (h - 1 - y) * xorStride; // bottom-up
            for (int x = 0; x < w; x++)
            {
                byte r, g, b, a = 255;
                switch (bpp)
                {
                    case 32:
                        b = d[srcRow + x * 4]; g = d[srcRow + x * 4 + 1]; r = d[srcRow + x * 4 + 2]; a = d[srcRow + x * 4 + 3];
                        if (a != 0) anyAlpha = true;
                        break;
                    case 24:
                        b = d[srcRow + x * 3]; g = d[srcRow + x * 3 + 1]; r = d[srcRow + x * 3 + 2];
                        break;
                    case 8: case 4: case 1:
                    {
                        int bitPos = x * bpp;
                        int v = d[srcRow + bitPos / 8];
                        int shift = 8 - bpp - (bitPos % 8);
                        int idx = (v >> shift) & ((1 << bpp) - 1);
                        int p = palOff + idx * 4;
                        if (p + 3 > d.Length) return null;
                        b = d[p]; g = d[p + 1]; r = d[p + 2];
                        break;
                    }
                    default: return null;
                }
                int o = (y * w + x) * 4;
                rgba[o] = r; rgba[o + 1] = g; rgba[o + 2] = b; rgba[o + 3] = a;
            }
        }

        // Without an alpha channel the AND mask decides transparency.
        if ((bpp < 32 || !anyAlpha) && hasAnd)
        {
            for (int y = 0; y < h; y++)
            {
                int row = andOff + (h - 1 - y) * andStride;
                for (int x = 0; x < w; x++)
                {
                    bool transparent = ((d[row + x / 8] >> (7 - x % 8)) & 1) == 1;
                    rgba[(y * w + x) * 4 + 3] = transparent ? (byte)0 : (byte)255;
                }
            }
        }
        return Png.Encode(rgba, w, h);
    }
}

/// <summary>Tiny PNG encoder (RGBA8) used for converted icons.</summary>
public static class Png
{
    private static readonly uint[] CrcTable = BuildCrc();

    public static byte[] Encode(byte[] rgba, int w, int h)
    {
        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);

        var ihdr = new byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, w);
        BinaryPrimitives.WriteInt32BigEndian(ihdr.AsSpan(4), h);
        ihdr[8] = 8; ihdr[9] = 6; // 8-bit RGBA
        Chunk(ms, "IHDR", ihdr);

        using var raw = new MemoryStream();
        using (var z = new ZLibStream(raw, CompressionLevel.Optimal, leaveOpen: true))
        {
            for (int y = 0; y < h; y++)
            {
                z.WriteByte(0); // filter: none
                z.Write(rgba, y * w * 4, w * 4);
            }
        }
        Chunk(ms, "IDAT", raw.ToArray());
        Chunk(ms, "IEND", []);
        return ms.ToArray();
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        Span<byte> len = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(len, data.Length);
        s.Write(len);
        var t = System.Text.Encoding.ASCII.GetBytes(type);
        s.Write(t);
        s.Write(data);
        uint crc = 0xFFFFFFFF;
        foreach (var b in t) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        foreach (var b in data) crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
        crc ^= 0xFFFFFFFF;
        BinaryPrimitives.WriteUInt32BigEndian(len, crc);
        s.Write(len);
    }

    private static uint[] BuildCrc()
    {
        var t = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320 ^ (c >> 1) : c >> 1;
            t[n] = c;
        }
        return t;
    }
}
