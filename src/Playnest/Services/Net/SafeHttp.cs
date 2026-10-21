using System.Net;
using System.Net.Http.Headers;
using System.Text;

namespace Playnest.Services.Net;

/// <summary>
/// The only way Playnest talks to the internet. Every request goes through these checks:
/// <list type="bullet">
/// <item>HTTPS only, with normal certificate validation (never disabled).</item>
/// <item>Only a fixed list of hosts (Steam, Epic, SteamGridDB, GitHub's API for update checks) — anything else is refused, including redirects to it.</item>
/// <item>Redirects are followed manually (max 3) and each hop is checked again; downgrades to HTTP are refused.</item>
/// <item>Responses are size-capped while reading (also protects against compressed "zip bombs").</item>
/// <item>No cookies are stored or sent, and the SteamGridDB key is only ever sent to steamgriddb.com.</item>
/// <item>Short timeouts (including an overall deadline per download) and limited parallel connections.</item>
/// </list>
/// Callers still validate what they receive (images by their header, JSON by its structure).
/// </summary>
public static class SafeHttp
{
    public const int MaxImageBytes = 15 * 1024 * 1024;
    public const int MaxJsonBytes = 4 * 1024 * 1024;
    private const int MaxRedirects = 3;
    /// <summary>Deadline for a whole download, redirects and body included (tests shorten it).</summary>
    internal static TimeSpan TotalTimeout { get; set; } = TimeSpan.FromSeconds(45);

    /// <summary>Hosts Playnest may contact. Sub-domains are allowed only for the entries starting with a dot.</summary>
    private static readonly string[] AllowedHosts =
    [
        "store.steampowered.com",
        "api.steampowered.com",
        ".steamstatic.com",        // shared.akamai / shared.cloudflare / cdn.cloudflare (Steam's image CDN)
        ".epicgames.com",          // cdn1/cdn2.epicgames.com (Epic store images)
        ".unrealengine.com",       // cdn2.unrealengine.com (older Epic store images)
        "www.steamgriddb.com",
        "cdn2.steamgriddb.com",
        "api.github.com",          // update check: reads the latest release's version only
    ];

    /// <summary>Tests can substitute the network layer (set before the first request).</summary>
    internal static HttpMessageHandler? TestHandler { get; set; }

    private static readonly Lazy<HttpClient> Client = new(() =>
    {
        if (TestHandler is not null) return new HttpClient(TestHandler) { Timeout = TimeSpan.FromSeconds(20) };
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,               // redirects are checked by hand
            UseCookies = false,                      // nothing to track us with
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromSeconds(30),
            MaxConnectionsPerServer = 4,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            MaxResponseHeadersLength = 64,           // KB
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Playnest/1.0 (+game launcher)");
        return client;
    });

    /// <summary>True when <paramref name="uri"/> is HTTPS to one of the allowed hosts on the default port.</summary>
    public static bool IsAllowed(Uri? uri)
    {
        if (uri is null || !uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort) return false;
        if (!string.IsNullOrEmpty(uri.UserInfo)) return false; // "https://user@host" tricks
        var host = uri.IdnHost.ToLowerInvariant();
        foreach (var allowed in AllowedHosts)
        {
            if (allowed.StartsWith('.') ? host.EndsWith(allowed, StringComparison.Ordinal) && host.Length > allowed.Length : host == allowed)
                return true;
        }
        return false;
    }

    public static bool IsAllowed(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var u) && IsAllowed(u);

    /// <summary>Downloads up to <paramref name="maxBytes"/> bytes, or null when anything is off.</summary>
    public static async Task<byte[]?> GetBytesAsync(string? url, int maxBytes, string? bearerToken = null, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        // HttpClient.Timeout stops once the headers arrive (ResponseHeadersRead), so the whole download,
        // body included, gets its own deadline: a server trickling bytes can't hold a download slot forever.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TotalTimeout);
        ct = deadline.Token;
        try
        {
            for (int hop = 0; hop <= MaxRedirects; hop++)
            {
                if (!IsAllowed(uri)) return null;
                using var req = new HttpRequestMessage(HttpMethod.Get, uri);
                // The key is attached only for SteamGridDB's API host, never for image CDNs or redirect targets elsewhere.
                if (bearerToken is not null && uri.Host.Equals("www.steamgriddb.com", StringComparison.OrdinalIgnoreCase))
                    req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);

                using var resp = await Client.Value.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if ((int)resp.StatusCode is >= 300 and < 400)
                {
                    var next = resp.Headers.Location;
                    if (next is null) return null;
                    uri = next.IsAbsoluteUri ? next : new Uri(uri, next);
                    continue; // checked again at the top of the loop
                }
                if (!resp.IsSuccessStatusCode) return null;
                if (resp.Content.Headers.ContentLength is long len && len > maxBytes) return null;

                await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var ms = new MemoryStream();
                var buffer = new byte[81920];
                int read;
                while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    if (ms.Length + read > maxBytes) return null; // counted after decompression
                    ms.Write(buffer, 0, read);
                }
                return ms.ToArray();
            }
            return null; // too many redirects
        }
        catch (Exception)
        {
            // Network errors, timeouts, broken compressed data (InvalidDataException)…: a failed download is never fatal.
            return null;
        }
    }

    public static async Task<string?> GetStringAsync(string url, string? bearerToken = null)
    {
        var b = await GetBytesAsync(url, MaxJsonBytes, bearerToken).ConfigureAwait(false);
        return b is null ? null : Encoding.UTF8.GetString(b);
    }
}
