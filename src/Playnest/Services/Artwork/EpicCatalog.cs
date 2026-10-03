using System.Text;
using System.Text.Json;
using Playnest.Services.Platform;

namespace Playnest.Services.Artwork;

/// <summary>Official Epic store images for one catalog item.</summary>
public sealed record EpicImages(string Title, string? Tall, string? Wide, string? Logo);

/// <summary>
/// Reads the Epic Games Launcher's own local catalog cache (ProgramData\Epic\EpicGamesLauncher\Data\Catalog\catcache.bin,
/// base64-encoded JSON). It lists the games in the user's Epic library with links to Epic's official cover art,
/// so no Epic account login or API key is needed. Only the few fields Playnest needs are kept in memory.
/// </summary>
public static class EpicCatalog
{
    private static readonly object Gate = new();
    private static Dictionary<string, EpicImages>? _byAppName;
    private static List<EpicImages> _all = new();
    private static DateTime _loadedStamp;

    public static string CachePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "EpicGamesLauncher", "Data", "Catalog", "catcache.bin");

    /// <summary>Images for an installed Epic game (AppName from its manifest).</summary>
    public static EpicImages? ForAppName(string appName)
    {
        EnsureLoaded();
        lock (Gate) return _byAppName is not null && _byAppName.TryGetValue(appName, out var i) ? i : null;
    }

    /// <summary>Images for a game installed some other way but owned on Epic, matched by title.</summary>
    public static EpicImages? ForTitle(string title)
    {
        EnsureLoaded();
        List<EpicImages> all;
        lock (Gate) all = _all;
        var id = ArtworkService.BestNameMatch(title, all.Select((x, i) => (x.Title, (long)i + 1)));
        return id > 0 ? all[(int)id - 1] : null;
    }

    private static void EnsureLoaded()
    {
        var path = CachePath;
        DateTime stamp;
        try { stamp = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue; }
        catch { stamp = DateTime.MinValue; }
        lock (Gate)
        {
            if (_byAppName is not null && stamp == _loadedStamp) return;
            _loadedStamp = stamp;
            _byAppName = new Dictionary<string, EpicImages>(StringComparer.OrdinalIgnoreCase);
            _all = new List<EpicImages>();
            if (stamp == DateTime.MinValue) return;
            try
            {
                var info = new FileInfo(path);
                if (info.Length > 64L * 1024 * 1024) return; // not a real catalog cache
                Parse(File.ReadAllText(path), _byAppName, _all);
            }
            catch (Exception ex) { Log.Error("epic catalog", ex); }
        }
    }

    internal static void Parse(string base64, Dictionary<string, EpicImages> byApp, List<EpicImages> all)
    {
        var json = Encoding.UTF8.GetString(Convert.FromBase64String(base64.Trim()));
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return;
        foreach (var item in doc.RootElement.EnumerateArray())
        {
            if (!item.TryGetProperty("keyImages", out var imgs) || imgs.ValueKind != JsonValueKind.Array) continue;
            string? tall = null, wide = null, logo = null;
            foreach (var k in imgs.EnumerateArray())
            {
                var type = k.TryGetProperty("type", out var t) ? t.GetString() : null;
                var url = k.TryGetProperty("url", out var u) ? u.GetString() : null;
                if (url is null || !IsEpicCdn(url)) continue;
                switch (type)
                {
                    case "DieselGameBoxTall": tall ??= url; break;
                    case "OfferImageTall": tall ??= url; break;
                    case "DieselGameBox": wide ??= url; break;
                    case "OfferImageWide": wide ??= url; break;
                    case "DieselGameBoxLogo": logo ??= url; break;
                }
            }
            if (tall is null && wide is null) continue;
            var title = item.TryGetProperty("title", out var ti) ? ti.GetString() ?? "" : "";
            var images = new EpicImages(title, tall, wide, logo);
            all.Add(images);
            if (item.TryGetProperty("releaseInfo", out var rel) && rel.ValueKind == JsonValueKind.Array)
                foreach (var r in rel.EnumerateArray())
                    if (r.TryGetProperty("appId", out var a) && a.GetString() is { Length: > 0 } appId)
                        byApp.TryAdd(appId, images);
        }
    }

    /// <summary>Only Epic's own image hosts are trusted for downloads.</summary>
    internal static bool IsEpicCdn(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps &&
        (u.Host.EndsWith(".epicgames.com", StringComparison.OrdinalIgnoreCase) || u.Host.EndsWith(".unrealengine.com", StringComparison.OrdinalIgnoreCase));
}
