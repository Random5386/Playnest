using Playnest.Models;
using Playnest.Services.Platform;

namespace Playnest.Services.Scanning;

public sealed class ScanContext(AppSettings settings, IReadOnlyList<UninstallEntry> uninstall, CancellationToken ct)
{
    public AppSettings Settings { get; } = settings;
    public IReadOnlyList<UninstallEntry> Uninstall { get; } = uninstall;
    public CancellationToken Token { get; } = ct;

    /// <summary>Install folders already claimed by a store scanner (keys from <see cref="PathUtil.Key"/>).</summary>
    public HashSet<string> ClaimedDirs { get; } = new();

    /// <summary>Folders the user excluded.</summary>
    public bool IsExcluded(string? dir)
    {
        if (string.IsNullOrEmpty(dir)) return false;
        foreach (var ex in Settings.ExcludedFolders)
            if (PathUtil.IsUnder(dir, ex)) return true;
        return false;
    }

    public bool IsClaimed(string? dir)
    {
        if (string.IsNullOrEmpty(dir)) return false;
        var key = PathUtil.Key(dir);
        if (ClaimedDirs.Contains(key)) return true;
        foreach (var c in ClaimedDirs)
            if (key.StartsWith(c + Path.DirectorySeparatorChar)) return true;
        return false;
    }

    public void Claim(string? dir)
    {
        if (!string.IsNullOrEmpty(dir)) ClaimedDirs.Add(PathUtil.Key(dir));
    }

    private Dictionary<string, UninstallEntry>? _byInstallLocation;

    /// <summary>Uninstall entry whose install folder matches <paramref name="dir"/>.</summary>
    public UninstallEntry? FindUninstallFor(string? dir)
    {
        if (string.IsNullOrEmpty(dir)) return null;
        // Built once per scan instead of normalising every entry's path on every lookup. First entry wins, as before.
        if (_byInstallLocation is null)
        {
            _byInstallLocation = new Dictionary<string, UninstallEntry>();
            foreach (var u in Uninstall)
            {
                var k = PathUtil.Key(u.InstallLocation);
                if (k.Length > 0) _byInstallLocation.TryAdd(k, u);
            }
        }
        return _byInstallLocation.TryGetValue(PathUtil.Key(dir), out var e) ? e : null;
    }
}

public interface IGameScanner
{
    GameSource Source { get; }
    bool IsEnabled(AppSettings s);
    IEnumerable<GameEntry> Scan(ScanContext ctx);
}
