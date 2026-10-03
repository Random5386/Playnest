using System.Text;
using System.Text.RegularExpressions;

namespace Playnest.Services.Platform;

public static partial class PathUtil
{
    public static string? Clean(string? p)
    {
        if (string.IsNullOrWhiteSpace(p)) return null;
        p = p.Trim().Trim('"').Trim();
        p = Environment.ExpandEnvironmentVariables(p);
        if (p.Length > 3) p = p.TrimEnd('\\', '/');
        return p.Length == 0 ? null : p;
    }

    /// <summary>Key used to compare folders: full path, no trailing slash, lower case.</summary>
    public static string Key(string? p)
    {
        var c = Clean(p);
        if (c is null) return "";
        try { c = Path.GetFullPath(c.Replace('/', Path.DirectorySeparatorChar)); } catch { }
        return c.TrimEnd('\\', '/').ToLowerInvariant();
    }

    /// <summary>True when <paramref name="child"/> equals or lives under <paramref name="parent"/>.</summary>
    public static bool IsUnder(string child, string parent)
    {
        var c = Key(child);
        var p = Key(parent);
        if (c.Length == 0 || p.Length == 0) return false;
        return c == p || c.StartsWith(p + Path.DirectorySeparatorChar) || c.StartsWith(p + "/");
    }

    /// <summary>"C:\Games\X\x.exe",0 → C:\Games\X\x.exe (only when it is an existing .exe).</summary>
    public static string? ExeFromIconSpec(string? spec)
    {
        if (string.IsNullOrWhiteSpace(spec)) return null;
        var s = spec.Trim();
        if (s.StartsWith('"'))
        {
            int end = s.IndexOf('"', 1);
            s = end > 0 ? s[1..end] : s.Trim('"');
        }
        else
        {
            int comma = s.LastIndexOf(',');
            if (comma > 2 && int.TryParse(s[(comma + 1)..].Trim(), out _)) s = s[..comma];
        }
        s = Environment.ExpandEnvironmentVariables(s.Trim());
        return s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(s) ? s : null;
    }

    public static bool LooksLikeUninstaller(string path)
    {
        var n = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
        return n.StartsWith("unins") || n.Contains("uninstall") || n.Contains("remover") || n == "setup" || n.Contains("cleanup");
    }

    /// <summary>Splits a command line into the program and its arguments.</summary>
    public static (string File, string Args) SplitCommand(string command)
    {
        var s = command.Trim();
        if (s.StartsWith('"'))
        {
            int end = s.IndexOf('"', 1);
            if (end > 0) return (s[1..end], s[(end + 1)..].Trim());
        }
        int exe = s.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (exe > 0) return (s[..(exe + 4)], s[(exe + 4)..].Trim());
        int space = s.IndexOf(' ');
        return space > 0 ? (s[..space], s[(space + 1)..].Trim()) : (s, "");
    }

    /// <summary>Lower-case alphanumeric name used to compare game titles ("The Witcher® 3: Wild Hunt" → "witcher3wildhunt").</summary>
    public static string NormalizeName(string name)
    {
        var s = name.ToLowerInvariant();
        s = TrademarkRegex().Replace(s, "");
        s = EditionRegex().Replace(s, "");
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
        var r = sb.ToString();
        if (r.StartsWith("the") && r.Length > 6) r = r[3..];
        return r;
    }

    /// <summary>Removes trailing version numbers and store noise from installer names ("Foo, v1.2.3" → "Foo").</summary>
    public static string CleanTitle(string name)
    {
        var s = VersionSuffixRegex().Replace(name.Trim(), "");
        s = s.Replace("™", "").Replace("®", "").Replace("©", "").Trim();
        return s.Length == 0 ? name.Trim() : s;
    }

    /// <summary>Readable title from a folder name ("Five.Hearts.Under.One.Roof.season.2" → "Five Hearts Under One Roof season 2").</summary>
    public static string TitleFromFolder(string folder)
    {
        var s = folder.Trim();
        if (!s.Contains(' ') && (s.Contains('.') || s.Contains('_')))
            s = s.Replace('.', ' ').Replace('_', ' ');
        s = MultiSpaceRegex().Replace(s, " ").Trim();
        return CleanTitle(s);
    }

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex MultiSpaceRegex();

    [GeneratedRegex(@"[™®©]")]
    private static partial Regex TrademarkRegex();

    [GeneratedRegex(@"\b(game of the year|goty|definitive|ultimate|deluxe|complete|digital deluxe|standard|gold|enhanced|remastered|director'?s cut|anniversary|edition|bundle)\b")]
    private static partial Regex EditionRegex();

    [GeneratedRegex(@"(\s*[,\-–]?\s*\(?v?\d+(\.\d+){1,4}[a-z]?\)?\s*$)|(\s*\((x64|64-bit|32-bit|x86)\)\s*$)", RegexOptions.IgnoreCase)]
    private static partial Regex VersionSuffixRegex();
}

/// <summary>XML loading with DTDs and external entities disabled (protects against XXE / "billion laughs").</summary>
public static class SafeXml
{
    public static System.Xml.Linq.XDocument Load(string path)
    {
        var settings = new System.Xml.XmlReaderSettings
        {
            DtdProcessing = System.Xml.DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 10_000_000,
        };
        using var reader = System.Xml.XmlReader.Create(path, settings);
        return System.Xml.Linq.XDocument.Load(reader);
    }
}
