using System.Text;

namespace Playnest.Services.Scanning;

/// <summary>What Steam says about an app: its real type (game, tool, application...) and launch executable.</summary>
public sealed record SteamAppMeta(uint AppId, string? Name, string? Type, string? Executable, string? ClientIcon = null);

/// <summary>
/// Reads Steam's binary appinfo.vdf (versions 27, 28 and 29) and returns metadata for the requested app ids only.
/// Other entries are skipped by length, so this stays fast even with a 100+ MB cache file.
/// </summary>
public static class SteamAppInfo
{
    private const uint V27 = 0x07564427, V28 = 0x07564428, V29 = 0x07564429;

    public static Dictionary<uint, SteamAppMeta> Read(string path, IReadOnlySet<uint> wanted)
    {
        var result = new Dictionary<uint, SteamAppMeta>();
        if (wanted.Count == 0 || !File.Exists(path)) return result;

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
        using var br = new BinaryReader(fs, Encoding.UTF8);
        uint magic = br.ReadUInt32();
        if (magic is not (V27 or V28 or V29)) return result;
        br.ReadUInt32(); // universe

        string[]? strings = null;
        if (magic == V29)
        {
            long tableOffset = br.ReadInt64();
            long back = fs.Position;
            fs.Position = tableOffset;
            uint count = br.ReadUInt32();
            if (count > 2_000_000) return result; // corrupt file — refuse instead of allocating gigabytes
            strings = new string[count];
            for (int i = 0; i < count; i++) strings[i] = ReadCString(br);
            fs.Position = back;
        }

        while (fs.Position + 8 <= fs.Length)
        {
            uint appId = br.ReadUInt32();
            if (appId == 0) break;
            uint size = br.ReadUInt32();
            long end = fs.Position + size;
            if (!wanted.Contains(appId))
            {
                fs.Position = end;
                continue;
            }

            // infoState, lastUpdated, picsToken, sha1, changeNumber, (v28+) binary sha1
            fs.Position += 4 + 4 + 8 + 20 + 4 + (magic >= V28 ? 20 : 0);
            try
            {
                var root = ReadObject(br, strings, end, 0);
                var info = root.TryGetValue("appinfo", out var a) && a is Dictionary<string, object> ai ? ai : root;
                var common = Child(info, "common");
                var config = Child(info, "config");
                string? name = common is not null && common.TryGetValue("name", out var n) ? n as string : null;
                string? type = common is not null && common.TryGetValue("type", out var t) ? t as string : null;
                string? icon = common is not null && common.TryGetValue("clienticon", out var ci) ? ci as string : null;
                if (icon is not null && !IsHex(icon)) icon = null;
                result[appId] = new SteamAppMeta(appId, name, type, FindWindowsExecutable(config), icon);
            }
            catch
            {
                // A malformed entry should not stop the scan.
            }
            fs.Position = end;
        }
        return result;
    }

    private static Dictionary<string, object>? Child(Dictionary<string, object>? d, string key) =>
        d is not null && d.TryGetValue(key, out var v) ? v as Dictionary<string, object> : null;

    private static string? FindWindowsExecutable(Dictionary<string, object>? config)
    {
        var launch = Child(config, "launch");
        if (launch is null) return null;
        string? fallback = null;
        foreach (var (_, entryObj) in launch.OrderBy(k => int.TryParse(k.Key, out var i) ? i : 999))
        {
            if (entryObj is not Dictionary<string, object> entry) continue;
            if (!entry.TryGetValue("executable", out var exeObj) || exeObj is not string exe || exe.Length == 0) continue;
            var cfg = Child(entry, "config");
            string? os = cfg is not null && cfg.TryGetValue("oslist", out var o) ? o as string : null;
            if (!exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
            if (os is null || os.Contains("windows", StringComparison.OrdinalIgnoreCase))
            {
                // Prefer entries without a special "type" such as "option2"/"server".
                if (entry.TryGetValue("type", out var typ) && typ is string ts && ts is not ("default" or "none" or ""))
                {
                    fallback ??= exe;
                    continue;
                }
                return exe;
            }
        }
        return fallback;
    }

    private static bool IsHex(string s) => s.Length is > 8 and < 80 && s.All(Uri.IsHexDigit);

    private static Dictionary<string, object> ReadObject(BinaryReader br, string[]? strings, long end, int depth)
    {
        if (depth > 32) throw new InvalidDataException("Nesting too deep"); // protects against stack overflow on corrupt data
        var d = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        while (br.BaseStream.Position < end)
        {
            byte type = br.ReadByte();
            if (type == 0x08) break; // end of object
            string key = strings is null ? ReadCString(br) : SafeIndex(strings, br.ReadInt32());
            switch (type)
            {
                case 0x00: d[key] = ReadObject(br, strings, end, depth + 1); break;
                case 0x01: d[key] = ReadCString(br); break;
                case 0x02: d[key] = br.ReadInt32(); break;
                case 0x03: d[key] = br.ReadSingle(); break;
                case 0x04: d[key] = br.ReadInt32(); break; // pointer
                case 0x05: d[key] = ReadWString(br); break;
                case 0x06: d[key] = br.ReadInt32(); break; // color
                case 0x07: d[key] = br.ReadUInt64(); break;
                case 0x0A: d[key] = br.ReadInt64(); break;
                default: throw new InvalidDataException($"Unknown KV type {type}");
            }
        }
        return d;
    }

    private static string SafeIndex(string[] s, int i) => (uint)i < (uint)s.Length ? s[i] : "";

    private static string ReadCString(BinaryReader br)
    {
        var bytes = new List<byte>(32);
        byte b;
        while ((b = br.ReadByte()) != 0)
        {
            bytes.Add(b);
            if (bytes.Count > 1 << 20) throw new InvalidDataException("String too long");
        }
        return Encoding.UTF8.GetString(bytes.ToArray());
    }

    private static string ReadWString(BinaryReader br)
    {
        var sb = new StringBuilder();
        char c;
        while ((c = (char)br.ReadUInt16()) != 0) sb.Append(c);
        return sb.ToString();
    }
}
