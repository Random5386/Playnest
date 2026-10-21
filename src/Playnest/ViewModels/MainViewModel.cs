using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Playnest.Models;
using Playnest.Services;
using Playnest.Services.Net;
using Playnest.Services.Artwork;
using Playnest.Services.Platform;
using Playnest.Services.Scanning;

namespace Playnest.ViewModels;

public sealed partial class MainViewModel : ObservableObject, IGameHost
{
    private readonly Dictionary<string, GameViewModel> _vms = new(StringComparer.OrdinalIgnoreCase);
    private readonly SessionTracker _sessions = new();
    private readonly SizeCalculator _sizes = new();
    private readonly LinkedList<GameViewModel> _imageOrder = new();
    private int _releasedSinceIdle;
    private DispatcherTimer? _idleGcTimer;
    private CancellationTokenSource? _scanCts;
    private DispatcherTimer? _toastTimer;
    private DispatcherTimer? _searchTimer;
    private Timer? _settingsSaveTimer;
    private List<GameViewModel> _filtered = new();
    private int _columns = 1;
    private bool _pendingPrune;
    private bool _hiddenForGame;

    public MainViewModel(AppSettings settings, IUiService ui)
    {
        Settings = settings;
        Ui = ui;
        Library = new LibraryService();
        Artwork = new ArtworkService(() => Settings);
        SettingsPage = new SettingsViewModel(this);
        _sessions.GameStarted += e => Dispatcher.UIThread.Post(() => OnGameStarted(e));
        _sessions.GameExited += (e, d) => Dispatcher.UIThread.Post(() => OnGameExited(e, d));
        ImageCache.SetCapacity(settings.LowMemoryMode ? 12 : 32);
        _sessions.SetWatchAll(settings.TrackPlaytime && settings.TrackExternalPlaytime);
    }

    public AppSettings Settings { get; }
    public IUiService Ui { get; }
    public LibraryService Library { get; }
    public ArtworkService Artwork { get; }
    public SettingsViewModel SettingsPage { get; }

    // ------------------------------------------------------------------ observable state

