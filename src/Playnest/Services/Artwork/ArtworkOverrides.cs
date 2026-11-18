using Playnest.Models;

namespace Playnest.Services.Artwork;

/// <summary>
/// Artwork chosen from SteamGridDB is stored like a custom image (it wins over found artwork and survives clearing the
/// cache). The image it replaced is remembered on the game, so it can always be put back.
/// </summary>
public static class ArtworkOverrides
{
    public static readonly ArtKind[] Kinds = [ArtKind.Cover, ArtKind.Hero, ArtKind.Icon];

    private const string Marker = "-sgdb-";

    public static string? Current(GameEntry e, ArtKind kind) => kind switch
    {
        ArtKind.Cover => e.CustomCover,
        ArtKind.Icon => e.CustomIcon,
        _ => e.CustomHero,
    };

    private static void SetCurrent(GameEntry e, ArtKind kind, string? value)
    {
        switch (kind)
        {
            case ArtKind.Cover: e.CustomCover = value; break;
            case ArtKind.Icon: e.CustomIcon = value; break;
            default: e.CustomHero = value; break;
        }
    }

    private static string? Before(GameEntry e, ArtKind kind) => kind switch
    {
        ArtKind.Cover => e.CoverBeforeSgdb,
        ArtKind.Icon => e.IconBeforeSgdb,
        _ => e.HeroBeforeSgdb,
    };

    private static void SetBefore(GameEntry e, ArtKind kind, string? value)
    {
        switch (kind)
        {
            case ArtKind.Cover: e.CoverBeforeSgdb = value; break;
            case ArtKind.Icon: e.IconBeforeSgdb = value; break;
            default: e.HeroBeforeSgdb = value; break;
        }
    }

    /// <summary>True when the image is one Playnest saved from SteamGridDB.</summary>
    public static bool IsSgdbFile(string? path) =>
        !string.IsNullOrWhiteSpace(path) && Path.GetFileName(path).Contains(Marker, StringComparison.Ordinal) &&
        string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), Path.GetFullPath(AppPaths.CustomArtDir), StringComparison.OrdinalIgnoreCase);

    /// <summary>True when this kind of image currently comes from SteamGridDB.</summary>
    public static bool IsFromSgdb(GameEntry e, ArtKind kind) => IsSgdbFile(Current(e, kind));

    /// <summary>True when the image was replaced by SteamGridDB art and the earlier one can be put back.</summary>
    public static bool CanRestore(GameEntry e, ArtKind kind) => Before(e, kind) is not null && IsFromSgdb(e, kind);

    public static bool CanRestoreAny(GameEntry e) => Kinds.Any(k => CanRestore(e, k));

    /// <summary>Saves SteamGridDB image data as the game's artwork of this kind. Returns the new file, or null on failure.</summary>
    public static string? Apply(GameEntry e, ArtKind kind, byte[] image)
    {
        if (!ArtworkService.IsImage(image)) return null;
        try
        {
            Directory.CreateDirectory(AppPaths.CustomArtDir);
            var safe = string.Concat(e.Id.Select(c => char.IsLetterOrDigit(c) ? c : '_'));
            var name = kind switch { ArtKind.Cover => "cover", ArtKind.Icon => "icon", _ => "hero" };
            var ext = image[0] == 0xFF ? ".jpg" : image[0] == 0x89 ? ".png" : ".webp";
            var dest = Path.Combine(AppPaths.CustomArtDir, $"{safe}-{name}{Marker}{DateTime.UtcNow.Ticks}{ext}");
            File.WriteAllBytes(dest, image);

            var current = Current(e, kind);
            if (IsSgdbFile(current))
            {
                // Swapping one SteamGridDB pick for another: the image from before SteamGridDB stays remembered.
                if (Before(e, kind) is null) SetBefore(e, kind, "");
                TryDelete(current!);
            }
            else
            {
                SetBefore(e, kind, current ?? "");
            }
            SetCurrent(e, kind, dest);
            return dest;
        }
        catch (Exception ex)
        {
            Log.Error("save steamgriddb art", ex);
            return null;
        }
    }

    /// <summary>Puts back the image that SteamGridDB art replaced. Returns true when something changed.</summary>
    public static bool Restore(GameEntry e, ArtKind kind)
    {
        var before = Before(e, kind);
        if (before is null) return false;
        SetBefore(e, kind, null);
        var current = Current(e, kind);
        // The user chose another image themselves since then: keep theirs.
        if (!IsSgdbFile(current)) return false;
        SetCurrent(e, kind, before.Length == 0 || !File.Exists(before) ? null : before);
        TryDelete(current!);
        return true;
    }

    /// <summary>Restores every kind for every game. Returns how many games changed.</summary>
    public static int RestoreAll(IEnumerable<GameEntry> entries)
    {
        int n = 0;
        foreach (var e in entries)
        {
            bool changed = false;
            foreach (var k in Kinds) changed |= Restore(e, k);
            if (changed) n++;
        }
        return n;
    }

    private static void TryDelete(string p)
    {
        try { if (IsSgdbFile(p) && File.Exists(p)) File.Delete(p); } catch { }
    }
}
