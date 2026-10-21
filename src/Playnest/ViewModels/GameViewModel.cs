using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Playnest.Models;
using Playnest.Services;
using Playnest.Services.Artwork;
using Playnest.Services.Platform;

namespace Playnest.ViewModels;

/// <summary>What a game tile needs from the main view model.</summary>
public interface IGameHost
{
    ArtworkService Artwork { get; }
    /// <summary>True while Playnest is hidden to free memory; images are not loaded then.</summary>
    bool ImagesSuspended { get; }
    /// <summary>Called after a tile decoded an image, so the host can keep total image memory within budget.</summary>
    void NoteImagesLoaded(GameViewModel g);
    int DecodeWidth(ArtKind kind);
    bool IsRunning(GameEntry e);
    void Play(GameViewModel g);
    Task ForceStop(GameViewModel g);
    void OpenFolder(GameViewModel g);
    Task Edit(GameViewModel g);
    void ToggleFavorite(GameViewModel g);
    Task Uninstall(GameViewModel g);
    Task Remove(GameViewModel g);
    Task ChangeImage(GameViewModel g, ArtKind kind);
    void RefreshArtwork(GameViewModel g);
    void Select(GameViewModel g);
}

public sealed partial class GameViewModel : ObservableObject
{
    private readonly IGameHost _host;
    private Bitmap? _cover, _icon, _hero;
    private int _coverW, _iconW, _heroW;
    private bool _coverLoading, _iconLoading, _heroLoading;
    private bool _coverMissing, _iconMissing, _heroMissing;
    private IBrush? _placeholder;

    public GameViewModel(GameEntry entry, IGameHost host)
    {
        Entry = entry;
        _host = host;
    }

    public GameEntry Entry { get; }

    public string Name => Entry.DisplayName;
    public string SortName => Name.StartsWith("The ", StringComparison.OrdinalIgnoreCase) ? Name[4..] : Name;

    private string? _searchKeyFor, _searchKey;
    /// <summary>Normalised name for search, worked out once per name instead of on every keystroke.</summary>
    public string SearchKey
    {
        get
        {
            var name = Name;
            if (!ReferenceEquals(name, _searchKeyFor)) { _searchKey = PathUtil.NormalizeName(name); _searchKeyFor = name; }
            return _searchKey!;
        }
    }

    public ItemKind Kind => Entry.EffectiveKind;
    public string SourceLabel => GameSourceInfo.Badge(Entry.Source);
    public string SourceFull => GameSourceInfo.DisplayName(Entry.Source);
    public bool IsFavorite => Entry.Favorite;
    public bool IsMissing => Entry.Missing;
    public string? InstallDir => Entry.EffectiveDir;
    public bool HasFolder => InstallDir is not null;
    public string Initials
    {
        get
        {
            var words = Name.Split([' ', ':', '-', '_'], StringSplitOptions.RemoveEmptyEntries);
            return words.Length switch
            {
                0 => "?",
                1 => words[0][..Math.Min(2, words[0].Length)].ToUpperInvariant(),
                _ => string.Concat(words[0][0], words[1][0]).ToUpperInvariant(),
            };
        }
    }

    public IBrush Placeholder => _placeholder ??= ThemeService.PlaceholderBrush(Name);

    public string PlaytimeText => FormatPlaytime(Entry.TotalPlaytimeSeconds);
    public string SizeText => Services.SizeCalculator.Format(Entry.SizeBytes);
    public bool HasSize => Entry.SizeBytes > 0;
    public string LastPlayedText => Entry.LastPlayedUtc is { } d ? FormatAgo(d) : "Never";
    public bool HasPlaytime => Entry.TotalPlaytimeSeconds > 0;
    public string FavoriteMenuText => IsFavorite ? "Remove from favourites" : "Add to favourites";
    public bool CanUninstall => LaunchService.CanUninstall(Entry) || Entry.EffectiveDir is not null;

    [ObservableProperty] public partial bool IsSelected { get; set; }
    [ObservableProperty] public partial bool IsRunning { get; set; }

    /// <summary>Extra line shown on launcher cards (e.g. "12 games").</summary>
    [ObservableProperty] public partial string Subtitle { get; set; } = "";

    // ------------------------------------------------------------------ images (lazy)

    public Bitmap? Cover
    {
        get
        {
            EnsureImage(ArtKind.Cover);
            return _cover;
        }
    }

    public Bitmap? Icon
    {
        get
        {
            EnsureImage(ArtKind.Icon);
            return _icon;
        }
    }

    public Bitmap? Hero
    {
        get
        {
            EnsureImage(ArtKind.Hero);
            return _hero;
        }
    }

