using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Playnest.Models;
using Playnest.Services;
using Playnest.Services.Artwork;
using Playnest.Services.Net;
using Playnest.Services.Platform;

namespace Playnest.ViewModels;

/// <summary>Settings page. Every change applies immediately and is saved automatically.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private AppSettings S => _main.Settings;

    public SettingsViewModel(MainViewModel main)
    {
        _main = main;
        foreach (var hex in ThemeService.AccentPresets) Swatches.Add(new AccentSwatch(hex, h => AccentColor = h));
        foreach (var f in S.GameFolders) GameFolders.Add(new FolderItemViewModel(f, RemoveGameFolder));
        foreach (var f in S.ExcludedFolders) ExcludedFolders.Add(new FolderItemViewModel(f, RemoveExcludedFolder));
        Fonts = BuildFontList();
    }

    public ObservableCollection<AccentSwatch> Swatches { get; } = new();
    public ObservableCollection<FolderItemViewModel> GameFolders { get; } = new();
    public ObservableCollection<FolderItemViewModel> ExcludedFolders { get; } = new();
    public ObservableCollection<IgnoredItemViewModel> Ignored { get; } = new();
    public IReadOnlyList<string> Fonts { get; }

    public string[] ThemeOptions { get; } = ["Follow Windows", "Dark", "Light"];
    public string[] BackdropOptions { get; } = ["Mica (Windows 11)", "Acrylic (blur)", "Solid colour", "Custom image"];
    public string[] ViewModeOptions { get; } = ["Cover grid", "Icon grid", "List"];
    public string[] SortOptions { get; } = ["Name", "Last played", "Playtime", "Recently added", "Store", "Size"];
    public string[] LaunchOptions { get; } = ["Do nothing", "Minimize Playnest", "Hide Playnest to the tray", "Close Playnest"];
    public string[] QualityOptions { get; } = ["Low (least memory)", "Balanced", "High (sharpest)"];
    public string[] DoubleClickOptions { get; } = ["Play the game", "Show details"];
    public string[] QuickPlayOptions { get; } = ["When the pointer is over a game", "Always", "Never"];

    // ------------------------------------------------------------------ helpers

    private bool Set<T>(T current, T value, Action<T> assign, bool layout = false, bool theme = false, bool window = false,
        [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(current, value)) return false;
        assign(value);
        OnPropertyChanged(name);
        if (theme) ThemeService.Apply(S);
        if (window) _main.Ui.ApplyWindowSettings();
        if (layout) _main.OnLayoutSettingsChanged();
        _main.SaveSettingsSoon();
        return true;
    }

    private static IReadOnlyList<string> BuildFontList()
    {
        var list = new List<string> { "(Default — Segoe UI Variable)" };
        try
        {
            list.AddRange(FontManager.Current.SystemFonts.Select(f => f.Name).Distinct().OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
        }
        catch { }
        return list;
    }

    // ------------------------------------------------------------------ Appearance

    public int ThemeIndex { get => (int)S.Theme; set => Set(S.Theme, (ThemeMode)value, v => S.Theme = v, theme: true); }

    public string AccentColor
    {
        get => S.AccentColor;
        set
        {
            if (!Color.TryParse(value?.Trim() ?? "", out _)) return;
            if (Set(S.AccentColor, value!.Trim(), v => S.AccentColor = v, theme: true)) OnPropertyChanged(nameof(AccentPreview));
        }
    }

    public IBrush AccentPreview => new SolidColorBrush(ThemeService.ParseColor(S.AccentColor, Colors.MediumPurple));

    public int BackdropIndex
    {
        get => (int)S.Backdrop;
        set { if (Set(S.Backdrop, (BackdropMode)value, v => S.Backdrop = v, window: true)) OnPropertyChanged(nameof(IsImageBackdrop)); }
    }

    public bool IsImageBackdrop => S.Backdrop == BackdropMode.Image;

    public string BackgroundImage
    {
        get => S.BackgroundImage ?? "";
        set => Set(S.BackgroundImage ?? "", value ?? "", v => S.BackgroundImage = v.Length == 0 ? null : v, window: true);
    }

    public double BackgroundImageOpacity
    {
        get => S.BackgroundImageOpacity;
        set => Set(S.BackgroundImageOpacity, Math.Round(value, 2), v => S.BackgroundImageOpacity = v, window: true);
    }

    public double CornerRadius { get => S.CornerRadius; set => Set(S.CornerRadius, Math.Round(value), v => S.CornerRadius = v, theme: true); }
    public double FontSize { get => S.FontSize; set => Set(S.FontSize, Math.Round(value), v => S.FontSize = v, theme: true, layout: true); }

    public int FontIndex
    {
        get
        {
            if (string.IsNullOrWhiteSpace(S.FontFamily)) return 0;
            var i = Fonts.ToList().FindIndex(f => f.Equals(S.FontFamily, StringComparison.OrdinalIgnoreCase));
            return i < 0 ? 0 : i;
        }
        set
        {
            if (value < 0 || value >= Fonts.Count) return;
            Set(S.FontFamily, value == 0 ? "" : Fonts[value], v => S.FontFamily = v, theme: true);
        }
    }

    public bool CompactSidebar { get => S.CompactSidebar; set => Set(S.CompactSidebar, value, v => S.CompactSidebar = v, layout: true); }
    public bool ShowSidebarCounts { get => S.ShowSidebarCounts; set => Set(S.ShowSidebarCounts, value, v => S.ShowSidebarCounts = v, layout: true); }

    [RelayCommand]
    private async Task BrowseBackground()
    {
        var p = await _main.Ui.PickFile("Choose a background image", "Images", ["*.png", "*.jpg", "*.jpeg", "*.webp"]);
        if (p is null) return;
        BackgroundImage = p;
        BackdropIndex = (int)BackdropMode.Image;
    }

    // ------------------------------------------------------------------ Library

    public int ViewModeIndex { get => (int)S.ViewMode; set => Set(S.ViewMode, (LibraryViewMode)value, v => S.ViewMode = v, layout: true); }
    public double CoverWidth { get => S.CoverWidth; set => Set(S.CoverWidth, Math.Round(value), v => S.CoverWidth = v, layout: true); }
    public double IconSize { get => S.IconSize; set => Set(S.IconSize, Math.Round(value), v => S.IconSize = v, layout: true); }
    public double TileSpacing { get => S.TileSpacing; set => Set(S.TileSpacing, Math.Round(value), v => S.TileSpacing = v, layout: true); }
    public bool ShowTitles { get => S.ShowTitles; set => Set(S.ShowTitles, value, v => S.ShowTitles = v, layout: true); }
    public bool ShowSourceBadges { get => S.ShowSourceBadges; set => Set(S.ShowSourceBadges, value, v => S.ShowSourceBadges = v, layout: true); }
    public bool ShowPlaytimeOnTiles { get => S.ShowPlaytimeOnTiles; set => Set(S.ShowPlaytimeOnTiles, value, v => S.ShowPlaytimeOnTiles = v, layout: true); }
    public bool ShowSizeOnTiles { get => S.ShowSizeOnTiles; set => Set(S.ShowSizeOnTiles, value, v => S.ShowSizeOnTiles = v, layout: true); }

    public bool CalculateSizes
    {
        get => S.CalculateSizes;
        set { if (Set(S.CalculateSizes, value, v => S.CalculateSizes = v) && value) _main.StartSizeCalculation(); }
    }
    public bool ShowDetailsPanel { get => S.ShowDetailsPanel; set => Set(S.ShowDetailsPanel, value, v => S.ShowDetailsPanel = v, layout: true); }
    public bool ShowRecentRow { get => S.ShowRecentRow; set => Set(S.ShowRecentRow, value, v => S.ShowRecentRow = v, layout: true); }
    public bool FavoritesFirst { get => S.FavoritesFirst; set => Set(S.FavoritesFirst, value, v => S.FavoritesFirst = v, layout: true); }
    public bool HoverEffects { get => S.HoverEffects; set => Set(S.HoverEffects, value, v => S.HoverEffects = v, layout: true); }
    public int SortIndex { get => (int)S.Sort; set => Set(S.Sort, (SortMode)value, v => S.Sort = v, layout: true); }
    public int QuickPlayIndex { get => (int)S.QuickPlay; set => Set(S.QuickPlay, (QuickPlayMode)value, v => S.QuickPlay = v, layout: true); }
    public int DoubleClickIndex { get => (int)S.DoubleClick; set => Set(S.DoubleClick, (DoubleClickAction)value, v => S.DoubleClick = v); }

    // ------------------------------------------------------------------ Behaviour

    public int LaunchBehaviorIndex { get => (int)S.OnGameLaunch; set => Set(S.OnGameLaunch, (LaunchBehavior)value, v => S.OnGameLaunch = v); }
    public bool RestoreAfterGameExit { get => S.RestoreAfterGameExit; set => Set(S.RestoreAfterGameExit, value, v => S.RestoreAfterGameExit = v); }
    public bool CloseToTray { get => S.CloseToTray; set => Set(S.CloseToTray, value, v => S.CloseToTray = v); }
    public bool StartMinimized { get => S.StartMinimized; set => Set(S.StartMinimized, value, v => S.StartMinimized = v); }
    public bool TrackPlaytime { get => S.TrackPlaytime; set { if (Set(S.TrackPlaytime, value, v => S.TrackPlaytime = v)) _main.ApplyPlaytimeSettings(); } }
    public bool TrackExternalPlaytime { get => S.TrackExternalPlaytime; set { if (Set(S.TrackExternalPlaytime, value, v => S.TrackExternalPlaytime = v)) _main.ApplyPlaytimeSettings(); } }
    public bool ImportSteamPlaytime { get => S.ImportSteamPlaytime; set => Set(S.ImportSteamPlaytime, value, v => S.ImportSteamPlaytime = v); }
    public bool FreeMemoryWhileGaming { get => S.FreeMemoryWhileGaming; set => Set(S.FreeMemoryWhileGaming, value, v => S.FreeMemoryWhileGaming = v); }
    public bool ConfirmUnusualLaunchLinks { get => S.ConfirmUnusualLaunchLinks; set => Set(S.ConfirmUnusualLaunchLinks, value, v => S.ConfirmUnusualLaunchLinks = v); }

    public bool StartWithWindows
    {
        get => S.StartWithWindows;
        set
        {
            if (!Set(S.StartWithWindows, value, v => S.StartWithWindows = v)) return;
            ApplyStartup(S);
        }
    }

    /// <summary>
    /// Makes the Windows "Run" entry match the setting. Only writes when something actually differs
    /// (e.g. the option was toggled or the exe moved), so normal start-ups never touch the registry.
    /// </summary>
    public static void ApplyStartup(AppSettings s)
    {
        var exe = Environment.ProcessPath;
        string? wanted = s.StartWithWindows && exe is not null ? $"\"{exe}\" --autostart" : null;
        var current = Reg.GetRunAtStartup("Playnest");
        if (string.Equals(current, wanted, StringComparison.OrdinalIgnoreCase)) return;
        Reg.SetRunAtStartup("Playnest", wanted);
    }

    // ------------------------------------------------------------------ Scanning

    public bool ScanOnStartup { get => S.ScanOnStartup; set => Set(S.ScanOnStartup, value, v => S.ScanOnStartup = v); }
    public bool ScanSteam { get => S.ScanSteam; set => Set(S.ScanSteam, value, v => S.ScanSteam = v); }
    public bool ScanEpic { get => S.ScanEpic; set => Set(S.ScanEpic, value, v => S.ScanEpic = v); }
    public bool ScanGog { get => S.ScanGog; set => Set(S.ScanGog, value, v => S.ScanGog = v); }
    public bool ScanEA { get => S.ScanEA; set => Set(S.ScanEA, value, v => S.ScanEA = v); }
    public bool ScanUbisoft { get => S.ScanUbisoft; set => Set(S.ScanUbisoft, value, v => S.ScanUbisoft = v); }
    public bool ScanBattleNet { get => S.ScanBattleNet; set => Set(S.ScanBattleNet, value, v => S.ScanBattleNet = v); }
    public bool ScanXbox { get => S.ScanXbox; set => Set(S.ScanXbox, value, v => S.ScanXbox = v); }
    public bool ScanRockstar { get => S.ScanRockstar; set => Set(S.ScanRockstar, value, v => S.ScanRockstar = v); }
    public bool ScanRiot { get => S.ScanRiot; set => Set(S.ScanRiot, value, v => S.ScanRiot = v); }
    public bool ScanAmazon { get => S.ScanAmazon; set => Set(S.ScanAmazon, value, v => S.ScanAmazon = v); }
    public bool ScanInstalledPrograms { get => S.ScanInstalledPrograms; set => Set(S.ScanInstalledPrograms, value, v => S.ScanInstalledPrograms = v); }
    public bool AutoDetectGameFolders { get => S.AutoDetectGameFolders; set => Set(S.AutoDetectGameFolders, value, v => S.AutoDetectGameFolders = v); }
    public bool DetectLaunchers { get => S.DetectLaunchers; set => Set(S.DetectLaunchers, value, v => S.DetectLaunchers = v); }
    public bool DetectTools { get => S.DetectTools; set => Set(S.DetectTools, value, v => S.DetectTools = v); }
    public bool IncludeSteamSoftwareAsTools { get => S.IncludeSteamSoftwareAsTools; set => Set(S.IncludeSteamSoftwareAsTools, value, v => S.IncludeSteamSoftwareAsTools = v); }

    public string ExcludeKeywords
    {
        get => string.Join(", ", S.ExtraExcludeKeywords);
        set => Set(ExcludeKeywords, value ?? "", v => S.ExtraExcludeKeywords =
            v.Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList());
    }

    [RelayCommand]
    private async Task AddGameFolder()
    {
        var p = await _main.Ui.PickFolder("Choose a folder that contains games");
        if (p is not null) AddGameFolderPath(p);
    }

    public void AddGameFolderPath(string p)
    {
        if (S.GameFolders.Any(f => PathUtil.Key(f) == PathUtil.Key(p))) return;
        S.GameFolders.Add(p);
        GameFolders.Add(new FolderItemViewModel(p, RemoveGameFolder));
        _main.SaveSettingsSoon();
    }

    private void RemoveGameFolder(FolderItemViewModel f)
    {
        S.GameFolders.RemoveAll(x => PathUtil.Key(x) == PathUtil.Key(f.Path));
        GameFolders.Remove(f);
        _main.SaveSettingsSoon();
    }

    [RelayCommand]
    private async Task AddExcludedFolder()
    {
        var p = await _main.Ui.PickFolder("Choose a folder Playnest should never scan");
        if (p is null || S.ExcludedFolders.Any(f => PathUtil.Key(f) == PathUtil.Key(p))) return;
        S.ExcludedFolders.Add(p);
        ExcludedFolders.Add(new FolderItemViewModel(p, RemoveExcludedFolder));
        _main.SaveSettingsSoon();
    }

    private void RemoveExcludedFolder(FolderItemViewModel f)
    {
        S.ExcludedFolders.RemoveAll(x => PathUtil.Key(x) == PathUtil.Key(f.Path));
        ExcludedFolders.Remove(f);
        _main.SaveSettingsSoon();
    }

    public void RefreshIgnored()
    {
        Ignored.Clear();
        foreach (var i in _main.Library.Data.Ignored.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase))
            Ignored.Add(new IgnoredItemViewModel(i, Restore));
        OnPropertyChanged(nameof(HasIgnored));
    }

    public bool HasIgnored => Ignored.Count > 0;

    private void Restore(IgnoredItemViewModel i)
    {
        _main.Library.Unignore(i.Item);
        Ignored.Remove(i);
        OnPropertyChanged(nameof(HasIgnored));
        _main.ShowToast($"{i.Name} will come back on the next scan.", actionText: "Scan now", action: () => _ = _main.ScanAsync(false));
    }

    [RelayCommand]
    private Task ScanNow() => _main.ScanAsync(quiet: false);

    // ------------------------------------------------------------------ Artwork

    public bool DownloadArtwork { get => S.DownloadArtwork; set => Set(S.DownloadArtwork, value, v => S.DownloadArtwork = v); }

    public string SteamGridDbApiKey
    {
        get => S.SteamGridDbApiKey;
        set => Set(S.SteamGridDbApiKey, value?.Trim() ?? "", v => S.SteamGridDbApiKey = v);
    }

    public int ImageQualityIndex { get => (int)S.ImageQuality; set { if (Set(S.ImageQuality, (ImageQuality)value, v => S.ImageQuality = v, layout: true)) _main.InvalidateAllImages(); } }

    [ObservableProperty] public partial string KeyStatus { get; set; } = "";
    [ObservableProperty] public partial string CacheSizeText { get; set; } = "";

    [RelayCommand]
    private async Task TestKey()
    {
        if (string.IsNullOrWhiteSpace(S.SteamGridDbApiKey)) { KeyStatus = "Paste a key first."; return; }
        KeyStatus = "Checking…";
        KeyStatus = await _main.Artwork.TestSteamGridDbKeyAsync(S.SteamGridDbApiKey) ? "Key works ✓" : "That key didn't work (or SteamGridDB is unreachable).";
    }

    [RelayCommand]
    private void GetKey() => TryOpen("https://www.steamgriddb.com/profile/preferences/api");

    [RelayCommand]
    private void ClearArtworkCache()
    {
        ArtworkService.ClearAll();
        foreach (var e in _main.Library.Data.Items) if (e.ArtSteamAppId < 0) e.ArtSteamAppId = 0;
        _main.InvalidateAllImages();
        UpdateCacheSize();
        _main.ShowToast("Artwork cache cleared. Images will be fetched again as you scroll.");
    }

    public void UpdateCacheSize()
    {
        var bytes = ArtworkService.CacheSizeBytes();
        CacheSizeText = bytes < 1024 * 1024 ? $"{bytes / 1024} KB" : $"{bytes / 1024.0 / 1024.0:0.#} MB";
    }

    // ------------------------------------------------------------------ Performance

    public bool LowMemoryMode
    {
        get => S.LowMemoryMode;
        set
        {
            if (Set(S.LowMemoryMode, value, v => S.LowMemoryMode = v, layout: true, window: true)) _main.InvalidateAllImages();
        }
    }

    public bool Animations { get => S.Animations; set => Set(S.Animations, value, v => S.Animations = v, window: true); }
    public bool GpuRendering { get => S.GpuRendering; set => Set(S.GpuRendering, value, v => S.GpuRendering = v); }

    [ObservableProperty] public partial string MemoryText { get; set; } = "";

    [RelayCommand]
    private void FreeMemoryNow()
    {
        Native.TrimWorkingSet();
        UpdateMemory();
    }

    public void UpdateMemory()
    {
        try
        {
            using var p = System.Diagnostics.Process.GetCurrentProcess();
            p.Refresh();
            MemoryText = $"Playnest is using {p.WorkingSet64 / 1024.0 / 1024.0:0} MB right now (private: {p.PrivateMemorySize64 / 1024.0 / 1024.0:0} MB).";
        }
        catch { MemoryText = ""; }
    }

    // ------------------------------------------------------------------ About / data

    public string VersionText => $"Playnest {UpdateService.Display(UpdateService.CurrentVersion)}";

    public bool CheckForUpdates { get => S.CheckForUpdates; set => Set(S.CheckForUpdates, value, v => S.CheckForUpdates = v); }

    [ObservableProperty] public partial string UpdateStatusText { get; set; } = "";
    [ObservableProperty] public partial string UpdateNotes { get; set; } = "";
    [ObservableProperty] public partial bool HasUpdate { get; set; }
    [ObservableProperty] public partial bool IsCheckingUpdate { get; set; }
    private UpdateInfo? _update;

    public bool HasUpdateStatus => UpdateStatusText.Length > 0;
    public bool HasUpdateNotes => UpdateNotes.Length > 0;
    partial void OnUpdateStatusTextChanged(string value) => OnPropertyChanged(nameof(HasUpdateStatus));
    partial void OnUpdateNotesChanged(string value) => OnPropertyChanged(nameof(HasUpdateNotes));

    [RelayCommand]
    private async Task CheckUpdatesNow()
    {
        if (IsCheckingUpdate) return;
        IsCheckingUpdate = true;
        UpdateStatusText = "Checking…";
        try { ShowUpdateResult(await _main.CheckForUpdatesAsync(manual: true)); }
        finally { IsCheckingUpdate = false; }
    }

    [RelayCommand]
    private void OpenUpdatePage()
    {
        if (_update is not null) _main.OpenUpdatePage(_update);
    }

    /// <summary>Shows the result of the last check (also the automatic one) when the settings page opens.</summary>
    public void ShowUpdateResult(UpdateResult? r)
    {
        _update = r?.Info;
        HasUpdate = r?.Status == UpdateStatus.Available;
        UpdateNotes = r?.Info?.Notes ?? "";
        UpdateStatusText = r?.Status switch
        {
            UpdateStatus.Available => $"{r.Info!.Title} is available. You have {UpdateService.Display(UpdateService.CurrentVersion)}.",
            UpdateStatus.UpToDate => "You have the latest version.",
            UpdateStatus.Failed => "Couldn't check for updates. There may be no internet connection, or no public release has been published yet.",
            _ => "",
        };
    }
    public string DataFolder => AppPaths.DataDir;

    [RelayCommand]
    private void OpenDataFolder() => TryOpen(AppPaths.DataDir);

    [RelayCommand]
    private async Task ExportSettings()
    {
        var p = await _main.Ui.PickSaveFile("Export settings", "playnest-settings.json", "JSON", ["*.json"]);
        if (p is null) return;
        try
        {
            await File.WriteAllTextAsync(p, Storage.ExportSettings(S));
            _main.ShowToast("Settings exported.");
        }
        catch (Exception ex) { _main.ShowToast("Export failed: " + ex.Message, error: true); }
    }

    [RelayCommand]
    private async Task ImportSettings()
    {
        var p = await _main.Ui.PickFile("Import settings", "JSON", ["*.json"]);
        if (p is null) return;
        AppSettings? imported;
        try { imported = Storage.ImportSettings(await File.ReadAllTextAsync(p)); }
        catch (Exception ex) { _main.ShowToast("Import failed: " + ex.Message, error: true); return; }
        if (imported is null) { _main.ShowToast("That file isn't a Playnest settings file.", error: true); return; }
        // Copy every public property so the existing object (shared by the app) is updated in place.
        foreach (var prop in SettingsProperties)
            prop.Set(S, prop.Get(imported));
        if (!string.IsNullOrWhiteSpace(imported.SteamGridDbApiKey)) S.SteamGridDbApiKey = imported.SteamGridDbApiKey;
        Storage.SaveSettings(S);
        ThemeService.Apply(S);
        _main.Ui.ApplyWindowSettings();
        _main.OnLayoutSettingsChanged();
        foreach (var p2 in typeof(SettingsViewModel).GetProperties().Select(x => x.Name)) OnPropertyChanged(p2);
        _main.ShowToast("Settings imported.");
    }

    [RelayCommand]
    private async Task ResetSettings()
    {
        if (!await _main.Ui.Confirm("Reset all settings?", "Appearance, library and scanning options go back to their defaults. Your games, favourites and playtime are kept.", "Reset", danger: true))
            return;
        var fresh = new AppSettings();
        foreach (var prop in SettingsProperties) prop.Set(S, prop.Get(fresh));
        Storage.SaveSettings(S);
        ThemeService.Apply(S);
        _main.Ui.ApplyWindowSettings();
        _main.OnLayoutSettingsChanged();
        foreach (var p2 in typeof(SettingsViewModel).GetProperties().Select(x => x.Name)) OnPropertyChanged(p2);
        _main.ShowToast("Settings reset.");
    }

    /// <summary>Explicit copy list (reflection-free, so it stays trim/AOT-safe).</summary>
    private static readonly (Func<AppSettings, object?> Get, Action<AppSettings, object?> Set)[] SettingsProperties = BuildCopiers();

    private static (Func<AppSettings, object?>, Action<AppSettings, object?>)[] BuildCopiers() =>
    [
        (s => s.Theme, (s, v) => s.Theme = (ThemeMode)v!), (s => s.AccentColor, (s, v) => s.AccentColor = (string)v!),
        (s => s.Backdrop, (s, v) => s.Backdrop = (BackdropMode)v!), (s => s.BackgroundImage, (s, v) => s.BackgroundImage = (string?)v),
        (s => s.BackgroundImageOpacity, (s, v) => s.BackgroundImageOpacity = (double)v!), (s => s.CornerRadius, (s, v) => s.CornerRadius = (double)v!),
        (s => s.FontSize, (s, v) => s.FontSize = (double)v!), (s => s.FontFamily, (s, v) => s.FontFamily = (string)v!),
        (s => s.CompactSidebar, (s, v) => s.CompactSidebar = (bool)v!), (s => s.ShowSidebarCounts, (s, v) => s.ShowSidebarCounts = (bool)v!),
        (s => s.ViewMode, (s, v) => s.ViewMode = (LibraryViewMode)v!), (s => s.CoverWidth, (s, v) => s.CoverWidth = (double)v!),
        (s => s.IconSize, (s, v) => s.IconSize = (double)v!), (s => s.TileSpacing, (s, v) => s.TileSpacing = (double)v!),
        (s => s.ShowTitles, (s, v) => s.ShowTitles = (bool)v!), (s => s.ShowSourceBadges, (s, v) => s.ShowSourceBadges = (bool)v!),
        (s => s.ShowPlaytimeOnTiles, (s, v) => s.ShowPlaytimeOnTiles = (bool)v!),
        (s => s.ShowSizeOnTiles, (s, v) => s.ShowSizeOnTiles = (bool)v!), (s => s.CalculateSizes, (s, v) => s.CalculateSizes = (bool)v!),
        (s => s.TrackExternalPlaytime, (s, v) => s.TrackExternalPlaytime = (bool)v!), (s => s.ShowDetailsPanel, (s, v) => s.ShowDetailsPanel = (bool)v!),
        (s => s.ShowRecentRow, (s, v) => s.ShowRecentRow = (bool)v!), (s => s.Sort, (s, v) => s.Sort = (SortMode)v!),
        (s => s.SortDescending, (s, v) => s.SortDescending = (bool)v!), (s => s.FavoritesFirst, (s, v) => s.FavoritesFirst = (bool)v!),
        (s => s.HoverEffects, (s, v) => s.HoverEffects = (bool)v!), (s => s.DoubleClick, (s, v) => s.DoubleClick = (DoubleClickAction)v!),
        (s => s.QuickPlay, (s, v) => s.QuickPlay = (QuickPlayMode)v!),
        (s => s.CheckForUpdates, (s, v) => s.CheckForUpdates = (bool)v!),
        (s => s.OnGameLaunch, (s, v) => s.OnGameLaunch = (LaunchBehavior)v!), (s => s.RestoreAfterGameExit, (s, v) => s.RestoreAfterGameExit = (bool)v!),
        (s => s.ConfirmUnusualLaunchLinks, (s, v) => s.ConfirmUnusualLaunchLinks = (bool)v!),
        (s => s.CloseToTray, (s, v) => s.CloseToTray = (bool)v!), (s => s.StartMinimized, (s, v) => s.StartMinimized = (bool)v!),
        (s => s.TrackPlaytime, (s, v) => s.TrackPlaytime = (bool)v!), (s => s.ImportSteamPlaytime, (s, v) => s.ImportSteamPlaytime = (bool)v!),
        (s => s.FreeMemoryWhileGaming, (s, v) => s.FreeMemoryWhileGaming = (bool)v!), (s => s.ScanOnStartup, (s, v) => s.ScanOnStartup = (bool)v!),
        (s => s.ScanSteam, (s, v) => s.ScanSteam = (bool)v!), (s => s.ScanEpic, (s, v) => s.ScanEpic = (bool)v!),
        (s => s.ScanGog, (s, v) => s.ScanGog = (bool)v!), (s => s.ScanEA, (s, v) => s.ScanEA = (bool)v!),
        (s => s.ScanUbisoft, (s, v) => s.ScanUbisoft = (bool)v!), (s => s.ScanBattleNet, (s, v) => s.ScanBattleNet = (bool)v!),
        (s => s.ScanXbox, (s, v) => s.ScanXbox = (bool)v!), (s => s.ScanRockstar, (s, v) => s.ScanRockstar = (bool)v!),
        (s => s.ScanRiot, (s, v) => s.ScanRiot = (bool)v!), (s => s.ScanAmazon, (s, v) => s.ScanAmazon = (bool)v!),
        (s => s.ScanInstalledPrograms, (s, v) => s.ScanInstalledPrograms = (bool)v!), (s => s.AutoDetectGameFolders, (s, v) => s.AutoDetectGameFolders = (bool)v!),
        (s => s.DetectLaunchers, (s, v) => s.DetectLaunchers = (bool)v!), (s => s.DetectTools, (s, v) => s.DetectTools = (bool)v!),
        (s => s.IncludeSteamSoftwareAsTools, (s, v) => s.IncludeSteamSoftwareAsTools = (bool)v!),
        (s => s.DownloadArtwork, (s, v) => s.DownloadArtwork = (bool)v!), (s => s.ImageQuality, (s, v) => s.ImageQuality = (ImageQuality)v!),
        (s => s.LowMemoryMode, (s, v) => s.LowMemoryMode = (bool)v!), (s => s.Animations, (s, v) => s.Animations = (bool)v!),
        (s => s.GpuRendering, (s, v) => s.GpuRendering = (bool)v!),
    ];

    private void TryOpen(string target)
    {
        try { LaunchService.OpenPath(target); }
        catch (Exception ex) { _main.ShowToast(ex.Message, error: true); }
    }
}
