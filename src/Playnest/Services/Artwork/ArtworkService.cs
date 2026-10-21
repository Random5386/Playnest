using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;
using Playnest.Models;
using Playnest.Services.Platform;
using Playnest.Services.Net;
using Playnest.Services.Scanning;

namespace Playnest.Services.Artwork;

public enum ArtKind { Cover, Icon, Hero }

/// <summary>
/// Finds the best available artwork for a game and caches it on disk:
/// custom image → store files already on disk → Steam's CDN (no key) → SteamGridDB (with key) → icon in the exe.
/// Work runs on at most two background threads; results are remembered so nothing is fetched twice.
/// </summary>
public sealed partial class ArtworkService
{
    private readonly Func<AppSettings> _settings;
    private readonly SemaphoreSlim _localGate = new(2);
    private readonly SemaphoreSlim _netGate = new(3);
    private readonly ConcurrentDictionary<string, Lazy<Task<string?>>> _inflight = new();
    private static readonly TimeSpan MissRetry = TimeSpan.FromDays(7);

    public ArtworkService(Func<AppSettings> settings) => _settings = settings;

    public static string FolderFor(GameEntry e)
    {
        var safe = string.Concat(e.Id.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_'));
        return Path.Combine(AppPaths.ArtDir, safe);
    }

    private static string FileName(ArtKind k) => k switch { ArtKind.Cover => "cover", ArtKind.Icon => "icon", _ => "hero" };

    /// <summary>Returns a cached or custom image path immediately if one exists (no I/O beyond a few File.Exists).</summary>
    public string? Cached(GameEntry e, ArtKind kind)
    {
        var custom = kind switch { ArtKind.Cover => e.CustomCover, ArtKind.Icon => e.CustomIcon, _ => e.CustomHero };
        if (!string.IsNullOrWhiteSpace(custom) && File.Exists(custom)) return custom;
        var dir = FolderFor(e);
        var name = FileName(kind);
        foreach (var ext in new[] { ".jpg", ".png", ".webp" })
        {
            var p = Path.Combine(dir, name + ext);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    /// <summary>Finds artwork (possibly downloading it). Returns a local file path or null.</summary>
    public Task<string?> GetAsync(GameEntry e, ArtKind kind, bool force = false)
    {
        if (!force && Cached(e, kind) is { } c) return Task.FromResult<string?>(c);
        // A forced refresh gets its own slot, so it isn't swallowed by a normal lookup that's already running.
        var key = e.Id + "|" + kind + (force ? "|refresh" : "");
        // Lazy makes sure only one task is ever created per key, and the entry is removed only after it finished.
        var lazy = _inflight.GetOrAdd(key, _ => new Lazy<Task<string?>>(() => Task.Run(async () =>
        {
            try { return await ResolveAsync(e, kind, force).ConfigureAwait(false); }
            catch (Exception ex) { Log.Error($"artwork {kind} {e.DisplayName}", ex); return null; }
        })));
        var task = lazy.Value;
        task.ContinueWith(_ => _inflight.TryRemove(new KeyValuePair<string, Lazy<Task<string?>>>(key, lazy)), TaskScheduler.Default);
        return task;
    }

    public void Clear(GameEntry e)
    {
        try
        {
            var dir = FolderFor(e);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
        catch (Exception ex) { Log.Error("clear art", ex); }
    }

    public static long CacheSizeBytes()
    {
        try
        {
            if (!Directory.Exists(AppPaths.ArtDir)) return 0;
            return new DirectoryInfo(AppPaths.ArtDir).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        }
        catch { return 0; }
    }

    private const string CacheVersion = "2";

    /// <summary>
    /// One-time clean-up when the artwork rules change: forget "not found" markers and drop low-resolution
    /// covers/icons so better ones are fetched. Runs in the background at start-up.
    /// </summary>
    public static void MigrateCache()
    {
        try
        {
            var marker = Path.Combine(AppPaths.ArtDir, "version.txt");
            if (File.Exists(marker) && File.ReadAllText(marker).Trim() == CacheVersion) return;
            if (Directory.Exists(AppPaths.ArtDir))
            {
                foreach (var f in Directory.EnumerateFiles(AppPaths.ArtDir, "*.miss", SearchOption.AllDirectories)) TryDelete(f);
                foreach (var f in Directory.EnumerateFiles(AppPaths.ArtDir, "cover.*", SearchOption.AllDirectories))
                    if ((ImageInfo.SizeOfFile(f)?.Width ?? 0) < GoodCoverWidth) TryDelete(f);
                foreach (var f in Directory.EnumerateFiles(AppPaths.ArtDir, "icon.*", SearchOption.AllDirectories))
                    if ((ImageInfo.SizeOfFile(f)?.Width ?? 0) < GoodIconWidth) TryDelete(f);
            }
            Directory.CreateDirectory(AppPaths.ArtDir);
            File.WriteAllText(marker, CacheVersion);
        }
        catch (Exception ex) { Log.Error("migrate art cache", ex); }
    }

    public static void ClearAll()
    {
        try
        {
            if (Directory.Exists(AppPaths.ArtDir)) Directory.Delete(AppPaths.ArtDir, true);
            Directory.CreateDirectory(AppPaths.ArtDir);
        }
        catch (Exception ex) { Log.Error("clear cache", ex); }
    }

    // ------------------------------------------------------------------ resolution

    /// <summary>Covers narrower than this are treated as low resolution (Steam keeps 300×450 copies locally).</summary>
    private const int GoodCoverWidth = 600;
    /// <summary>Icons smaller than this are worth replacing with a sharper one when possible.</summary>
    private const int GoodIconWidth = 128;

    private async Task<string?> ResolveAsync(GameEntry e, ArtKind kind, bool force)
    {
        var dir = FolderFor(e);
        Directory.CreateDirectory(dir);
        var name = FileName(kind);
        var miss = Path.Combine(dir, name + ".miss");
        if (force)
        {
            foreach (var f in Directory.GetFiles(dir, name + ".*")) TryDelete(f);
        }
        else if (File.Exists(miss) && DateTime.UtcNow - File.GetLastWriteTimeUtc(miss) < MissRetry)
        {
            return null;
        }

        var s = _settings();
        byte[]? best;
        if (kind == ArtKind.Icon) best = await ResolveIconAsync(e, s).ConfigureAwait(false);
        else
        {
            // 1) Local copy (fast, offline). Good enough unless it is a low-resolution cover.
            byte[]? local;
            await _localGate.WaitAsync().ConfigureAwait(false);
            try { local = FindLocal(e, kind); }
            finally { _localGate.Release(); }

            best = local;
            bool lowRes = local is null || (kind == ArtKind.Cover && ImageInfo.Width(local) < GoodCoverWidth);
            if (lowRes && s.DownloadArtwork)
            {
                await _netGate.WaitAsync().ConfigureAwait(false);
                try { best = await FindOnlineAsync(e, kind, s).ConfigureAwait(false) ?? local; }
                finally { _netGate.Release(); }
            }
            if (best is null && kind == ArtKind.Hero && e.Source == GameSource.Xbox && e.InstallDir is not null)
                best = ReadImage(XboxAsset(e.InstallDir, ArtKind.Hero));
        }

        if (best is not null)
        {
            var dest = Path.Combine(dir, name + Ext(best));
            foreach (var f in Directory.GetFiles(dir, name + ".*")) TryDelete(f);
            await File.WriteAllBytesAsync(dest, best).ConfigureAwait(false);
            return dest;
        }

        try { File.WriteAllText(miss, ""); File.SetLastWriteTimeUtc(miss, DateTime.UtcNow); } catch { }
        return null;
    }

    /// <summary>Local cover/background: Steam's own cache.</summary>
    private static byte[]? FindLocal(GameEntry e, ArtKind kind)
    {
        if (e.SteamAppId > 0 && SteamScanner.SteamPath is { } steam)
        {
            var file = SteamLibraryCache(steam, e.SteamAppId, kind);
            if (file is not null && ReadImage(file) is { } b) return b;
        }
        return null;
    }

    private async Task<byte[]?> FindOnlineAsync(GameEntry e, ArtKind kind, AppSettings s)
    {
        // Epic games: the exact official art from the launcher's own catalog
        if (e.Source == GameSource.Epic && e.SourceId is not null && EpicCatalog.ForAppName(e.SourceId) is { } epic)
        {
            var b = await DownloadImageAsync(kind == ArtKind.Cover ? epic.Tall : epic.Wide).ConfigureAwait(false);
            if (b is not null) return b;
        }

        long steamId = await SteamIdAsync(e).ConfigureAwait(false);
        if (steamId > 0)
        {
            var b = await SteamCdnAsync(steamId, kind).ConfigureAwait(false);
            if (b is not null) return b;
        }

        // Not on Steam, but owned on Epic (e.g. installed from a disc or another store)
        if (e.EffectiveKind == ItemKind.Game && e.Source != GameSource.Epic && EpicCatalog.ForTitle(e.DisplayName) is { } owned)
        {
            var b = await DownloadImageAsync(kind == ArtKind.Cover ? owned.Tall : owned.Wide).ConfigureAwait(false);
            if (b is not null) return b;
        }

        if (!string.IsNullOrWhiteSpace(s.SteamGridDbApiKey))
        {
            var b = await SteamGridDbAsync(e, kind, steamId, s.SteamGridDbApiKey.Trim()).ConfigureAwait(false);
            if (b is not null) return b;
        }
        return null;
    }

    private async Task<long> SteamIdAsync(GameEntry e)
    {
        if (e.SteamAppId > 0) return e.SteamAppId;
        if (e.ArtSteamAppId != 0) return e.ArtSteamAppId;
        if (e.EffectiveKind != ItemKind.Game) return 0;
        var id = await SteamSearchAsync(e.DisplayName).ConfigureAwait(false);
        e.ArtSteamAppId = id == 0 ? -1 : id;
        return id;
    }

    // ------------------------------------------------------------------ icons

    /// <summary>
    /// Collects every icon available on disk and keeps the largest; when that is still small (old games often only
    /// ship 32–48 px icons) it tries Steam's high-resolution icon for the same game.
    /// </summary>
    private async Task<byte[]?> ResolveIconAsync(GameEntry e, AppSettings s)
    {
        byte[]? best = null;
        void Consider(byte[]? b)
        {
            // Only real PNG/JPEG/WebP data with sane dimensions is kept (same rule as downloads).
            if (b is not null && ImageInfo.Size(b) is { Width: > 0 and <= 4096, Height: > 0 and <= 4096 } && ImageInfo.Width(b) > ImageInfo.Width(best)) best = b;
        }

        await _localGate.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var candidate in LocalIcons(e)) Consider(candidate);
        }
        finally { _localGate.Release(); }

        if (ImageInfo.Width(best) < GoodIconWidth && s.DownloadArtwork && e.EffectiveKind == ItemKind.Game)
        {
            await _netGate.WaitAsync().ConfigureAwait(false);
            try
            {
                long steamId = await SteamIdAsync(e).ConfigureAwait(false);
                if (steamId > 0)
                {
                    if (e.SteamClientIcon is { } hash)
                        Consider(IcoToPng(await DownloadAsync($"https://cdn.cloudflare.steamstatic.com/steamcommunity/public/images/apps/{steamId}/{hash}.ico").ConfigureAwait(false)));
                    if (ImageInfo.Width(best) < GoodIconWidth && await SteamAssetsAsync(steamId).ConfigureAwait(false) is { CommunityIcon: { } ci })
                        Consider(await DownloadImageAsync($"https://cdn.cloudflare.steamstatic.com/steamcommunity/public/images/apps/{steamId}/{ci}.jpg").ConfigureAwait(false));
                }
                if (ImageInfo.Width(best) < GoodIconWidth && !string.IsNullOrWhiteSpace(s.SteamGridDbApiKey))
                    Consider(await SteamGridDbAsync(e, ArtKind.Icon, steamId, s.SteamGridDbApiKey.Trim()).ConfigureAwait(false));
            }
            finally { _netGate.Release(); }
        }
        return best;
    }

    private static IEnumerable<byte[]?> LocalIcons(GameEntry e)
    {
        var dir = e.InstallDir;

        // GOG ships a high-res goggame-<id>.ico
        if (e.Source == GameSource.Gog && dir is not null && e.SourceId is not null)
            yield return PeIcon.ExtractLargestPng(Path.Combine(dir, $"goggame-{e.SourceId}.ico"));

        // Xbox / Game Pass logos (largest scale variant)
        if (e.Source == GameSource.Xbox && dir is not null)
            yield return ReadImage(XboxAsset(dir, ArtKind.Icon));

        // Steam's own copy of the game's icon (used for desktop shortcuts)
        if (e.SteamClientIcon is { } hash && SteamScanner.SteamPath is { } steam)
            yield return PeIcon.ExtractLargestPng(Path.Combine(steam, "steam", "games", hash + ".ico"));

        // The game's exe
        yield return ExeIcon(e);

        // Any .ico shipped in the game folder (many games include a high-res one)
        if (dir is not null && Directory.Exists(dir))
        {
            IEnumerable<string> icos;
            try
            {
                icos = Directory.EnumerateFiles(dir, "*.ico", new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 2, IgnoreInaccessible = true })
                    .Take(8).ToList();
            }
            catch { icos = []; }
            foreach (var ico in icos) yield return PeIcon.ExtractLargestPng(ico);
        }
    }

    private static byte[]? IcoToPng(byte[]? ico)
    {
        if (ico is null || ico.Length < 6 || ico[0] != 0 || ico[1] != 0 || ico[2] != 1) return null;
        return PeIcon.FromIcoBytes(ico);
    }

    // ------------------------------------------------------------------ Steam (local)

    internal static string? SteamLibraryCache(string steam, long appId, ArtKind kind)
    {
        var cache = Path.Combine(steam, "appcache", "librarycache");
        string[] wanted = kind switch
        {
            ArtKind.Cover => ["library_600x900_2x", "library_600x900", "library_capsule_2x", "library_capsule"],
            ArtKind.Hero => ["library_hero_2x", "library_hero"],
            _ => [],
        };
        if (wanted.Length == 0) return null;

        // Old layout: librarycache\<id>_library_600x900.jpg
        foreach (var w in wanted)
        {
            var p = Path.Combine(cache, $"{appId}_{w}.jpg");
            if (File.Exists(p)) return p;
        }
        // New layout (2024+): librarycache\<id>\[<hash>\]library_600x900.jpg
        var dir = Path.Combine(cache, appId.ToString());
        if (!Directory.Exists(dir)) return null;
        try
        {
            var files = Directory.GetFiles(dir, "*.*", SearchOption.AllDirectories);
            foreach (var w in wanted)
                foreach (var f in files)
                    if (Path.GetFileNameWithoutExtension(f).Equals(w, StringComparison.OrdinalIgnoreCase)) return f;
        }
        catch { }
        return null;
    }

    // ------------------------------------------------------------------ Steam (online, keyless)

    private sealed record SteamAssets(string Format, Dictionary<string, string> Files, string? CommunityIcon);
    private readonly ConcurrentDictionary<long, SteamAssets?> _steamAssets = new();

    /// <summary>Asks Steam's public store API for the exact artwork file names of an app (newer games use hashed paths).</summary>
    private async Task<SteamAssets?> SteamAssetsAsync(long appId)
    {
        if (_steamAssets.TryGetValue(appId, out var cached)) return cached;
        SteamAssets? result = null;
        try
        {
            var input = $"{{\"ids\":[{{\"appid\":{appId}}}],\"context\":{{\"language\":\"english\",\"country_code\":\"US\"}},\"data_request\":{{\"include_assets\":true}}}}";
            var url = "https://api.steampowered.com/IStoreBrowseService/GetItems/v1?input_json=" + Uri.EscapeDataString(input);
            var json = await DownloadStringAsync(url).ConfigureAwait(false);
            if (json is not null)
            {
                using var doc = JsonDocument.Parse(json);
                var item = doc.RootElement.GetProperty("response").GetProperty("store_items")[0];
                if (item.TryGetProperty("assets", out var assets) && assets.TryGetProperty("asset_url_format", out var fmt) &&
                    fmt.GetString() is { } format && format.Contains("${FILENAME}") && SafeAssetPath().IsMatch(format.Replace("${FILENAME}", "x")))
                {
                    var files = new Dictionary<string, string>();
                    foreach (var p in assets.EnumerateObject())
                        if (p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() is { Length: > 0 and < 200 } v && SafeAssetPath().IsMatch(v)) files[p.Name] = v;
                    files.TryGetValue("community_icon", out var icon);
                    result = new SteamAssets(format, files, icon is not null && icon.All(Uri.IsHexDigit) ? icon : null);
                }
            }
        }
        catch { }
        if (_steamAssets.Count > 2000) _steamAssets.Clear();
        _steamAssets[appId] = result;
        return result;
    }

    /// <summary>Asset paths from Steam's API may only contain ordinary URL path characters (no "..", no host tricks).</summary>
    [GeneratedRegex(@"^(?!.*\.\.)[A-Za-z0-9_\-./?=&]+$")]
    private static partial Regex SafeAssetPath();

    internal static bool IsSafeAssetPath(string s) => SafeAssetPath().IsMatch(s);

    private async Task<byte[]?> SteamCdnAsync(long appId, ArtKind kind)
    {
        string[] keys = kind == ArtKind.Cover ? ["library_capsule_2x", "library_capsule"] : ["library_hero_2x", "library_hero"];
        if (await SteamAssetsAsync(appId).ConfigureAwait(false) is { } assets)
        {
            foreach (var k in keys)
            {
                if (!assets.Files.TryGetValue(k, out var file)) continue;
                var path = assets.Format.Replace("${FILENAME}", file);
                if (path.Contains("..")) continue;
                var b = await DownloadImageAsync("https://shared.akamai.steamstatic.com/store_item_assets/" + path).ConfigureAwait(false);
                if (b is not null) return b;
            }
        }

        // Fall back to the classic fixed URLs.
        string[] names = kind == ArtKind.Cover ? ["library_600x900_2x.jpg", "library_600x900.jpg"] : ["library_hero.jpg"];
        foreach (var n in names)
        {
            var b = await DownloadImageAsync($"https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/{appId}/{n}").ConfigureAwait(false)
                    ?? await DownloadImageAsync($"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/{n}").ConfigureAwait(false);
            if (b is not null) return b;
        }
        return null;
    }

    /// <summary>
    /// Finds the Steam app id for a non-Steam game by name, so its official artwork can be used. Tries the full name,
    /// then simpler versions ("The Witcher 3: Wild Hunt - Remastered" → "The Witcher 3: Wild Hunt"). 0 = no confident match.
    /// </summary>
    private async Task<long> SteamSearchAsync(string name)
    {
        foreach (var query in SearchQueries(name))
        {
            try
            {
                var json = await DownloadStringAsync("https://store.steampowered.com/api/storesearch/?l=english&cc=US&term=" + Uri.EscapeDataString(query)).ConfigureAwait(false);
                if (json is null) continue;
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("items", out var items)) continue;
                if (items.ValueKind != JsonValueKind.Array) continue;
                var id = BestNameMatch(name, items.EnumerateArray().Take(25)
                    .Select(i => (Name: i.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? "" : "",
                                  Id: i.TryGetProperty("id", out var idEl) && idEl.TryGetInt64(out var v) && v is > 0 and < 100_000_000 ? v : 0))
                    .Where(x => x.Id > 0));
                if (id > 0) return id;
            }
            catch { }
        }
        return 0;
    }