    /// <summary>Hero image, falling back to the cover (shown dimmed in the details panel).</summary>
    public Bitmap? Backdrop => Hero ?? Cover;

    public bool HasCover => _cover is not null;

    /// <summary>True when the cover is roughly portrait (fills the tile); landscape/square art is shown whole on a dimmed copy instead of being cropped.</summary>
    public bool CoverFits => _cover is null || _cover.Size.Width / Math.Max(1, _cover.Size.Height) is >= 0.55 and <= 0.82;
    public bool CoverLetterboxed => _cover is not null && !CoverFits;
    public bool HasIcon => _icon is not null;
    public bool ShowCoverPlaceholder => _cover is null;
    public bool ShowIconPlaceholder => _icon is null;

    /// <summary>Cover-grid fallback: when there is no cover but there is an icon, the icon is drawn on a gradient.</summary>
    public Bitmap? CoverFallbackIcon => _cover is null && _coverMissing ? Icon : null;
    public bool ShowIconInitials => _icon is null && _iconMissing;
    public bool ShowInitials => _cover is null && _coverMissing && _icon is null && _iconMissing;

    private void EnsureImage(ArtKind kind)
    {
        if (_host.ImagesSuspended) return;
        int want = _host.DecodeWidth(kind);
        switch (kind)
        {
            case ArtKind.Cover:
                if (_coverLoading || _coverMissing || (_cover is not null && want <= _coverW * 1.25)) return;
                _coverLoading = true;
                break;
            case ArtKind.Icon:
                if (_iconLoading || _iconMissing || (_icon is not null && want <= _iconW * 1.25)) return;
                _iconLoading = true;
                break;
            default:
                if (_heroLoading || _heroMissing || (_hero is not null && want <= _heroW * 1.25)) return;
                _heroLoading = true;
                break;
        }
        _ = LoadAsync(kind, want);
    }

    private int _generation; // bumped whenever images are dropped, so late loads are discarded

