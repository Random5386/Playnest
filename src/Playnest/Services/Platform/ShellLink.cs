using System.Text;

namespace Playnest.Services.Platform;

public sealed record ShellLinkInfo(string? Target, string? Arguments, string? WorkingDir, string? IconLocation);

/// <summary>Minimal reader for Windows .lnk shortcut files (MS-SHLLINK). No COM needed, so it is AOT/trim safe.</summary>
public static class ShellLink
{
    private const uint HasLinkTargetIdList = 0x1, HasLinkInfo = 0x2, HasName = 0x4, HasRelativePath = 0x8,
        HasWorkingDir = 0x10, HasArguments = 0x20, HasIconLocation = 0x40, IsUnicode = 0x80;

    public static ShellLinkInfo? TryRead(string path)
    {
        try
        {
            if (new FileInfo(path).Length > 1024 * 1024) return null; // real shortcuts are a few KB
            var b = File.ReadAllBytes(path);
            if (b.Length < 0x4C || BitConverter.ToUInt32(b, 0) != 0x4C) return null;
            uint flags = BitConverter.ToUInt32(b, 0x14);
            int pos = 0x4C;
            if ((flags & HasLinkTargetIdList) != 0) pos += 2 + BitConverter.ToUInt16(b, pos);

            string? target = null;
            if ((flags & HasLinkInfo) != 0)
            {
                int start = pos;
                uint size = BitConverter.ToUInt32(b, start);
                uint headerSize = BitConverter.ToUInt32(b, start + 4);
                uint liFlags = BitConverter.ToUInt32(b, start + 8);
                if ((liFlags & 1) != 0) // VolumeIDAndLocalBasePath
                {
                    if (headerSize >= 0x24)
                    {
                        uint uOff = BitConverter.ToUInt32(b, start + 0x1C);
                        target = ReadUtf16Z(b, start + (int)uOff);
                    }
                    if (string.IsNullOrEmpty(target))
                    {
                        uint off = BitConverter.ToUInt32(b, start + 0x10);
                        target = ReadAnsiZ(b, start + (int)off);
                    }
                    uint suffixOff = BitConverter.ToUInt32(b, start + 0x18);
                    var suffix = ReadAnsiZ(b, start + (int)suffixOff);
                    if (!string.IsNullOrEmpty(suffix)) target = Path.Combine(target ?? "", suffix);
                }
                pos = start + (int)size;
            }

            bool unicode = (flags & IsUnicode) != 0;
            string? Next(uint flag)
            {
                if ((flags & flag) == 0) return null;
                int count = BitConverter.ToUInt16(b, pos);
                pos += 2;
                int bytes = unicode ? count * 2 : count;
                var s = unicode ? Encoding.Unicode.GetString(b, pos, bytes) : Encoding.Latin1.GetString(b, pos, bytes);
                pos += bytes;
                return s;
            }

            Next(HasName);
            var rel = Next(HasRelativePath);
            var work = Next(HasWorkingDir);
            var args = Next(HasArguments);
            var icon = Next(HasIconLocation);

            if (string.IsNullOrEmpty(target) && !string.IsNullOrEmpty(rel))
                target = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, rel));
            return new ShellLinkInfo(
                string.IsNullOrEmpty(target) ? null : Environment.ExpandEnvironmentVariables(target),
                args,
                string.IsNullOrEmpty(work) ? null : Environment.ExpandEnvironmentVariables(work),
                icon);
        }
        catch
        {
            return null;
        }
    }

    private static string ReadAnsiZ(byte[] b, int pos)
    {
        int end = pos;
        while (end < b.Length && b[end] != 0) end++;
        return Encoding.Latin1.GetString(b, pos, end - pos);
    }

    private static string ReadUtf16Z(byte[] b, int pos)
    {
        int end = pos;
        while (end + 1 < b.Length && (b[end] != 0 || b[end + 1] != 0)) end += 2;
        return Encoding.Unicode.GetString(b, pos, end - pos);
    }
}

public sealed record InternetShortcutInfo(string Url, string? IconFile);

/// <summary>Reads .url files (e.g. Steam/Epic desktop shortcuts: URL=steam://rungameid/123).</summary>
public static class InternetShortcut
{
    public static InternetShortcutInfo? TryRead(string path)
    {
        try
        {
            string? url = null, icon = null;
            if (new FileInfo(path).Length > 64 * 1024) return null;
            foreach (var line in File.ReadLines(path))
            {
                if (line.StartsWith("URL=", StringComparison.OrdinalIgnoreCase)) url = line[4..].Trim();
                else if (line.StartsWith("IconFile=", StringComparison.OrdinalIgnoreCase)) icon = line[9..].Trim();
            }
            return url is null ? null : new InternetShortcutInfo(url, icon);
        }
        catch
        {
            return null;
        }
    }
}