    public ObservableCollection<SidebarItem> Filters { get; } = new();
    public ObservableCollection<GameRow> Rows { get; } = new();
    public ObservableCollection<GameViewModel> ListItems { get; } = new();
    public ObservableCollection<GameViewModel> RecentGames { get; } = new();
    public ObservableCollection<GameViewModel> Launchers { get; } = new();
    public ObservableCollection<GameViewModel> Tools { get; } = new();

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsLibraryPage), nameof(IsLaunchersPage), nameof(IsSettingsPage), nameof(IsDetailsOpen))]
    public partial AppPage CurrentPage { get; set; } = AppPage.Library;

    partial void OnCurrentPageChanged(AppPage value)
    {
        foreach (var f in Filters) f.IsSelected = value == AppPage.Library && f.Key == _filterKey;
        if (value != AppPage.Settings) return;
        SettingsPage.UpdateMemory();
        SettingsPage.UpdateCacheSize();
        if (LastUpdateResult is { Status: not UpdateStatus.Failed } r && !SettingsPage.IsCheckingUpdate) SettingsPage.ShowUpdateResult(r);
    }

    public bool IsLibraryPage => CurrentPage == AppPage.Library;
    public bool IsLaunchersPage => CurrentPage == AppPage.Launchers;
    public bool IsSettingsPage => CurrentPage == AppPage.Settings;

    [ObservableProperty] public partial string SearchText { get; set; } = "";
    [ObservableProperty] public partial string FilterTitle { get; set; } = "All games";
    [ObservableProperty] public partial string CountText { get; set; } = "";
    [ObservableProperty] public partial bool IsScanning { get; set; }
    [ObservableProperty] public partial string ScanStatus { get; set; } = "";
    [ObservableProperty] public partial Toast? CurrentToast { get; set; }
    [ObservableProperty] public partial bool IsEmpty { get; set; }
    [ObservableProperty] public partial string EmptyText { get; set; } = "";
    [ObservableProperty] public partial double ViewportWidth { get; set; } = 1000;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsDetailsOpen), nameof(HasSelection))]
    public partial GameViewModel? SelectedGame { get; set; }

    public bool HasSelection => SelectedGame is not null;
    public bool IsDetailsOpen => Settings.ShowDetailsPanel && SelectedGame is not null && IsLibraryPage;
    public bool ShowRecent => RecentGames.Count > 0;

    public bool IsCoverView => Settings.ViewMode == LibraryViewMode.Covers;
    public bool IsIconView => Settings.ViewMode == LibraryViewMode.Icons;
    public bool IsListView => Settings.ViewMode == LibraryViewMode.List;
    public bool IsGridView => !IsListView;

    public double TileWidth => IsCoverView ? Settings.CoverWidth : Settings.IconSize + 36;
    public double CoverHeight => Math.Round(Settings.CoverWidth * 1.5);
    public double IconSize => Settings.IconSize;
    public double TileSpacing => Settings.TileSpacing;
    public Avalonia.Thickness TileMargin => new(0, 0, Settings.TileSpacing, Settings.TileSpacing);
    public bool ShowTitles => Settings.ShowTitles;
    public bool ShowBadges => Settings.ShowSourceBadges;
    public bool ShowTilePlaytime => Settings.ShowPlaytimeOnTiles;
    public bool HoverEffects => Settings.HoverEffects && !Settings.LowMemoryMode;
    public bool CompactSidebar => Settings.CompactSidebar;
    public bool ShowSidebarCounts => Settings.ShowSidebarCounts && !Settings.CompactSidebar;
    public double SidebarWidth => Settings.CompactSidebar ? 64 : 232;
    public string SortLabel => Settings.Sort switch
    {
        SortMode.LastPlayed => "Last played",
        SortMode.Playtime => "Playtime",
        SortMode.RecentlyAdded => "Recently added",
        SortMode.Source => "Store",
        SortMode.Size => "Size",
        _ => "Name",
    };

    public bool ImagesSuspended { get; private set; }

    private string _filterKey = "all";

    // ------------------------------------------------------------------ start-up

    public void Initialize()
    {
        Task.Run(ArtworkService.MigrateCache);
        Library.Load();
        SyncViewModels();
        var key = Filters.Any(f => f.Key == Settings.LastFilter) ? Settings.LastFilter : "all";
        SelectFilterByKey(key);
        if (Settings.ScanOnStartup || Library.Data.Items.Count == 0)
        {
            // Let the window paint first; the scan runs in the background.
            DispatcherTimer.RunOnce(() => _ = ScanAsync(quiet: Library.Data.Items.Count > 0), TimeSpan.FromMilliseconds(800));
        }
        else
        {
            DispatcherTimer.RunOnce(StartSizeCalculation, TimeSpan.FromSeconds(15));
        }
        DispatcherTimer.RunOnce(() => _ = CheckForUpdatesAsync(manual: false), TimeSpan.FromSeconds(20));
    }

    // ------------------------------------------------------------------ updates

    public UpdateResult? LastUpdateResult { get; private set; }

    /// <summary>
    /// Asks GitHub for the latest release. Automatic checks run at most once a day and only when enabled,
    /// and only show a small notice. Nothing is ever downloaded or installed.
    /// </summary>
    public async Task<UpdateResult?> CheckForUpdatesAsync(bool manual)
    {
        if (!manual)
        {
            if (!Settings.CheckForUpdates) return null;
            if (Settings.LastUpdateCheckUtc is DateTime last && DateTime.UtcNow - last < TimeSpan.FromHours(24)) return null;
        }
        var result = await Task.Run(() => UpdateService.CheckAsync(UpdateService.CurrentVersion));
        LastUpdateResult = result;
        if (result.Status != UpdateStatus.Failed)
        {
            Settings.LastUpdateCheckUtc = DateTime.UtcNow;
            SaveSettingsSoon();
        }
        if (!manual && result is { Status: UpdateStatus.Available, Info: { } info })
            ShowToast($"Playnest {UpdateService.Display(info.Version)} is available.", actionText: "Download", action: () => OpenUpdatePage(info));
        return result;
    }

    public void OpenUpdatePage(UpdateInfo info)
    {
        // The link is built by UpdateService from the fixed repository name, but check it again before opening.
        if (!info.PageUrl.StartsWith($"https://github.com/{UpdateService.Repository}/releases/", StringComparison.Ordinal)) return;
        try { LaunchService.OpenPath(info.PageUrl); }
        catch (Exception ex) { ShowToast("Couldn't open the browser: " + ex.Message, error: true); }
    }

    /// <summary>Measures missing install sizes in the background (low priority).</summary>
    public void StartSizeCalculation()
    {
        if (!Settings.CalculateSizes) return;
        _sizes.Start(Library.Snapshot(), e => Dispatcher.UIThread.Post(() =>
        {
            if (_vms.TryGetValue(e.Id, out var vm)) vm.Refresh();
            UpdateCountText();
            Library.SaveSoon();
        }));
    }

    /// <summary>Settings for playtime watching changed.</summary>
    public void ApplyPlaytimeSettings() => _sessions.SetWatchAll(Settings.TrackPlaytime && Settings.TrackExternalPlaytime);

    // ------------------------------------------------------------------ scanning

    [RelayCommand]
    private Task Scan() => ScanAsync(quiet: false);

    public async Task ScanAsync(bool quiet)
    {
        if (IsScanning) return;
        IsScanning = true;
        _scanCts = new CancellationTokenSource();
        var progress = new Progress<string>(s => ScanStatus = s);
        try
        {
            var settings = Settings;
            var token = _scanCts.Token;
            var result = await Task.Run(() => ScanService.Run(settings, progress, token), token);
            var summary = Library.Merge(result, Settings);
            _sessions.ForgetFolderContents(); // games may have been installed, updated or patched
            SyncViewModels();
            Rebuild();
            StartSizeCalculation();
            if (!quiet || summary.Added > 0 || summary.Removed > 0)
            {
                var parts = new List<string>();
                if (summary.Added > 0) parts.Add($"{summary.Added} new");
                if (summary.Removed > 0) parts.Add($"{summary.Removed} removed");
                var games = Library.Data.Items.Count(i => i.EffectiveKind == ItemKind.Game);
                var msg = parts.Count > 0 ? $"Scan complete: {string.Join(", ", parts)}. {games} games in your library." : $"Scan complete. {games} games, nothing new.";
                if (result.Errors.Count > 0) msg += $" ({result.Errors.Count} source(s) had problems — see the log.)";
                ShowToast(msg);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error("scan", ex);
            ShowToast("Scan failed: " + ex.Message, error: true);
        }
        finally
        {
            IsScanning = false;
            ScanStatus = "";
            _scanCts?.Dispose();
            _scanCts = null;
        }
    }

    [RelayCommand]
    private void CancelScan() => _scanCts?.Cancel();

    // ------------------------------------------------------------------ view model sync & filtering

    private void SyncViewModels()
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in Library.Data.Items)
        {
            ids.Add(e.Id);
            if (_vms.TryGetValue(e.Id, out var vm))
            {
                if (!ReferenceEquals(vm.Entry, e))
                {
                    _imageOrder.Remove(vm);
                    vm.ReleaseImages();
                    _vms[e.Id] = new GameViewModel(e, this);
                }
                else vm.Refresh();
            }
            else _vms[e.Id] = new GameViewModel(e, this);
        }
        foreach (var gone in _vms.Keys.Where(k => !ids.Contains(k)).ToList())
        {
            _imageOrder.Remove(_vms[gone]);
            _vms[gone].ReleaseImages();
            _vms.Remove(gone);
        }
        if (SelectedGame is not null && !_vms.ContainsKey(SelectedGame.Entry.Id)) SelectedGame = null;
        BuildFilters();
        BuildLaunchers();
        _sessions.UpdateIndex(Library.Snapshot());
    }

    private IEnumerable<GameViewModel> Games => _vms.Values.Where(v => v.Kind == ItemKind.Game);

    private void BuildFilters()
    {
        var games = Games.ToList();
        var items = new List<SidebarItem>
        {
            new() { Key = "all", Label = "All games", Glyph = "\uE7FC", Count = games.Count },
            new() { Key = "favorites", Label = "Favourites", Glyph = "\uE734", Count = games.Count(g => g.IsFavorite) },
            new() { Key = "recent", Label = "Recently played", Glyph = "\uE823", Count = games.Count(g => IsRecent(g.Entry)) },
        };
        var bySource = games.GroupBy(g => g.Entry.Source).OrderBy(g => SourceOrder(g.Key));
        bool header = false;
        foreach (var grp in bySource)
        {
            if (!header) { items.Add(new SidebarItem { Key = "hdr", Label = "Stores", Glyph = "", IsHeader = true }); header = true; }
            items.Add(new SidebarItem { Key = "source:" + grp.Key, Label = GameSourceInfo.DisplayName(grp.Key), Glyph = "\uE719", Count = grp.Count() });
        }

        // Keep the existing collection when only counts changed (avoids flicker).
        if (Filters.Count == items.Count && Filters.Zip(items).All(p => p.First.Key == p.Second.Key))
        {
            for (int i = 0; i < items.Count; i++) Filters[i].Count = items[i].Count;
            return;
        }
        Filters.Clear();
        foreach (var i in items) Filters.Add(i);
        foreach (var f in Filters) f.IsSelected = IsLibraryPage && f.Key == _filterKey;
    }

    private static int SourceOrder(GameSource s) => s switch
    {
        GameSource.Steam => 0, GameSource.Epic => 1, GameSource.Gog => 2, GameSource.Xbox => 3, GameSource.EA => 4,
        GameSource.Ubisoft => 5, GameSource.BattleNet => 6, GameSource.Rockstar => 7, GameSource.Riot => 8, GameSource.Amazon => 9,
        GameSource.Folder => 10, GameSource.Registry => 11, GameSource.Manual => 12, _ => 13,
    };

    private static bool IsRecent(GameEntry e) => e.LastPlayedUtc is { } d && DateTime.UtcNow - d < TimeSpan.FromDays(60);

    private void BuildLaunchers()
    {
        Launchers.Clear();
        var counts = Games.GroupBy(g => GameSourceInfo.LauncherId(g.Entry.Source) ?? "").ToDictionary(g => g.Key, g => g.Count());
        foreach (var l in _vms.Values.Where(v => v.Kind == ItemKind.Launcher).OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase))
        {
            var id = l.Entry.SourceId ?? "";
            if (id == "origin") id = "ea";
            var n = counts.TryGetValue(id, out var c) ? c : 0;
            l.Subtitle = !IsStoreLauncherId(id) ? "Launcher" : n switch { 0 => "No games found", 1 => "1 game", _ => $"{n} games" };
            Launchers.Add(l);
        }
        Tools.Clear();
        foreach (var t in _vms.Values.Where(v => v.Kind == ItemKind.Tool).OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase))
            Tools.Add(t);
        OnPropertyChanged(nameof(HasLaunchers));
        OnPropertyChanged(nameof(HasTools));
    }

    private static bool IsStoreLauncherId(string id) =>
        Enum.GetValues<GameSource>().Any(s => GameSourceInfo.LauncherId(s) == id);

    public bool HasLaunchers => Launchers.Count > 0;
    public bool HasTools => Tools.Count > 0;

    [RelayCommand]
    private void SelectFilter(SidebarItem? item)
    {
        if (item is null || item.IsHeader) return;
        SelectFilterByKey(item.Key);
        CurrentPage = AppPage.Library;
    }

    private void SelectFilterByKey(string key)
    {
        _filterKey = key;
        foreach (var f in Filters) f.IsSelected = IsLibraryPage && f.Key == key;
        FilterTitle = Filters.FirstOrDefault(f => f.Key == key)?.Label ?? "All games";
        if (Settings.LastFilter != key) { Settings.LastFilter = key; SaveSettingsSoon(); }
        Rebuild();
    }

    partial void OnSearchTextChanged(string value)
    {
        _searchTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(150), DispatcherPriority.Background, (_, _) =>
        {
            _searchTimer!.Stop();
            Rebuild();
        });
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    partial void OnViewportWidthChanged(double value)
    {
        var cols = ComputeColumns();
        if (cols != _columns) { _columns = cols; Regroup(); }
        UpdateRecent();
    }

    private int ComputeColumns()
    {
        var w = ViewportWidth - 8;
        var tile = TileWidth + Settings.TileSpacing;
        return Math.Max(1, (int)Math.Floor((w + Settings.TileSpacing) / tile));
    }

    /// <summary>Recomputes the filtered + sorted list and the grid rows.</summary>
    public void Rebuild()
    {
        IEnumerable<GameViewModel> q = Games;
        switch (_filterKey)
        {
            case "favorites": q = q.Where(g => g.IsFavorite); break;
            case "recent": q = q.Where(g => IsRecent(g.Entry)); break;
            default:
                if (_filterKey.StartsWith("source:") && Enum.TryParse<GameSource>(_filterKey[7..], out var src))
                    q = q.Where(g => g.Entry.Source == src);
                break;
        }

        var search = SearchText.Trim();
        if (search.Length > 0)
        {
            var norm = PathUtil.NormalizeName(search);
            q = q.Where(g => g.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                             || (norm.Length > 0 && g.SearchKey.Contains(norm)));
        }

        q = _filterKey == "recent" && search.Length == 0 ? q.OrderByDescending(g => g.Entry.LastPlayedUtc) : Sort(q);
        _filtered = q.ToList();

        UpdateCountText();
        IsEmpty = _filtered.Count == 0;
        EmptyText = Games.Any()
            ? (search.Length > 0 ? $"No games match “{search}”." : "Nothing here yet.")
            : IsScanning ? "Looking for your games…" : "No games found yet. Press Scan, or add a game or a game folder.";

        UpdateRecent();

        _columns = ComputeColumns();
        Regroup();
    }

    private void UpdateCountText()
    {
        var games = _filtered.Count == 1 ? "1 game" : $"{_filtered.Count} games";
        long total = _filtered.Sum(g => g.Entry.SizeBytes);
        CountText = total > 0 ? $"{games} · {SizeCalculator.Format(total)}" : games;
    }

    /// <summary>"Continue playing" strip: as many cards as fit on one line.</summary>
    private void UpdateRecent()
    {
        int fit = Math.Clamp((int)((ViewportWidth + 12) / 262), 1, 8);
        bool show = Settings.ShowRecentRow && _filterKey == "all" && SearchText.Trim().Length == 0;
        var wanted = show
            ? Games.Where(g => IsRecent(g.Entry)).OrderByDescending(g => g.Entry.LastPlayedUtc).Take(fit).ToList()
            : new List<GameViewModel>();
        if (wanted.SequenceEqual(RecentGames)) return;
        RecentGames.Clear();
        foreach (var g in wanted) RecentGames.Add(g);
        OnPropertyChanged(nameof(ShowRecent));
    }

    private IEnumerable<GameViewModel> Sort(IEnumerable<GameViewModel> q)
    {
        bool desc = Settings.SortDescending;
        IOrderedEnumerable<GameViewModel> o = Settings.FavoritesFirst
            ? q.OrderByDescending(g => g.IsFavorite)
            : q.OrderBy(_ => 0);
        o = Settings.Sort switch
        {
            SortMode.LastPlayed => desc ? o.ThenBy(g => g.Entry.LastPlayedUtc ?? DateTime.MinValue) : o.ThenByDescending(g => g.Entry.LastPlayedUtc ?? DateTime.MinValue),
            SortMode.Playtime => desc ? o.ThenBy(g => g.Entry.TotalPlaytimeSeconds) : o.ThenByDescending(g => g.Entry.TotalPlaytimeSeconds),
            SortMode.RecentlyAdded => desc ? o.ThenBy(g => g.Entry.AddedUtc) : o.ThenByDescending(g => g.Entry.AddedUtc),
            SortMode.Source => desc ? o.ThenByDescending(g => SourceOrder(g.Entry.Source)) : o.ThenBy(g => SourceOrder(g.Entry.Source)),
            SortMode.Size => desc ? o.ThenBy(g => g.Entry.SizeBytes) : o.ThenByDescending(g => g.Entry.SizeBytes),
            _ => o,
        };
        return desc && Settings.Sort == SortMode.Name
            ? o.ThenByDescending(g => g.SortName, StringComparer.OrdinalIgnoreCase)
            : o.ThenBy(g => g.SortName, StringComparer.OrdinalIgnoreCase);
    }

    private void Regroup()
    {
        Rows.Clear();
        ListItems.Clear();
        if (IsListView)
        {
            foreach (var g in _filtered) ListItems.Add(g);
            return;
        }
        for (int i = 0; i < _filtered.Count; i += _columns)
            Rows.Add(new GameRow(_filtered.GetRange(i, Math.Min(_columns, _filtered.Count - i))));
    }

    // ------------------------------------------------------------------ pages, view options

    [RelayCommand] private void ShowLibrary() => CurrentPage = AppPage.Library;
    [RelayCommand] private void ShowLaunchers() => CurrentPage = AppPage.Launchers;
    [RelayCommand] private void ShowSettings() => CurrentPage = AppPage.Settings;
    [RelayCommand] private void ClearSearch() => SearchText = "";

    [RelayCommand]
    private void SetView(string mode)
    {
        if (Enum.TryParse<LibraryViewMode>(mode, out var m)) SettingsPage.ViewModeIndex = (int)m;
    }

    [RelayCommand]
    private void SetSort(string mode)
    {
        if (Enum.TryParse<SortMode>(mode, out var m))
        {
            if (Settings.Sort == m) Settings.SortDescending = !Settings.SortDescending;
            else { Settings.Sort = m; Settings.SortDescending = false; }
            SaveSettingsSoon();
            OnPropertyChanged(nameof(SortLabel));
            Rebuild();
        }
    }

    [RelayCommand]
    private void CloseDetails() => SelectedGame = null;

    [RelayCommand]
    private void PlaySelected()
    {
        if (SelectedGame is { } g) Play(g);
    }

    /// <summary>Called by the settings page whenever something that affects the library view changes.</summary>
    public void OnLayoutSettingsChanged()
    {
        foreach (var p in new[]
                 {
                     nameof(IsCoverView), nameof(IsIconView), nameof(IsListView), nameof(IsGridView), nameof(TileWidth), nameof(CoverHeight),
                     nameof(IconSize), nameof(TileSpacing), nameof(TileMargin), nameof(ShowTitles), nameof(ShowBadges), nameof(ShowTilePlaytime),
                     nameof(HoverEffects), nameof(CompactSidebar), nameof(ShowSidebarCounts), nameof(SidebarWidth), nameof(IsDetailsOpen), nameof(SortLabel),
                 })
            OnPropertyChanged(p);
        ThemeService.ApplyLayout(Settings);
        ImageCache.SetCapacity(Settings.LowMemoryMode ? 12 : 32);
        _columns = ComputeColumns();
        Rebuild();
        LayoutChanged?.Invoke();
    }

    /// <summary>Raised after layout settings changed (the view updates its hover/animation classes).</summary>
    public event Action? LayoutChanged;

    public void SaveSettingsSoon()
    {
        _settingsSaveTimer?.Dispose();
        _settingsSaveTimer = new Timer(_ => Storage.SaveSettings(Settings), null, 600, Timeout.Infinite);
    }

    public void FlushOnExit()
    {
        _settingsSaveTimer?.Dispose();
        Storage.SaveSettings(Settings);
        Library.SaveNow();
        _sessions.Dispose();
        _sizes.Dispose();
    }

    // ------------------------------------------------------------------ IGameHost

    public int DecodeWidth(ArtKind kind)
    {
        double scale = Settings.LowMemoryMode || Settings.ImageQuality == ImageQuality.Low ? 1.0
            : Settings.ImageQuality == ImageQuality.High ? Ui.RenderScaling * 1.5 : Ui.RenderScaling;
        return kind switch
        {
            ArtKind.Cover => (int)(Settings.CoverWidth * scale),
            ArtKind.Icon => (int)(Math.Max(Settings.IconSize, 64) * scale),
            _ => (int)(Settings.LowMemoryMode ? 480 : 720 * Math.Min(scale, 1.5)),
        };
    }

    public bool IsRunning(GameEntry e) => _sessions.IsRunning(e);

    public void Select(GameViewModel g) => SelectedGame = g;

    partial void OnSelectedGameChanged(GameViewModel? oldValue, GameViewModel? newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is not null) newValue.IsSelected = true;
    }

    private GameEntry? _lastLaunched;
    private long _lastLaunchTicks;

    public void Play(GameViewModel g)
    {
        // Ignore a repeat launch of the same game within 3 s (double-clicks, Enter held down, quick play + double-click).
        var now = Environment.TickCount64;
        if (ReferenceEquals(_lastLaunched, g.Entry) && now - _lastLaunchTicks < 3000) return;
        _lastLaunched = g.Entry;
        _lastLaunchTicks = now;
        try
        {
            LaunchService.Launch(g.Entry);
            if (g.Kind == ItemKind.Game)
            {
                g.Entry.LastPlayedUtc = DateTime.UtcNow;
                Library.SaveSoon();
                if (Settings.TrackPlaytime) _sessions.Track(g.Entry);
                if (Settings.OnGameLaunch != LaunchBehavior.Nothing)
                {
                    _hiddenForGame = true;
                    Ui.HideForGame(Settings.OnGameLaunch);
                    if (Settings.FreeMemoryWhileGaming && Settings.OnGameLaunch is LaunchBehavior.MinimizeToTray or LaunchBehavior.Minimize)
                        SuspendImages();
                }
                ShowToast($"Starting {g.Name}…");
            }
        }
        catch (Exception ex)
        {
            _lastLaunchTicks = now - 2000; // after a failure, allow a retry after 1 s instead of 3
            Log.Error("launch " + g.Name, ex);
            ShowToast($"Couldn't start {g.Name}: {ex.Message}", error: true, actionText: "Edit", action: () => _ = Edit(g));
        }
    }

    private void OnGameStarted(GameEntry e)
    {
        if (_vms.TryGetValue(e.Id, out var vm)) vm.IsRunning = true;
    }

    private void OnGameExited(GameEntry e, TimeSpan duration)
    {
        e.PlaytimeSeconds += (long)duration.TotalSeconds;
        e.LastPlayedUtc = DateTime.UtcNow;
        Library.SaveSoon();
        if (_vms.TryGetValue(e.Id, out var vm))
        {
            vm.IsRunning = false;
            vm.Refresh();
        }
        if (_hiddenForGame && !_sessions.HasRunningGames)
        {
            _hiddenForGame = false;
            if (Settings.RestoreAfterGameExit) Ui.RestoreAfterGame();
        }
        BuildFilters();
    }

    /// <summary>
    /// Ends a running game's programs right away (after the user confirms the exact list).
    /// Only programs inside the game's own install folder are touched; see <see cref="GameProcesses"/>.
    /// </summary>
    public async Task ForceStop(GameViewModel g)
    {
        var e = g.Entry;
        var dir = e.EffectiveDir;
        if (!GameProcesses.IsSafeGameFolder(dir))
        {
            ShowToast($"Playnest can't safely stop {g.Name}: its install folder isn't specific to this game. Close it from inside the game.", error: true);
            return;
        }

        var only = _sessions.NamesOnlyFor(e);
        var procs = await Task.Run(() => GameProcesses.Find(dir!, only));
        if (procs.Count == 0)
        {
            ShowToast($"{g.Name} isn't running any more.");
            _sessions.NoteStopped(e);
            return;
        }

        var names = procs.Select(p => p.Name + ".exe").Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var list = string.Join("\n", names.Take(8).Select(n => "• " + n)) + (names.Count > 8 ? $"\n• …and {names.Count - 8} more" : "");
        bool ok = await Ui.Confirm($"Force stop {g.Name}?",
            $"These programs will be ended straight away. Anything you haven't saved in the game will be lost.\n\n{list}",
            "Force stop", danger: true);
        if (!ok) return;

        var (stopped, denied) = await Task.Run(() => GameProcesses.Stop(procs));
        _sessions.NoteStopped(e);
        if (denied == 0)
            ShowToast($"{g.Name} was stopped.");
        else
            ShowToast(stopped > 0
                    ? $"Part of {g.Name} couldn't be stopped. It may be running as administrator or protected by anti-cheat."
                    : $"Windows didn't let Playnest stop {g.Name}. It may be running as administrator or protected by anti-cheat. Close it from inside the game, or run Playnest as administrator.",
                error: true);
    }

    public void OpenFolder(GameViewModel g)
    {
        try { LaunchService.OpenFolder(g.Entry); }
        catch (Exception ex) { ShowToast(ex.Message, error: true); }
    }

    public void ToggleFavorite(GameViewModel g)
    {
        g.Entry.Favorite = !g.Entry.Favorite;
        g.Refresh();
        Library.SaveSoon();
        BuildFilters();
        if (_filterKey == "favorites" || Settings.FavoritesFirst) Rebuild();
    }

    public async Task Edit(GameViewModel g)
    {
        var vm = new EditGameViewModel(g.Entry, Ui, isNew: false);
        if (!await Ui.EditGame(vm)) return;
        ApplyEdit(g.Entry, vm);
        g.Refresh();
        g.ReloadImages(force: false);
        Library.SaveSoon();
        SyncViewModels();
        Rebuild();
    }

    private void ApplyEdit(GameEntry e, EditGameViewModel vm)
    {
        var name = vm.Name.Trim();
        if (e.IsManual) e.Name = name;
        else e.CustomName = name == e.Name || name.Length == 0 ? null : name;

        var kind = (ItemKind)Math.Clamp(vm.KindIndex, 0, 2);
        if (e.IsManual) e.Kind = kind;
        else e.CustomKind = kind == e.Kind ? null : kind;

        string? Diff(string v, string? detected) => string.IsNullOrWhiteSpace(v) || v == detected ? null : v.Trim();
        if (e.IsManual)
        {
            e.ExePath = string.IsNullOrWhiteSpace(vm.ExePath) ? null : vm.ExePath.Trim();
            e.Arguments = string.IsNullOrWhiteSpace(vm.Arguments) ? null : vm.Arguments;
            e.LaunchUri = string.IsNullOrWhiteSpace(vm.LaunchUri) ? null : vm.LaunchUri.Trim();
            if (e.ExePath is not null) e.InstallDir ??= Path.GetDirectoryName(e.ExePath);
        }
        else
        {
            e.CustomExePath = Diff(vm.ExePath, e.ExePath);
            e.CustomArguments = vm.Arguments == (e.Arguments ?? "") ? null : vm.Arguments;
            e.CustomLaunchUri = Diff(vm.LaunchUri, e.LaunchUri);
        }
        e.WorkingDir = string.IsNullOrWhiteSpace(vm.WorkingDir) ? null : vm.WorkingDir.Trim();
        e.RunAsAdmin = vm.RunAsAdmin;
        e.CustomCover = ImportCustomImage(e, vm.CoverPath, "cover", e.CustomCover);
        e.CustomIcon = ImportCustomImage(e, vm.IconPath, "icon", e.CustomIcon);
        e.CustomHero = ImportCustomImage(e, vm.HeroPath, "hero", e.CustomHero);
    }

    /// <summary>Copies a chosen image into Playnest's folder (icons can come from .exe/.ico files).</summary>
    private static string? ImportCustomImage(GameEntry e, string chosen, string kind, string? current)
    {
        if (string.IsNullOrWhiteSpace(chosen)) return null;
        if (chosen == current) return current;
        try
        {
            Directory.CreateDirectory(AppPaths.CustomArtDir);
            var safe = string.Concat(e.Id.Select(c => char.IsLetterOrDigit(c) ? c : '_'));
            var ext = Path.GetExtension(chosen).ToLowerInvariant();
            if (ext is ".exe" or ".ico" or ".dll")
            {
                var png = PeIcon.ExtractLargestPng(chosen);
                if (png is null) return current;
                var dest = Path.Combine(AppPaths.CustomArtDir, $"{safe}-{kind}-{DateTime.UtcNow.Ticks}.png");
                File.WriteAllBytes(dest, png);
                return dest;
            }
            if (ext is not (".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp")) return current;
            if (new FileInfo(chosen).Length > 30 * 1024 * 1024) return current; // not a sensible artwork file
            var target = Path.Combine(AppPaths.CustomArtDir, $"{safe}-{kind}-{DateTime.UtcNow.Ticks}{ext}");
            File.Copy(chosen, target, overwrite: true);
            return target;
        }
        catch (Exception ex)
        {
            Log.Error("import image", ex);
            return current;
        }
    }

    public async Task ChangeImage(GameViewModel g, ArtKind kind)
    {
        string[] patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp", "*.ico", "*.exe"];
        var title = kind switch { ArtKind.Cover => "Choose a cover (portrait)", ArtKind.Icon => "Choose an icon", _ => "Choose a background (wide)" };
        var p = await Ui.PickFile(title, "Images", patterns);
        if (p is null) return;
        var e = g.Entry;
        switch (kind)
        {
            case ArtKind.Cover: e.CustomCover = ImportCustomImage(e, p, "cover", e.CustomCover); break;
            case ArtKind.Icon: e.CustomIcon = ImportCustomImage(e, p, "icon", e.CustomIcon); break;
            default: e.CustomHero = ImportCustomImage(e, p, "hero", e.CustomHero); break;
        }
        Library.SaveSoon();
        g.ReloadImages(force: false);
    }

    public void RefreshArtwork(GameViewModel g)
    {
        Artwork.Clear(g.Entry);
        if (g.Entry.ArtSteamAppId < 0) g.Entry.ArtSteamAppId = 0;
        g.ReloadImages(force: true);
        ShowToast($"Refreshing artwork for {g.Name}…");
    }

    public async Task Remove(GameViewModel g)
    {
        var what = g.Kind switch { ItemKind.Launcher => "launcher", ItemKind.Tool => "tool", _ => "game" };
        bool ok = await Ui.Confirm($"Remove {g.Name}?",
            g.Entry.IsManual
                ? $"This removes the {what} from Playnest. Nothing is deleted from your PC."
                : $"This hides the {what} from Playnest. Nothing is deleted from your PC, and future scans won't add it again.\n\nYou can bring it back in Settings → Scanning → Removed items.",
            "Remove");
        if (!ok) return;
        Library.Ignore(g.Entry);
        if (SelectedGame == g) SelectedGame = null;
        SyncViewModels();
        Rebuild();
        SettingsPage.RefreshIgnored();
        ShowToast($"{g.Name} removed from your library.");
    }

    public async Task Uninstall(GameViewModel g)
    {
        var e = g.Entry;
        if (!LaunchService.CanUninstall(e))
        {
            var dir = e.EffectiveDir;
            if (dir is null || !Directory.Exists(dir)) { ShowToast("No uninstaller or install folder was found for this item.", error: true); return; }
            // Same rule as force stop: never offer to bin a drive, a library folder like "D:\Games", Program Files, etc.
            if (!GameProcesses.IsSafeGameFolder(dir))
            {
                ShowToast($"No uninstaller was found, and {dir} isn't a folder that belongs only to {g.Name}, so Playnest won't remove it.", error: true);
                return;
            }
            bool recycle = await Ui.Confirm($"Uninstall {g.Name}?",
                $"No uninstaller was found. Playnest can move the game folder to the Recycle Bin instead:\n\n{dir}\n\nYou can restore it from the Recycle Bin if needed.",
                "Move to Recycle Bin", danger: true);
            if (!recycle) return;
            if (Native.RecycleFolder(dir))
            {
                Library.Remove(e);
                SyncViewModels();
                Rebuild();
                ShowToast($"{g.Name} was moved to the Recycle Bin.");
            }
            else ShowToast("The folder wasn't moved to the Recycle Bin.", error: true);
            return;
        }

        var how = !string.IsNullOrWhiteSpace(e.UninstallUri)
            ? e.Source switch
            {
                GameSource.Steam => "Steam will ask you to confirm.",
                GameSource.Ubisoft => "Ubisoft Connect will ask you to confirm.",
                GameSource.Epic => "The Epic Games Launcher will open — choose the game, then ⋯ → Uninstall.",
                GameSource.Xbox => "Windows Settings → Apps will open — find the game there and choose Uninstall.",
                _ => "The store will ask you to confirm.",
            }
            : "The game's own uninstaller will start:\n\n" + (e.UninstallCommand ?? Services.Scanning.FolderScanner.FindLocalUninstaller(e.EffectiveDir ?? "") ?? "");
        if (!await Ui.Confirm($"Uninstall {g.Name}?", how, "Uninstall", danger: true)) return;

        var (outcome, process) = LaunchService.Uninstall(e);
        switch (outcome)
        {
            case UninstallOutcome.Failed:
                ShowToast("The uninstaller couldn't be started. See the log for details.", error: true);
                return;
            case UninstallOutcome.NoUninstaller:
                ShowToast("No uninstaller was found.", error: true);
                return;
        }

        _pendingPrune = true;
        if (process is not null)
        {
            try
            {
                await process.WaitForExitAsync();
                await Task.Delay(1500);
                PruneAfterUninstall();
            }
            catch { }
            finally { process.Dispose(); }
        }
    }

    /// <summary>Called when the window becomes active again (e.g. after the store's uninstall dialog).</summary>
    public void OnWindowActivated()
    {
        if (_pendingPrune) PruneAfterUninstall();
    }

    private void PruneAfterUninstall()
    {
        var gone = Library.PruneMissing();
        if (gone.Count == 0) return;
        _pendingPrune = false;
        SyncViewModels();
        Rebuild();
        ShowToast(gone.Count == 1 ? $"{gone[0].DisplayName} was uninstalled." : $"{gone.Count} items were uninstalled.");
    }

    // ------------------------------------------------------------------ adding

    [RelayCommand]
    private async Task AddGame() => await AddManual(ItemKind.Game);

    [RelayCommand]
    private async Task AddTool() => await AddManual(ItemKind.Tool);

    private async Task AddManual(ItemKind kind)
    {
        var p = await Ui.PickFile(kind == ItemKind.Game ? "Choose a game (exe or shortcut)" : "Choose a program", "Games and shortcuts",
            ["*.exe", "*.lnk", "*.url", "*.bat", "*.cmd"]);
        if (p is null) return;
        var entry = LibraryService.CreateManual(p, kind);
        var vm = new EditGameViewModel(entry, Ui, isNew: true);
        if (!await Ui.EditGame(vm)) return;
        ApplyEdit(entry, vm);
        Library.Add(entry);
        SyncViewModels();
        Rebuild();
        if (_vms.TryGetValue(entry.Id, out var gvm)) Select(gvm);
        ShowToast($"{entry.DisplayName} added.");
    }

    [RelayCommand]
    private async Task AddGameFolder()
    {
        var p = await Ui.PickFolder("Choose a folder that contains games (e.g. D:\\Games)");
        if (p is null) return;
        SettingsPage.AddGameFolderPath(p);
        await ScanAsync(quiet: false);
    }

    // ------------------------------------------------------------------ memory

    /// <summary>
    /// Keeps decoded artwork within a budget: tiles that loaded images longest ago (i.e. scrolled out of view)
    /// drop theirs and reload if they come back. Without this, scrolling a big library would keep every cover in RAM.
    /// </summary>
    public void NoteImagesLoaded(GameViewModel g)
    {
        _imageOrder.Remove(g);
        _imageOrder.AddLast(g);
        int budget = Settings.LowMemoryMode ? 60 : 120; // a full screen of tiles is ~30–60
        while (_imageOrder.Count > budget)
        {
            var oldest = _imageOrder.First!.Value;
            _imageOrder.RemoveFirst();
            if (oldest == SelectedGame) { _imageOrder.AddLast(oldest); continue; }
            oldest.ReleaseImages(); // bitmaps are disposed (or kept briefly for reuse) by ImageCache
            _releasedSinceIdle++;
        }
        if (_releasedSinceIdle > 0)
        {
            // Once scrolling settles, collect the garbage it produced so memory drops back down.
            _idleGcTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(4), DispatcherPriority.Background, (_, _) =>
            {
                _idleGcTimer!.Stop();
                if (_releasedSinceIdle < 30) return;
                _releasedSinceIdle = 0;
                GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            });
            _idleGcTimer.Stop();
            _idleGcTimer.Start();
        }
    }

    /// <summary>Frees decoded images and trims RAM (window hidden / game running).</summary>
    public void SuspendImages()
    {
        ImagesSuspended = true;
        foreach (var vm in _vms.Values) vm.ReleaseImages();
        _imageOrder.Clear();
        ImageCache.ReleaseAll();
        DispatcherTimer.RunOnce(Native.TrimWorkingSet, TimeSpan.FromMilliseconds(300));
    }

    public void ResumeImages()
    {
        if (!ImagesSuspended) return;
        ImagesSuspended = false;
        foreach (var vm in _vms.Values) vm.ResumeImages();
    }

    /// <summary>Recently played games for the tray menu.</summary>
    public IEnumerable<GameViewModel> TrayGames() =>
        Games.Where(g => g.Entry.LastPlayedUtc is not null).OrderByDescending(g => g.Entry.LastPlayedUtc).Take(6);

    public void InvalidateAllImages()
    {
        foreach (var vm in _vms.Values) vm.ReloadImages(force: false);
    }

    public void RefreshAllEntries()
    {
        SyncViewModels();
        Rebuild();
    }

    // ------------------------------------------------------------------ toasts

    public void ShowToast(string message, bool error = false, string? actionText = null, Action? action = null)
    {
        CurrentToast = new Toast { Message = message, IsError = error, ActionText = actionText, Action = action };
        _toastTimer?.Stop();
        _toastTimer = new DispatcherTimer(TimeSpan.FromSeconds(error || actionText is not null ? 8 : 4.5), DispatcherPriority.Background, (_, _) =>
        {
            _toastTimer?.Stop();
            CurrentToast = null;
        });
        _toastTimer.Start();
    }

    [RelayCommand]
    private void DismissToast() => CurrentToast = null;
}