    private async Task LoadAsync(ArtKind kind, int width, bool force = false)
    {
        int generation = _generation;
        Bitmap? bmp = null;
        try
        {
            // Off the UI thread: finding the cached file and reading its header touch the disk for every tile shown.
            var path = await Task.Run(() => _host.Artwork.GetAsync(Entry, kind, force)).ConfigureAwait(false);
            if (path is not null)
            {
                if (force) ImageCache.Invalidate(path);
                bmp = await ImageCache.Acquire(path, width).ConfigureAwait(false);
            }
        }
        catch (Exception ex) { Log.Error("load art", ex); }

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (generation != _generation)
            {
                // Images were released while this was loading; give the bitmap straight back.
                ImageCache.Release(bmp);
                SetLoading(kind, false);
                return;
            }
            Bitmap? old = null;
            switch (kind)
            {
                case ArtKind.Cover:
                    _coverLoading = false;
                    if (bmp is null) _coverMissing = true; else { old = _cover; _cover = bmp; _coverW = width; }
                    OnPropertyChanged(nameof(Cover));
                    OnPropertyChanged(nameof(HasCover));
                    OnPropertyChanged(nameof(CoverFits));
                    OnPropertyChanged(nameof(CoverLetterboxed));
                    OnPropertyChanged(nameof(ShowCoverPlaceholder));
                    OnPropertyChanged(nameof(CoverFallbackIcon));
                    OnPropertyChanged(nameof(ShowInitials));
                    OnPropertyChanged(nameof(Backdrop));
                    break;
                case ArtKind.Icon:
                    _iconLoading = false;
                    if (bmp is null) _iconMissing = true; else { old = _icon; _icon = bmp; _iconW = width; }
                    OnPropertyChanged(nameof(Icon));
                    OnPropertyChanged(nameof(HasIcon));
                    OnPropertyChanged(nameof(ShowIconPlaceholder));
                    OnPropertyChanged(nameof(CoverFallbackIcon));
                    OnPropertyChanged(nameof(ShowInitials));
                    OnPropertyChanged(nameof(ShowIconInitials));
                    break;
                default:
                    _heroLoading = false;
                    if (bmp is null) _heroMissing = true; else { old = _hero; _hero = bmp; _heroW = width; }
                    OnPropertyChanged(nameof(Hero));
                    OnPropertyChanged(nameof(Backdrop));
                    break;
            }
            // Give back the version that was replaced (or the duplicate reference when the same bitmap came back).
            ImageCache.Release(old);
            if (bmp is not null) _host.NoteImagesLoaded(this);
        });
    }

    private void SetLoading(ArtKind kind, bool value)
    {
        switch (kind)
        {
            case ArtKind.Cover: _coverLoading = value; break;
            case ArtKind.Icon: _iconLoading = value; break;
            default: _heroLoading = value; break;
        }
    }

    public bool HasImages => _cover is not null || _icon is not null || _hero is not null;

    /// <summary>Drops decoded images (they reload when shown again). Used to keep memory low.</summary>
    public void ReleaseImages()
    {
        _generation++;
        _coverLoading = _iconLoading = _heroLoading = false;
        if (_cover is null && _icon is null && _hero is null) return;
        var (c, i, h) = (_cover, _icon, _hero);
        _cover = _icon = _hero = null;
        _coverW = _iconW = _heroW = 0;
        RaiseImages();          // controls let go of the bitmaps first…
        ImageCache.Release(c);  // …then they can be freed
        ImageCache.Release(i);
        ImageCache.Release(h);
    }

    /// <summary>Re-resolves artwork (after the user changed an image or asked for a refresh).</summary>
    public void ReloadImages(bool force)
    {
        ReleaseImages();
        _coverMissing = _iconMissing = _heroMissing = false;
        if (force)
        {
            _coverLoading = _iconLoading = _heroLoading = true;
            _ = LoadAsync(ArtKind.Cover, _host.DecodeWidth(ArtKind.Cover), true);
            _ = LoadAsync(ArtKind.Icon, _host.DecodeWidth(ArtKind.Icon), true);
            _ = LoadAsync(ArtKind.Hero, _host.DecodeWidth(ArtKind.Hero), true);
        }
        RaiseImages();
    }

    /// <summary>Asks bindings to read the images again (after resuming).</summary>
    public void ResumeImages() => RaiseImages();

    private void RaiseImages()
    {
        foreach (var p in new[] { nameof(Cover), nameof(Icon), nameof(Hero), nameof(Backdrop), nameof(HasCover), nameof(HasIcon), nameof(CoverFits), nameof(CoverLetterboxed),
                     nameof(ShowCoverPlaceholder), nameof(ShowIconPlaceholder), nameof(CoverFallbackIcon), nameof(ShowInitials), nameof(ShowIconInitials) })
            OnPropertyChanged(p);
    }

    /// <summary>Refreshes text properties after the entry changed.</summary>
    public void Refresh()
    {
        _placeholder = null;
        foreach (var p in new[] { nameof(Name), nameof(SortName), nameof(Kind), nameof(SourceLabel), nameof(SourceFull), nameof(IsFavorite),
                     nameof(IsMissing), nameof(PlaytimeText), nameof(LastPlayedText), nameof(HasPlaytime), nameof(FavoriteMenuText),
                     nameof(Initials), nameof(Placeholder), nameof(InstallDir), nameof(HasFolder), nameof(CanUninstall), nameof(SizeText), nameof(HasSize) })
            OnPropertyChanged(p);
    }

    // ------------------------------------------------------------------ commands

    [RelayCommand] private void Play() => _host.Play(this);
    [RelayCommand] private Task ForceStop() => _host.ForceStop(this);
    [RelayCommand] private void OpenFolder() => _host.OpenFolder(this);
    [RelayCommand] private Task Edit() => _host.Edit(this);
    [RelayCommand] private void ToggleFavorite() => _host.ToggleFavorite(this);
    [RelayCommand] private Task Uninstall() => _host.Uninstall(this);
    [RelayCommand] private Task Remove() => _host.Remove(this);
    [RelayCommand] private Task ChangeCover() => _host.ChangeImage(this, ArtKind.Cover);
    [RelayCommand] private Task ChangeIcon() => _host.ChangeImage(this, ArtKind.Icon);
    [RelayCommand] private Task ChangeHero() => _host.ChangeImage(this, ArtKind.Hero);
    [RelayCommand] private void RefreshArtwork() => _host.RefreshArtwork(this);
    [RelayCommand] private void Select() => _host.Select(this);

    // ------------------------------------------------------------------ formatting

    public static string FormatPlaytime(long seconds)
    {
        if (seconds <= 0) return "Not played";
        var t = TimeSpan.FromSeconds(seconds);
        if (t.TotalHours >= 1) return $"{t.TotalHours:0.#} h";
        return $"{Math.Max(1, (int)t.TotalMinutes)} min";
    }

    public static string FormatAgo(DateTime utc)
    {
        var d = DateTime.UtcNow - utc;
        if (d.TotalMinutes < 2) return "Just now";
        if (d.TotalHours < 1) return $"{(int)d.TotalMinutes} min ago";
        if (d.TotalDays < 1) return $"{(int)d.TotalHours} h ago";
        if (d.TotalDays < 2) return "Yesterday";
        if (d.TotalDays < 30) return $"{(int)d.TotalDays} days ago";
        return utc.ToLocalTime().ToString("d MMM yyyy");
    }
}
