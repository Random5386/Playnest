using System.Text.Json;
using Playnest.Models;

namespace Playnest.Services.Artwork;

/// <summary>A game listed on SteamGridDB.</summary>
public sealed record SgdbGame(long Id, string Name, int Year)
{
    public string Label => Year > 0 ? $"{Name} ({Year})" : Name;
    public override string ToString() => Label;
}

/// <summary>One piece of artwork listed on SteamGridDB (full image plus a small preview).</summary>
public sealed record SgdbImage(long Id, string Url, string Thumb, int Width, int Height, string Style);

/// <summary>
/// Browsing SteamGridDB: find the game, list every cover / background / icon it has, and download the one the user
/// picks. All calls go through <see cref="Net.SafeHttp"/> and return nothing when there is no valid key.
/// </summary>
public sealed partial class ArtworkService
{
    private string? Key()
    {
        var k = _settings().SteamGridDbApiKey?.Trim() ?? "";
        return IsValidKey(k) ? k : null;
    }

    public bool HasSteamGridDbKey => Key() is not null;

    /// <summary>Searches SteamGridDB by name (best first, as SteamGridDB ranks them).</summary>
    public async Task<IReadOnlyList<SgdbGame>> SgdbSearchAsync(string term)
    {
        term = term.Trim();
        if (Key() is not { } key || term.Length == 0) return [];
        try
        {
            using var doc = await SgdbGet($"{SgdbApi}/search/autocomplete/{Uri.EscapeDataString(term)}", key).ConfigureAwait(false);
            return doc is null ? [] : ParseGames(doc.RootElement.GetProperty("data")).Take(20).ToList();
        }
        catch (Exception ex) { Log.Error("steamgriddb search", ex); return []; }
    }

    /// <summary>
    /// The SteamGridDB game for a library item: the one the user chose before, else the one listed for its Steam id,
    /// else the closest search result (with a Steam id, only a confident name match). Null when none is found.
    /// </summary>
    public async Task<SgdbGame?> SgdbMatchAsync(GameEntry e)
    {
        if (Key() is not { } key) return null;
        try
        {
            if (e.SgdbGameId > 0 && await SgdbGameAsync($"games/id/{e.SgdbGameId}", key).ConfigureAwait(false) is { } chosen) return chosen;
            long steamId = e.SteamAppId > 0 ? e.SteamAppId : e.ArtSteamAppId > 0 ? e.ArtSteamAppId : 0;
            if (steamId > 0 && await SgdbGameAsync($"games/steam/{steamId}", key).ConfigureAwait(false) is { } bySteam) return bySteam;
            var found = await SgdbSearchAsync(e.DisplayName).ConfigureAwait(false);
            if (found.Count == 0) return null;
            var id = BestNameMatch(e.DisplayName, found.Select(g => (g.Name, g.Id)));
            if (id == 0 && steamId <= 0) id = found[0].Id;
            return found.FirstOrDefault(g => g.Id == id);
        }
        catch (Exception ex) { Log.Error("steamgriddb match", ex); return null; }
    }

    /// <summary>Every static, safe-for-work image of one kind for a SteamGridDB game (best rated first).</summary>
    public async Task<IReadOnlyList<SgdbImage>> SgdbImagesAsync(long gameId, ArtKind kind, int max = 48)
    {
        if (Key() is not { } key || gameId <= 0) return [];
        try
        {
            using var doc = await SgdbGet(SgdbImagesUrl($"game/{gameId}", kind), key).ConfigureAwait(false);
            return doc is null ? [] : ParseImages(doc.RootElement.GetProperty("data")).Take(max).ToList();
        }
        catch (Exception ex) { Log.Error("steamgriddb images", ex); return []; }
    }

    /// <summary>Downloads a SteamGridDB image (full size or preview). Only SteamGridDB's image host is accepted.</summary>
    public static Task<byte[]?> SgdbDownloadAsync(string url) =>
        IsSgdbImageUrl(url) ? DownloadImageAsync(url) : Task.FromResult<byte[]?>(null);

