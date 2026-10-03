using System.Buffers.Binary;

namespace Playnest.Services.Artwork;

/// <summary>Reads image dimensions from file headers (PNG, JPEG, WebP, ICO) without decoding the image.</summary>
public static class ImageInfo
{
    public static (int Width, int Height)? Size(byte[] b)
    {
        try
        {
            if (b.Length < 24) return null;
            // PNG: IHDR right after the signature
            if (b[0] == 0x89 && b[1] == 'P' && b[2] == 'N' && b[3] == 'G')
                return (BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(16)), BinaryPrimitives.ReadInt32BigEndian(b.AsSpan(20)));

            // JPEG: walk markers until a start-of-frame
            if (b[0] == 0xFF && b[1] == 0xD8)
            {
                int i = 2;
                while (i + 9 < b.Length)
                {
                    if (b[i] != 0xFF) { i++; continue; }
                    byte marker = b[i + 1];
                    if (marker is 0xD8 or 0x01 || (marker >= 0xD0 && marker <= 0xD7)) { i += 2; continue; }
                    int len = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(i + 2));
                    bool sof = marker is >= 0xC0 and <= 0xCF && marker is not (0xC4 or 0xC8 or 0xCC);
                    if (sof)
                        return (BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(i + 7)), BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(i + 5)));
                    if (len < 2) return null;
                    i += 2 + len;
                }
                return null;
            }

            // WebP
            if (b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F' && b[8] == 'W' && b[9] == 'E' && b.Length >= 30)
            {
                if (b[12] == 'V' && b[13] == 'P' && b[14] == '8' && b[15] == ' ')
                    return (BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(26)) & 0x3FFF, BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(28)) & 0x3FFF);
                if (b[12] == 'V' && b[13] == 'P' && b[14] == '8' && b[15] == 'L')
                {
                    uint bits = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(21));
                    return ((int)(bits & 0x3FFF) + 1, (int)((bits >> 14) & 0x3FFF) + 1);
                }
                if (b[12] == 'V' && b[13] == 'P' && b[14] == '8' && b[15] == 'X')
                    return ((b[24] | b[25] << 8 | b[26] << 16) + 1, (b[27] | b[28] << 8 | b[29] << 16) + 1);
            }
        }
        catch { }
        return null;
    }

    /// <summary>Reads just the start of a file (enough for the header) and returns its size.</summary>
    public static (int Width, int Height)? SizeOfFile(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buf = new byte[(int)Math.Min(fs.Length, 64 * 1024)];
            int read = fs.ReadAtLeast(buf, buf.Length, throwOnEndOfStream: false);
            return Size(read == buf.Length ? buf : buf[..read]);
        }
        catch { return null; }
    }

    public static int Width(byte[]? b) => b is null ? 0 : Size(b)?.Width ?? 0;
}