    /// <summary>Search terms from most to least specific. Exposed for tests.</summary>
    internal static List<string> SearchQueries(string name)
    {
        var list = new List<string>();
        void Add(string q)
        {
            q = PathUtil.CleanTitle(q).Trim(' ', '-', ':', '–');
            if (q.Length >= 2 && !list.Contains(q, StringComparer.OrdinalIgnoreCase)) list.Add(q);
        }
        Add(name);
        var noEdition = Regex.Replace(name, @"\b(game of the year|goty|definitive|ultimate|deluxe|complete|digital deluxe|gold|enhanced|remastered|remaster|director'?s cut|anniversary|special|collector'?s|premium|standard)\b( edition)?|\bedition\b", "", RegexOptions.IgnoreCase);
        Add(Regex.Replace(noEdition, @"\s{2,}", " "));
        foreach (var sep in new[] { " - ", " – ", ": " })
        {
            int i = name.IndexOf(sep, StringComparison.Ordinal);
            if (i > 2) Add(name[..i]);
        }
        return list.Take(3).ToList();
    }

    /// <summary>Picks a candidate whose name matches closely enough. Exposed for tests.</summary>
    internal static long BestNameMatch(string wanted, IEnumerable<(string Name, long Id)> candidates)
    {
        var w = PathUtil.NormalizeName(wanted);
        if (w.Length == 0) return 0;
        var wTokens = Tokens(wanted);
        long best = 0;
        int bestScore = 0;
        foreach (var (name, id) in candidates)
        {
            var c = PathUtil.NormalizeName(name);
            int score = 0;
            if (c == w) score = 100;
            else if (c.Length > 0 && (c.StartsWith(w) || w.StartsWith(c)) && Math.Abs(c.Length - w.Length) <= Math.Max(4, w.Length / 4)) score = 60;
            else
            {
                // Word overlap: "Kingdom Hearts HD 2.8 FCP" ~ "KINGDOM HEARTS HD 2.8 Final Chapter Prologue"
                var cTokens = Tokens(name);
                if (wTokens.Count >= 2 && cTokens.Count >= 2 && wTokens[0] == cTokens[0])
                {
                    var (small, large) = wTokens.Count <= cTokens.Count ? (wTokens, cTokens) : (cTokens, wTokens);
                    double contained = small.Count(large.Contains) / (double)small.Count;
                    double ratio = small.Count / (double)large.Count;
                    if (contained >= 0.8 && ratio >= 0.5) score = 40 + (int)(contained * 10);
                }
            }
            if (score > bestScore) { bestScore = score; best = id; }
        }
        return best;
    }