    internal static bool IsSgdbImageUrl(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps && u.IsDefaultPort &&
        string.IsNullOrEmpty(u.UserInfo) && u.IdnHost.Equals("cdn2.steamgriddb.com", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Finds and downloads SteamGridDB's top image of one kind for a game (used by "Replace all artwork").
    /// Returns null when SteamGridDB has no match or no usable image.
    /// </summary>
    public async Task<byte[]?> SgdbBestImageAsync(SgdbGame game, ArtKind kind)
    {
        await _netGate.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var img in (await SgdbImagesAsync(game.Id, kind, 3).ConfigureAwait(false)))
                if (await SgdbDownloadAsync(img.Url).ConfigureAwait(false) is { } b) return b;
            return null;
        }
        finally { _netGate.Release(); }
    }

    internal static string SgdbImagesUrl(string gameRef, ArtKind kind) => kind switch
    {
        ArtKind.Cover => $"{SgdbApi}/grids/{gameRef}?dimensions=600x900,342x482,660x930&types=static&nsfw=false&humor=false",
        ArtKind.Hero => $"{SgdbApi}/heroes/{gameRef}?types=static&nsfw=false&humor=false",
        _ => $"{SgdbApi}/icons/{gameRef}?types=static&mimes=image/png&nsfw=false&humor=false",
    };

    private static async Task<SgdbGame?> SgdbGameAsync(string path, string key)
    {
        var json = await Net.SafeHttp.GetStringAsync($"{SgdbApi}/{path}", key).ConfigureAwait(false);
        if (json is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.True &&
                root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
                return ParseGame(data);
        }
        catch (JsonException) { }
        return null;
    }

    internal static IEnumerable<SgdbGame> ParseGames(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Array) yield break;
        foreach (var x in data.EnumerateArray())
            if (ParseGame(x) is { } g) yield return g;
    }

    private static SgdbGame? ParseGame(JsonElement x)
    {
        if (x.ValueKind != JsonValueKind.Object || !x.TryGetProperty("id", out var idEl) || !idEl.TryGetInt64(out var id) || id <= 0) return null;
        var name = x.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? Clean(n.GetString()) : "";
        int year = 0;
        if (x.TryGetProperty("release_date", out var rd) && rd.ValueKind == JsonValueKind.Number && rd.TryGetInt64(out var unix) && unix is > 0 and < 32503680000)
            year = DateTimeOffset.FromUnixTimeSeconds(unix).Year;
        return new SgdbGame(id, name.Length > 0 ? name : $"Game {id}", year);
    }

    internal static IEnumerable<SgdbImage> ParseImages(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Array) yield break;
        foreach (var x in data.EnumerateArray())
        {
            if (x.ValueKind != JsonValueKind.Object) continue;
            // Safety net on top of the query: never show anything flagged as NSFW.
            if (x.TryGetProperty("nsfw", out var nsfw) && nsfw.ValueKind == JsonValueKind.True) continue;
            var url = Str(x, "url");
            if (!IsSgdbImageUrl(url)) continue;
            var thumb = Str(x, "thumb");
            if (!IsSgdbImageUrl(thumb)) thumb = url;
            long id = x.TryGetProperty("id", out var i) && i.TryGetInt64(out var v) ? v : 0;
            yield return new SgdbImage(id, url!, thumb!, Int(x, "width"), Int(x, "height"), Clean(Str(x, "style")));
        }

        static string? Str(JsonElement x, string p) => x.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        static int Int(JsonElement x, string p) => x.TryGetProperty(p, out var v) && v.TryGetInt32(out var n) && n is > 0 and <= 100_000 ? n : 0;
    }

    /// <summary>Names from the API are shown in the UI: keep them short and free of control/format characters.</summary>
    private static string Clean(string? s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var chars = s.Where(c => !char.IsControl(c) && char.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.Format).Take(120);
        return string.Concat(chars).Trim();
    }
}
