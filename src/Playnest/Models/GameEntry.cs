namespace Playnest.Models;

/// <summary>What an item in the library is. Plain apps are never added.</summary>
public enum ItemKind
{
    Game,
    Launcher,
    Tool,
}

/// <summary>Where an item was found.</summary>
public enum GameSource
{
    Manual,
    Steam,
    Epic,
    Gog,
    EA,
    Ubisoft,
    BattleNet,
    Xbox,
    Rockstar,
    Riot,
    Amazon,
    Folder,
    Registry,
    System,
}

public static class GameSourceInfo
{
    public static string DisplayName(GameSource s) => s switch
    {
        GameSource.Manual => "Added manually",
        GameSource.Steam => "Steam",
        GameSource.Epic => "Epic Games",
        GameSource.Gog => "GOG",
        GameSource.EA => "EA app",
        GameSource.Ubisoft => "Ubisoft Connect",
        GameSource.BattleNet => "Battle.net",
        GameSource.Xbox => "Xbox",
        GameSource.Rockstar => "Rockstar",
        GameSource.Riot => "Riot",
        GameSource.Amazon => "Amazon Games",
        GameSource.Folder => "Game folders",
        GameSource.Registry => "Installed (other)",
        GameSource.System => "Detected",
        _ => s.ToString(),
    };

    /// <summary>Short label used on tile badges.</summary>
    public static string Badge(GameSource s) => s switch
    {
        GameSource.Manual => "Manual",
        GameSource.Epic => "Epic",
        GameSource.EA => "EA",
        GameSource.Ubisoft => "Ubisoft",
        GameSource.BattleNet => "Battle.net",
        GameSource.Amazon => "Amazon",
        GameSource.Folder => "Local",
        GameSource.Registry => "Local",
        GameSource.System => "Local",
        _ => DisplayName(s),
    };

    /// <summary>The store client that owns games of this source (used on the Launchers page).</summary>
    public static string? LauncherId(GameSource s) => s switch
    {
        GameSource.Steam => "steam",
        GameSource.Epic => "epic",
        GameSource.Gog => "gog",
        GameSource.EA => "ea",
        GameSource.Ubisoft => "ubisoft",
        GameSource.BattleNet => "battlenet",
        GameSource.Xbox => "xbox",
        GameSource.Rockstar => "rockstar",
        GameSource.Riot => "riot",
        GameSource.Amazon => "amazon",
        _ => null,
    };
}

/// <summary>
/// One persisted library item. Fields written by scanners are refreshed on every scan;
/// fields starting with "Custom" (and favourites, playtime, etc.) belong to the user and are never overwritten.
/// </summary>
public sealed class GameEntry
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public ItemKind Kind { get; set; }
    public GameSource Source { get; set; }
    public string? SourceId { get; set; }
    public string? Publisher { get; set; }

    // Detected launch data
    public string? InstallDir { get; set; }
    public string? ExePath { get; set; }
    public string? Arguments { get; set; }
    public string? WorkingDir { get; set; }
    public string? LaunchUri { get; set; }
    public string? UninstallCommand { get; set; }
    public string? UninstallUri { get; set; }
    public long SteamAppId { get; set; }
    /// <summary>Steam's icon hash (appinfo common/clienticon) — used to get the high-res Steam icon.</summary>
    public string? SteamClientIcon { get; set; }

    // User overrides
    public string? CustomName { get; set; }
    public string? CustomExePath { get; set; }
    public string? CustomArguments { get; set; }
    public string? CustomLaunchUri { get; set; }
    public ItemKind? CustomKind { get; set; }
    public string? CustomCover { get; set; }
    public string? CustomIcon { get; set; }
    public string? CustomHero { get; set; }
    public bool RunAsAdmin { get; set; }
    /// <summary>An unusual launch link the user has agreed to open (they are asked again if the link changes).</summary>
    public string? ApprovedLaunchLink { get; set; }
    public bool Favorite { get; set; }
    public bool IsManual { get; set; }
    public bool Missing { get; set; }

    // Stats
    public DateTime AddedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastPlayedUtc { get; set; }
    public long PlaytimeSeconds { get; set; }
    /// <summary>Playtime imported from the store (e.g. Steam). Shown as max(tracked, imported).</summary>
    public long ImportedPlaytimeSeconds { get; set; }

    /// <summary>Install size in bytes (0 = unknown) and when it was last measured.</summary>
    public long SizeBytes { get; set; }
    public DateTime? SizeCheckedUtc { get; set; }

    /// <summary>Steam app id found by name lookup for artwork only (0 = not looked up, -1 = no match).</summary>
    public long ArtSteamAppId { get; set; }

    public string DisplayName => string.IsNullOrWhiteSpace(CustomName) ? Name : CustomName!;
    public ItemKind EffectiveKind => CustomKind ?? Kind;
    public string? EffectiveExe => string.IsNullOrWhiteSpace(CustomExePath) ? ExePath : CustomExePath;
    public string? EffectiveArgs => CustomArguments ?? Arguments;
    public string? EffectiveUri => string.IsNullOrWhiteSpace(CustomLaunchUri) ? LaunchUri : CustomLaunchUri;
    public long TotalPlaytimeSeconds => Math.Max(PlaytimeSeconds, ImportedPlaytimeSeconds);

    /// <summary>Folder used for "Open folder", playtime tracking and icon lookup.</summary>
    public string? EffectiveDir
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(InstallDir)) return InstallDir;
            var exe = EffectiveExe;
            return string.IsNullOrWhiteSpace(exe) ? null : Path.GetDirectoryName(exe);
        }
    }

    /// <summary>Copies scanner-owned fields from a fresh detection, keeping user data.</summary>
    public void UpdateFromScan(GameEntry d)
    {
        Name = d.Name;
        Kind = d.Kind;
        Source = d.Source;
        SourceId = d.SourceId;
        Publisher = d.Publisher ?? Publisher;
        InstallDir = d.InstallDir;
        ExePath = d.ExePath ?? ExePath;
        Arguments = d.Arguments;
        WorkingDir = d.WorkingDir;
        LaunchUri = d.LaunchUri;
        UninstallCommand = d.UninstallCommand;
        UninstallUri = d.UninstallUri;
        if (d.SteamAppId != 0) SteamAppId = d.SteamAppId;
        if (d.SteamClientIcon is not null) SteamClientIcon = d.SteamClientIcon;
        if (d.SizeBytes > 0) { SizeBytes = d.SizeBytes; SizeCheckedUtc = DateTime.UtcNow; }
        if (d.ImportedPlaytimeSeconds > ImportedPlaytimeSeconds) ImportedPlaytimeSeconds = d.ImportedPlaytimeSeconds;
        if (d.LastPlayedUtc is { } lp && (LastPlayedUtc is null || lp > LastPlayedUtc)) LastPlayedUtc = lp;
        Missing = false;
    }
}

public sealed class IgnoredItem
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? InstallDir { get; set; }
}

public sealed class LibraryData
{
    public int Version { get; set; } = 1;
    public List<GameEntry> Items { get; set; } = new();
    public List<IgnoredItem> Ignored { get; set; } = new();
}