    private static List<string> Tokens(string s)
    {
        var words = Regex.Split(s.ToLowerInvariant().Replace("™", "").Replace("®", ""), @"[^\p{L}\p{N}]+")
            .Where(x => x.Length > 0 && x is not ("the" or "and" or "of" or "a" or "dlc" or "edition"));
        return words.ToList();
    }

    // ------------------------------------------------------------------ SteamGridDB (optional key)

    private async Task<byte[]?> SteamGridDbAsync(GameEntry e, ArtKind kind, long steamId, string key)
    {
        if (!IsValidKey(key)) return null;
        try
        {
            const string api = "https://www.steamgriddb.com/api/v2";
            string? gameRef = steamId > 0 ? $"steam/{steamId}" : null;
            if (gameRef is null)
            {
                using var search = await SgdbGet($"{api}/search/autocomplete/{Uri.EscapeDataString(e.DisplayName)}", key).ConfigureAwait(false);
                if (search is null) return null;
                var data = search.RootElement.GetProperty("data");
                if (data.GetArrayLength() == 0) return null;
                var gid = BestNameMatch(e.DisplayName, data.EnumerateArray().Select(x => (x.GetProperty("name").GetString() ?? "", x.GetProperty("id").GetInt64())));
                if (gid == 0) gid = data[0].GetProperty("id").GetInt64();
                gameRef = $"game/{gid}";
            }

            string url = kind switch
            {
                ArtKind.Cover => $"{api}/grids/{gameRef}?dimensions=600x900,342x482,660x930&types=static&nsfw=false&humor=false",
                ArtKind.Hero => $"{api}/heroes/{gameRef}?types=static&nsfw=false&humor=false",
                _ => $"{api}/icons/{gameRef}?types=static&mimes=image/png&nsfw=false&humor=false",
            };
            using var res = await SgdbGet(url, key).ConfigureAwait(false);
            if (res is null) return null;
            foreach (var item in res.RootElement.GetProperty("data").EnumerateArray().Take(3))
            {
                var imgUrl = item.GetProperty("url").GetString();
                if (imgUrl is null) continue;
                var b = await DownloadImageAsync(imgUrl).ConfigureAwait(false);
                if (b is not null) return b;
            }
        }
        catch (Exception ex) { Log.Error("steamgriddb", ex); }
        return null;
    }

