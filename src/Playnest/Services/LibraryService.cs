using Playnest.Models;
using Playnest.Services.Platform;
using Playnest.Services.Scanning;

namespace Playnest.Services;

public sealed record MergeSummary(int Added, int Updated, int Removed);

/// <summary>Owns the library file: loading, merging scan results, ignore list and saving.</summary>
public sealed class LibraryService
{
    private readonly object _gate = new();
    private Timer? _saveTimer;

    public LibraryData Data { get; private set; } = new();

    public void Load() => Data = Storage.LoadLibrary();

    public void SaveSoon()
    {
        lock (_gate)
        {
            _saveTimer?.Dispose();
            _saveTimer = new Timer(_ => SaveNow(), null, 1500, Timeout.Infinite);
        }
    }

    public void SaveNow()
    {
        LibraryData snapshot;
        lock (_gate)
        {
            _saveTimer?.Dispose();
            _saveTimer = null;
            snapshot = new LibraryData { Version = Data.Version, Items = Data.Items.ToList(), Ignored = Data.Ignored.ToList() };
        }
        Storage.SaveLibrary(snapshot);
    }

    public bool IsIgnored(GameEntry e) =>
        Data.Ignored.Any(i => i.Id.Equals(e.Id, StringComparison.OrdinalIgnoreCase)
                              || (i.InstallDir is not null && e.InstallDir is not null && PathUtil.Key(i.InstallDir) == PathUtil.Key(e.InstallDir)));

    /// <summary>
    /// Merges a scan into the library. New items are added, known items refreshed (user edits kept),
    /// and auto-detected items that disappeared from a completed source are removed.
    /// </summary>
    public MergeSummary Merge(ScanResult scan, AppSettings settings)
    {
        lock (_gate)
        {
            int added = 0, updated = 0, removed = 0;
            var byId = Data.Items.ToDictionary(i => i.Id, StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var d in scan.Items)
            {
                if (IsIgnored(d)) continue;
                seen.Add(d.Id);
                if (byId.TryGetValue(d.Id, out var existing))
                {
                    existing.UpdateFromScan(d);
                    updated++;
                }
                else
                {
                    // A manual entry pointing into the same folder wins; don't add a duplicate.
                    if (d.InstallDir is not null && Data.Items.Any(x => x.IsManual && x.EffectiveDir is { } md && PathUtil.Key(md) == PathUtil.Key(d.InstallDir)))
                        continue;
                    d.AddedUtc = DateTime.UtcNow;
                    if (d.SizeBytes > 0) d.SizeCheckedUtc = DateTime.UtcNow;
                    Data.Items.Add(d);
                    byId[d.Id] = d;
                    added++;
                }
            }

            var disabled = DisabledSources(settings);
            removed = Data.Items.RemoveAll(i =>
                !i.IsManual && !seen.Contains(i.Id) &&
                (scan.CompletedSources.Contains(i.Source) || disabled.Contains(i.Source) ||
                 (i.Source == GameSource.System && i.Kind == ItemKind.Launcher && !settings.DetectLaunchers) ||
                 (i.Source == GameSource.System && i.Kind == ItemKind.Tool && !settings.DetectTools)));

            foreach (var m in Data.Items.Where(i => i.IsManual))
                m.Missing = !ManualTargetExists(m);

            SaveSoon();
            return new MergeSummary(added, updated, removed);
        }
    }

    private static HashSet<GameSource> DisabledSources(AppSettings s)
    {
        var set = new HashSet<GameSource>();
        if (!s.ScanSteam) set.Add(GameSource.Steam);
        if (!s.ScanEpic) set.Add(GameSource.Epic);
        if (!s.ScanGog) set.Add(GameSource.Gog);
        if (!s.ScanEA) set.Add(GameSource.EA);
        if (!s.ScanUbisoft) set.Add(GameSource.Ubisoft);
        if (!s.ScanBattleNet) set.Add(GameSource.BattleNet);
        if (!s.ScanXbox) set.Add(GameSource.Xbox);
        if (!s.ScanRockstar) set.Add(GameSource.Rockstar);
        if (!s.ScanRiot) set.Add(GameSource.Riot);
        if (!s.ScanAmazon) set.Add(GameSource.Amazon);
        if (!s.ScanInstalledPrograms) set.Add(GameSource.Registry);
        return set;
    }

