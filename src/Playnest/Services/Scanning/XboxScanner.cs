using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Playnest.Models;
using Playnest.Services.Platform;

namespace Playnest.Services.Scanning;

/// <summary>
/// Xbox app / PC Game Pass games. These live in folders such as "C:\XboxGames\&lt;Game&gt;\Content" and each one has a
/// MicrosoftGame.config describing it. The folder roots are read from the hidden ".GamingRoot" file on every drive.
/// </summary>
public sealed class XboxScanner : IGameScanner
{
    public GameSource Source => GameSource.Xbox;
    public bool IsEnabled(AppSettings s) => s.ScanXbox;

    public IEnumerable<GameEntry> Scan(ScanContext ctx)
    {
        foreach (var root in GamingRoots())
        {
            string[] games;
            try { games = Directory.GetDirectories(root); } catch { continue; }
            foreach (var g in games)
            {
                var content = Path.Combine(g, "Content");
                if (!Directory.Exists(content)) content = g;
                var e = ReadGame(content, ctx);
                if (e is not null) yield return e;
            }
        }
    }

    public static IEnumerable<string> GamingRoots()
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var drive in SafeDrives())
        {
            var marker = Path.Combine(drive, ".GamingRoot");
            if (File.Exists(marker))
            {
                foreach (var rel in ReadGamingRoot(marker))
                {
                    var full = Path.Combine(drive, rel);
                    if (Directory.Exists(full)) roots.Add(full);
                }
            }
            var def = Path.Combine(drive, "XboxGames");
            if (Directory.Exists(def)) roots.Add(def);
        }
        return roots;
    }

    /// <summary>.GamingRoot = "RGBX" + uint32 count + UTF-16 null-terminated relative folder names.</summary>
    internal static List<string> ReadGamingRoot(string file)
    {
        var list = new List<string>();
        try
        {
            var bytes = File.ReadAllBytes(file);
            if (bytes.Length < 8 || Encoding.ASCII.GetString(bytes, 0, 4) != "RGBX") return list;
            var text = Encoding.Unicode.GetString(bytes, 8, bytes.Length - 8);
            foreach (var part in text.Split('\0', StringSplitOptions.RemoveEmptyEntries))
            {
                var p = part.Trim().TrimStart('\\');
                if (p.Length > 0) list.Add(p);
            }
        }
        catch { }
        return list;
    }

    private static GameEntry? ReadGame(string content, ScanContext ctx)
    {
        var cfgPath = Path.Combine(content, "MicrosoftGame.config");
        var manifestPath = Path.Combine(content, "appxmanifest.xml");
        if (!File.Exists(cfgPath) && !File.Exists(manifestPath)) return null;
        try
        {
            string? identity = null, publisher = null, display = null, exeName = null, appId = null;
            if (File.Exists(manifestPath))
            {
                var m = SafeXml.Load(manifestPath).Root!;
                var id = m.Elements().FirstOrDefault(e => e.Name.LocalName == "Identity");
                identity = id?.Attribute("Name")?.Value;
                publisher = id?.Attribute("Publisher")?.Value;
                var app = m.Descendants().FirstOrDefault(e => e.Name.LocalName == "Application");
                appId = app?.Attribute("Id")?.Value;
                exeName ??= app?.Attribute("Executable")?.Value;
                display = m.Descendants().FirstOrDefault(e => e.Name.LocalName == "DisplayName")?.Value;
            }
            if (File.Exists(cfgPath))
            {
                var c = SafeXml.Load(cfgPath).Root!;
                var id = c.Descendants().FirstOrDefault(e => e.Name.LocalName == "Identity");
                identity ??= id?.Attribute("Name")?.Value;
                publisher ??= id?.Attribute("Publisher")?.Value;
                var shell = c.Descendants().FirstOrDefault(e => e.Name.LocalName == "ShellVisuals");
                var dn = shell?.Attribute("DefaultDisplayName")?.Value;
                if (!string.IsNullOrWhiteSpace(dn) && !dn.StartsWith("ms-resource", StringComparison.OrdinalIgnoreCase)) display = dn;
                var exe = c.Descendants().FirstOrDefault(e => e.Name.LocalName == "Executable");
                exeName = exe?.Attribute("Name")?.Value ?? exeName;
                appId ??= exe?.Attribute("Id")?.Value;
            }

            if (identity is null) return null;
            if (string.IsNullOrWhiteSpace(display) || display.StartsWith("ms-resource", StringComparison.OrdinalIgnoreCase))
                display = Path.GetFileName(Path.GetDirectoryName(content.TrimEnd('\\')) ?? content);
            if (GameClassifier.IsExcludedName(display!, ctx.Settings.ExtraExcludeKeywords)) return null;

            string? exePath = exeName is null ? null : Path.Combine(content, exeName);
            if (exePath is not null && !File.Exists(exePath)) exePath = null;
            // Packages without an executable are add-ons: DLC, pre-order bonuses, or parts of a collection
            // (e.g. the individual Halo games inside The Master Chief Collection). They're not separate games.
            if (exePath is null)
            {
                ctx.Claim(content);
                ctx.Claim(Path.GetDirectoryName(content));
                return null;
            }
            string? uri = null;
            if (publisher is not null && appId is not null)
                uri = $@"shell:AppsFolder\{identity}_{PublisherId(publisher)}!{appId}";

            ctx.Claim(content);
            ctx.Claim(Path.GetDirectoryName(content));
            return new GameEntry
            {
                Id = "xbox:" + identity,
                Name = PathUtil.CleanTitle(display!),
                Kind = ItemKind.Game,
                Source = GameSource.Xbox,
                SourceId = identity,
                InstallDir = content,
                ExePath = exePath,
                LaunchUri = uri,
                UninstallUri = "ms-settings:appsfeatures",
            };
        }
        catch (Exception ex)
        {
            Log.Error("xbox " + content, ex);
            return null;
        }
    }

    /// <summary>
    /// Computes the 13-character publisher id used in package family names
    /// (SHA-256 of the UTF-16 publisher string, first 8 bytes, Crockford base32).
    /// </summary>
    public static string PublisherId(string publisher)
    {
        const string alphabet = "0123456789abcdefghjkmnpqrstvwxyz";
        var hash = SHA256.HashData(Encoding.Unicode.GetBytes(publisher));
        ulong v = 0;
        for (int i = 0; i < 8; i++) v = (v << 8) | hash[i];
        var sb = new StringBuilder(13);
        // 64 bits + one padding zero bit = 65 bits = 13 groups of 5.
        for (int i = 0; i < 13; i++)
        {
            int shift = 59 - i * 5;
            int idx = shift >= 0 ? (int)((v >> shift) & 31) : (int)((v << 1) & 31);
            sb.Append(alphabet[idx]);
        }
        return sb.ToString();
    }

    internal static IEnumerable<string> SafeDrives()
    {
        DriveInfo[] drives;
        try { drives = DriveInfo.GetDrives(); } catch { yield break; }
        foreach (var d in drives)
        {
            bool ok;
            try { ok = d.DriveType == DriveType.Fixed && d.IsReady; } catch { ok = false; }
            if (ok) yield return d.RootDirectory.FullName;
        }
    }
}