    /// <summary>Calls the SteamGridDB API (the key is sent only to www.steamgriddb.com — see <see cref="SafeHttp"/>).</summary>
    private static async Task<JsonDocument?> SgdbGet(string url, string key)
    {
        var json = await SafeHttp.GetStringAsync(url, key).ConfigureAwait(false);
        if (json is null) return null;
        try
        {
            var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.True &&
                doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
                return doc;
            doc.Dispose();
        }
        catch (JsonException) { }
        return null;
    }

    /// <summary>Checks the SteamGridDB key (used by the Settings page).</summary>
    public async Task<bool> TestSteamGridDbKeyAsync(string key)
    {
        // Keys are short tokens; refuse anything that could smuggle header characters.
        key = key.Trim();
        if (!IsValidKey(key)) return false;
        using var doc = await SgdbGet("https://www.steamgriddb.com/api/v2/search/autocomplete/portal", key).ConfigureAwait(false);
        return doc is not null;
    }

    internal static bool IsValidKey(string key) => key.Length is >= 8 and <= 128 && key.All(char.IsAsciiLetterOrDigit);

    private const int MaxDownloadBytes = SafeHttp.MaxImageBytes;
    private const int MaxImageDimension = 8192;

    /// <summary>
    /// Downloads an image through <see cref="SafeHttp"/> and only accepts it when it really is a JPEG/PNG/WebP with sane
    /// dimensions (a tiny file claiming to be 50 000 px wide would make the decoder allocate gigabytes).
    /// </summary>
    private static async Task<byte[]?> DownloadImageAsync(string? url)
    {
        var b = await SafeHttp.GetBytesAsync(url, MaxDownloadBytes).ConfigureAwait(false);
        return b is not null && IsImage(b) ? b : null;
    }

