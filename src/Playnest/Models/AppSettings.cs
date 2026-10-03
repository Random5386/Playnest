namespace Playnest.Models;

public enum ThemeMode { System, Dark, Light }
public enum BackdropMode { Mica, Acrylic, Solid, Image }
public enum LibraryViewMode { Covers, Icons, List }
public enum SortMode { Name, LastPlayed, Playtime, RecentlyAdded, Source, Size }
public enum LaunchBehavior { Nothing, Minimize, MinimizeToTray, Close }
public enum ImageQuality { Low, Balanced, High }
public enum DoubleClickAction { Play, ShowDetails }
public enum QuickPlayMode { OnHover, Always, Off }

/// <summary>All user preferences. Every field has a sensible default so old settings files keep working.</summary>
public sealed class AppSettings
{
    /// <summary>Bumped when a default changes so existing users get the new default once.</summary>
    public int SettingsVersion { get; set; }

    // ---------- Appearance ----------
    public ThemeMode Theme { get; set; } = ThemeMode.Dark;
    public string AccentColor { get; set; } = "#7C5CFF";
    public BackdropMode Backdrop { get; set; } = BackdropMode.Mica;
    public string? BackgroundImage { get; set; }
    public double BackgroundImageOpacity { get; set; } = 0.35;
    public double CornerRadius { get; set; } = 8;
    public double FontSize { get; set; } = 14;
    public string FontFamily { get; set; } = "";
    public bool CompactSidebar { get; set; }
    public bool ShowSidebarCounts { get; set; } = true;

    // ---------- Library ----------
    public LibraryViewMode ViewMode { get; set; } = LibraryViewMode.Covers;
    public double CoverWidth { get; set; } = 160;
    public double IconSize { get; set; } = 96;
    public double TileSpacing { get; set; } = 14;
    public bool ShowTitles { get; set; } = true;
    public bool ShowSourceBadges { get; set; } = true;
    public bool ShowPlaytimeOnTiles { get; set; }
    public bool ShowSizeOnTiles { get; set; } = true;
    /// <summary>Measure install sizes in the background (Steam/Epic sizes are read from their records instantly).</summary>
    public bool CalculateSizes { get; set; } = true;
    public bool ShowDetailsPanel { get; set; } = true;
    public bool ShowRecentRow { get; set; } = true;
    public SortMode Sort { get; set; } = SortMode.Name;
    public bool SortDescending { get; set; }
    public bool FavoritesFirst { get; set; } = true;
    public bool HoverEffects { get; set; } = true;
    public DoubleClickAction DoubleClick { get; set; } = DoubleClickAction.Play;
    /// <summary>Small play button in the corner of each tile.</summary>
    public QuickPlayMode QuickPlay { get; set; } = QuickPlayMode.OnHover;

    // ---------- Behaviour ----------
    public LaunchBehavior OnGameLaunch { get; set; } = LaunchBehavior.MinimizeToTray;
    public bool RestoreAfterGameExit { get; set; } = true;
    public bool CloseToTray { get; set; } = true;
    public bool StartWithWindows { get; set; }
    public bool StartMinimized { get; set; }
    public bool TrackPlaytime { get; set; } = true;
    public bool ImportSteamPlaytime { get; set; } = true;
    public bool FreeMemoryWhileGaming { get; set; } = true;
    /// <summary>Also count playtime for games started outside Playnest (Steam, desktop shortcuts…).</summary>
    public bool TrackExternalPlaytime { get; set; } = true;

    // ---------- Scanning ----------
    public bool ScanOnStartup { get; set; } = true;
    public bool ScanSteam { get; set; } = true;
    public bool ScanEpic { get; set; } = true;
    public bool ScanGog { get; set; } = true;
    public bool ScanEA { get; set; } = true;
    public bool ScanUbisoft { get; set; } = true;
    public bool ScanBattleNet { get; set; } = true;
    public bool ScanXbox { get; set; } = true;
    public bool ScanRockstar { get; set; } = true;
    public bool ScanRiot { get; set; } = true;
    public bool ScanAmazon { get; set; } = true;
    /// <summary>Use Windows' installed-programs list and keep only entries that look like games.</summary>
    public bool ScanInstalledPrograms { get; set; } = true;
    public bool AutoDetectGameFolders { get; set; } = true;
    public List<string> GameFolders { get; set; } = new();
    public List<string> ExcludedFolders { get; set; } = new();
    public bool DetectLaunchers { get; set; } = true;
    public bool DetectTools { get; set; } = true;
    /// <summary>Steam "Software" titles (e.g. Lossless Scaling) are added as tools instead of being skipped.</summary>
    public bool IncludeSteamSoftwareAsTools { get; set; }
    /// <summary>Extra words that mark an install as not-a-game (one per entry, case-insensitive).</summary>
    public List<string> ExtraExcludeKeywords { get; set; } = new();

    // ---------- Artwork ----------
    public bool DownloadArtwork { get; set; } = true;
    public string SteamGridDbApiKey { get; set; } = "";
    public ImageQuality ImageQuality { get; set; } = ImageQuality.Balanced;

    // ---------- Performance ----------
    public bool LowMemoryMode { get; set; }
    public bool Animations { get; set; } = true;
    public bool GpuRendering { get; set; } = true;

    // ---------- Window ----------
    public double WindowWidth { get; set; } = 1280;
    public double WindowHeight { get; set; } = 780;
    public bool WindowMaximized { get; set; }
    public string LastFilter { get; set; } = "all";
}
