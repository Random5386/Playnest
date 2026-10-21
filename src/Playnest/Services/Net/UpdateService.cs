using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Playnest.Services.Net;

public enum UpdateStatus { UpToDate, Available, Failed }

public sealed record UpdateInfo(Version Version, string Title, string Notes, string PageUrl);

public sealed record UpdateResult(UpdateStatus Status, UpdateInfo? Info = null);

/// <summary>
/// Asks GitHub whether a newer Playnest release has been published.
/// It only reads release information; nothing is downloaded or run. The user opens the release page
/// in their browser and downloads it themselves.
/// <list type="bullet">
/// <item>One request to api.github.com through <see cref="SafeHttp"/> (HTTPS, host allowlist, size cap).</item>
/// <item>The version comes from the release tag and must look like v1.2.3.</item>
/// <item>The page that gets opened is built here from the fixed repository name and that checked
///       version. A link from the response is never used, so a tampered response can't send the user anywhere else.</item>
/// <item>Release notes are shown as plain text only, and shortened.</item>
/// </list>
/// </summary>
public static partial class UpdateService
{
    /// <summary>The GitHub repository ("owner/name") where Playnest releases are published.</summary>
    public const string Repository = "Random5386/Playnest";

    private const int MaxNotesLength = 1500;

    [GeneratedRegex(@"^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})/[A-Za-z0-9._-]{1,100}$")]
    private static partial Regex RepoPattern();

    [GeneratedRegex(@"^[vV]?(\d{1,5})\.(\d{1,5})(?:\.(\d{1,5}))?(?:\.(\d{1,5}))?$")]
    private static partial Regex TagPattern();

    public static Version CurrentVersion => Normalize(typeof(UpdateService).Assembly.GetName().Version ?? new Version(0, 0, 0));

    public static string LatestReleaseApiUrl => $"https://api.github.com/repos/{Repository}/releases/latest";

    public static async Task<UpdateResult> CheckAsync(Version current, CancellationToken ct = default)
    {
        if (!RepoPattern().IsMatch(Repository)) return new UpdateResult(UpdateStatus.Failed);
        var bytes = await SafeHttp.GetBytesAsync(LatestReleaseApiUrl, 512 * 1024, ct: ct).ConfigureAwait(false);
        if (bytes is null) return new UpdateResult(UpdateStatus.Failed);
        return Evaluate(Encoding.UTF8.GetString(bytes), current);
    }

    /// <summary>Reads GitHub's "latest release" JSON. Anything unexpected counts as a failed check.</summary>
    internal static UpdateResult Evaluate(string json, Version current)
    {
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return new UpdateResult(UpdateStatus.Failed);
            if (Bool(root, "draft") || Bool(root, "prerelease")) return new UpdateResult(UpdateStatus.UpToDate);

            var tag = Str(root, "tag_name");
            var version = ParseTag(tag);
            if (version is null) return new UpdateResult(UpdateStatus.Failed);
            if (version <= Normalize(current)) return new UpdateResult(UpdateStatus.UpToDate);

            var title = PlainText(Str(root, "name"), 100);
            if (string.IsNullOrWhiteSpace(title)) title = "Playnest " + Display(version);
            var notes = TidyMarkdown(PlainText(Str(root, "body"), MaxNotesLength));
            return new UpdateResult(UpdateStatus.Available, new UpdateInfo(version, title, notes, PageForTag(tag!)));
        }
        catch (JsonException)
        {
            return new UpdateResult(UpdateStatus.Failed);
        }
    }

    /// <summary>"v1.2.3" / "1.2" → version, or null when the tag isn't a plain version number.</summary>
    public static Version? ParseTag(string? tag)
    {
        if (string.IsNullOrEmpty(tag) || tag.Length > 30) return null;
        var m = TagPattern().Match(tag);
        if (!m.Success) return null;
        int Part(int i) => m.Groups[i].Success ? int.Parse(m.Groups[i].Value) : 0;
        return new Version(Part(1), Part(2), Part(3), Part(4));
    }

    /// <summary>The release page, built only from trusted parts (the fixed repository and a parsed version).</summary>
    public static string PageFor(Version v) => $"https://github.com/{Repository}/releases/tag/v{Display(v)}";

    /// <summary>
    /// The page of the release whose tag is <paramref name="tag"/>, exactly as tagged ("v1.2.0" or "1.2.0").
    /// Only called with a tag that <see cref="ParseTag"/> accepted, so it can only contain a "v", digits and dots.
    /// </summary>
    public static string PageForTag(string tag) =>
        ParseTag(tag) is not null ? $"https://github.com/{Repository}/releases/tag/{tag}" : $"https://github.com/{Repository}/releases/latest";

    public static string Display(Version v) => v.Revision > 0 ? v.ToString(4) : v.ToString(3);

    private static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));

    private static string? Str(JsonElement o, string name) =>
        o.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static bool Bool(JsonElement o, string name) =>
        o.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.True;

    /// <summary>Release notes are written in Markdown; drop heading marks and bold markers so they read as plain text.</summary>
    internal static string TidyMarkdown(string s)
    {
        var lines = s.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            var l = lines[i].TrimStart();
            if (l.StartsWith('#')) lines[i] = l.TrimStart('#').TrimStart();
            lines[i] = lines[i].Replace("**", "").Replace("__", "");
        }
        return string.Join('\n', lines).Trim();
    }

    /// <summary>Keeps printable text and line breaks only, shortened to <paramref name="max"/> characters.</summary>
    internal static string PlainText(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(Math.Min(s.Length, max + 1));
        foreach (var c in s)
        {
            if (c == '\r') continue;
            if (char.IsControl(c) && c != '\n') continue;
            if (char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format) continue; // e.g. right-to-left override
            sb.Append(c);
            if (sb.Length >= max) { sb.Append('…'); break; }
        }
        return sb.ToString().Trim();
    }
}