    private static Task<string?> DownloadStringAsync(string url) => SafeHttp.GetStringAsync(url);

    private static Task<byte[]?> DownloadAsync(string? url) => SafeHttp.GetBytesAsync(url, 2 * 1024 * 1024);

    // ------------------------------------------------------------------ Xbox

    private static string? XboxAsset(string content, ArtKind kind)
    {
        var cfg = Path.Combine(content, "MicrosoftGame.config");
        if (!File.Exists(cfg)) return null;
        try
        {
            var shell = SafeXml.Load(cfg).Descendants().FirstOrDefault(x => x.Name.LocalName == "ShellVisuals");
            if (shell is null) return null;
            string[] attrs = kind switch
            {
                ArtKind.Icon => ["Square480x480Logo", "Square150x150Logo", "StoreLogo"],
                ArtKind.Hero => ["SplashScreenImage"],
                _ => [],
            };
            // Take the biggest image among all listed logos and their scaled variants (Logo.scale-400.png …).
            string? best = null;
            int bestWidth = 0;
            foreach (var a in attrs)
            {
                var rel = shell.Attribute(a)?.Value;
                if (string.IsNullOrEmpty(rel)) continue;
                var p = Path.GetFullPath(Path.Combine(content, rel));
                if (!PathUtil.IsUnder(p, content)) continue; // never read outside the game folder
                var dir = Path.GetDirectoryName(p)!;
                var stem = Path.GetFileNameWithoutExtension(p);
                var files = new List<string>();
                if (File.Exists(p)) files.Add(p);
                if (Directory.Exists(dir)) files.AddRange(Directory.GetFiles(dir, stem + ".scale-*" + Path.GetExtension(p)));
                foreach (var f in files)
                {
                    int w = ImageInfo.SizeOfFile(f)?.Width ?? 0;
                    if (w > bestWidth) { bestWidth = w; best = f; }
                }
                if (best is not null && kind == ArtKind.Hero) break;
            }
            return best;
        }
        catch { }
        return null;
    }