    private static bool ManualTargetExists(GameEntry e)
    {
        if (!string.IsNullOrWhiteSpace(e.EffectiveUri)) return true;
        var exe = e.EffectiveExe;
        return exe is not null && File.Exists(exe);
    }

    /// <summary>Removes an item and remembers it so future scans don't bring it back.</summary>
    public void Ignore(GameEntry e)
    {
        lock (_gate)
        {
            Data.Items.Remove(e);
            if (!e.IsManual && Data.Ignored.All(i => !i.Id.Equals(e.Id, StringComparison.OrdinalIgnoreCase)))
                Data.Ignored.Add(new IgnoredItem { Id = e.Id, Name = e.DisplayName, InstallDir = e.InstallDir });
        }
        SaveSoon();
    }

    public void Unignore(IgnoredItem i)
    {
        lock (_gate) Data.Ignored.Remove(i);
        SaveSoon();
    }

    public void Remove(GameEntry e)
    {
        lock (_gate) Data.Items.Remove(e);
        SaveSoon();
    }

    /// <summary>A copy of the item list that background work can enumerate safely.</summary>
    public List<GameEntry> Snapshot()
    {
        lock (_gate) return Data.Items.ToList();
    }

    public void Add(GameEntry e)
    {
        lock (_gate) Data.Items.Add(e);
        SaveSoon();
    }

    /// <summary>Quick check (no scanning) that auto-detected items still exist on disk — used after an uninstall.</summary>
    public List<GameEntry> PruneMissing()
    {
        lock (_gate)
        {
            var gone = Data.Items.Where(i => !i.IsManual && i.InstallDir is not null && !Directory.Exists(i.InstallDir)).ToList();
            foreach (var g in gone) Data.Items.Remove(g);
            if (gone.Count > 0) SaveSoon();
            return gone;
        }
    }

    /// <summary>Creates a manual entry from an exe, shortcut (.lnk), internet shortcut (.url) or batch file.</summary>
    public static GameEntry CreateManual(string path, ItemKind kind)
    {
        var e = new GameEntry
        {
            Id = "manual:" + Guid.NewGuid().ToString("N")[..12],
            IsManual = true,
            Source = GameSource.Manual,
            Kind = kind,
            Name = PathUtil.CleanTitle(Path.GetFileNameWithoutExtension(path)),
        };
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".lnk" && ShellLink.TryRead(path) is { } lnk)
        {
            e.ExePath = lnk.Target;
            e.Arguments = string.IsNullOrWhiteSpace(lnk.Arguments) ? null : lnk.Arguments;
            e.WorkingDir = lnk.WorkingDir;
        }
        else if (ext == ".url" && InternetShortcut.TryRead(path) is { } url)
        {
            e.LaunchUri = url.Url;
            e.ExePath = url.IconFile is { } ic && ic.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(ic) ? ic : null;
            if (url.Url.StartsWith("steam://rungameid/", StringComparison.OrdinalIgnoreCase) &&
                long.TryParse(url.Url["steam://rungameid/".Length..].TrimEnd('/'), out var sid))
                e.SteamAppId = sid;
        }
        else
        {
            e.ExePath = path;
        }

        if (e.ExePath is not null)
        {
            var dir = Path.GetDirectoryName(e.ExePath);
            // For "...\Game\Binaries\Win64\Game.exe" use the game's root folder.
            e.InstallDir = GuessRoot(dir);
            if (ext == ".exe")
            {
                var folderName = Path.GetFileName(e.InstallDir ?? "");
                if (!string.IsNullOrEmpty(folderName) && folderName.Length > 2) e.Name = PathUtil.CleanTitle(folderName);
            }
        }
        return e;
    }

    private static string? GuessRoot(string? dir)
    {
        if (dir is null) return null;
        var d = new DirectoryInfo(dir);
        for (int i = 0; i < 3 && d.Parent is not null; i++)
        {
            var n = d.Name.ToLowerInvariant();
            if (n is "win64" or "win32" or "x64" or "x86" or "bin" or "binaries" or "bin64" or "bin32" or "retail" or "shipping")
                d = d.Parent;
            else break;
        }
        return d.FullName;
    }
}