    // ------------------------------------------------------------------ exe icon

    private static byte[]? ExeIcon(GameEntry e)
    {
        var exe = e.EffectiveExe;
        // Store clients (Battle.net / Riot) are the "exe" for some games; use the game's own exe instead.
        if (exe is not null && e.InstallDir is not null && !PathUtil.IsUnder(exe, e.InstallDir)) exe = null;
        if (exe is null && e.InstallDir is not null) exe = GameClassifier.Inspect(e.InstallDir, e.DisplayName).BestExe;
        return exe is null ? null : PeIcon.ExtractLargestPng(exe);
    }

    // ------------------------------------------------------------------ helpers

    private static byte[]? ReadImage(string? path)
    {
        if (path is null) return null;
        try
        {
            if (new FileInfo(path).Length > MaxDownloadBytes) return null;
            var b = File.ReadAllBytes(path);
            return IsImage(b) ? b : null;
        }
        catch { return null; }
    }

    internal static bool IsImage(byte[] b)
    {
        bool magic = b.Length > 512 && (
            (b[0] == 0xFF && b[1] == 0xD8) ||                                   // JPEG
            (b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) ||   // PNG
            (b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F' && b[8] == 'W' && b[9] == 'E')); // WEBP
        if (!magic) return false;
        // Header must state sane dimensions, otherwise the file is refused before it ever reaches the decoder.
        return ImageInfo.Size(b) is { Width: > 0 and <= MaxImageDimension, Height: > 0 and <= MaxImageDimension };
    }

    private static string Ext(byte[] b) => b[0] == 0xFF ? ".jpg" : b[0] == 0x89 ? ".png" : ".webp";

    private static void TryDelete(string p)
    {
        try { if (File.Exists(p)) File.Delete(p); } catch { }
    }
}
