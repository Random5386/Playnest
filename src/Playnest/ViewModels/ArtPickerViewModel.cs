using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Playnest.Services;
using Playnest.Services.Artwork;

namespace Playnest.ViewModels;

/// <summary>"Choose artwork from SteamGridDB" dialog: pick the game on SteamGridDB, then click a cover, background or icon.</summary>
public sealed partial class ArtPickerViewModel : ObservableObject, IDisposable
{
    private readonly MainViewModel _main;
    private readonly GameViewModel _game;
    private CancellationTokenSource? _loadCts;
    private bool _suppressGameChange;

    public ArtPickerViewModel(MainViewModel main, GameViewModel game, ArtKind kind)
    {
        _main = main;
        _game = game;
        SearchText = game.Name;
        Kind = kind;
    }

    public string Title => $"SteamGridDB artwork · {_game.Name}";
    public ObservableCollection<SgdbGame> Games { get; } = new();
    public ObservableCollection<ArtChoice> Images { get; } = new();

    [ObservableProperty] public partial string SearchText { get; set; }
    [ObservableProperty] public partial SgdbGame? SelectedGame { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsCover), nameof(IsHero), nameof(IsIcon))]
    public partial ArtKind Kind { get; set; }
    [ObservableProperty] public partial string Status { get; set; } = "";
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial bool CanRestore { get; set; }

    public bool IsCover => Kind == ArtKind.Cover;
    public bool IsHero => Kind == ArtKind.Hero;
    public bool IsIcon => Kind == ArtKind.Icon;
    private string KindLabel => Kind switch { ArtKind.Cover => "cover", ArtKind.Icon => "icon", _ => "background" };
    private double TileWidth => Kind switch { ArtKind.Cover => 150, ArtKind.Icon => 110, _ => 300 };
    private double TileHeight => Kind switch { ArtKind.Cover => 225, ArtKind.Icon => 110, _ => 97 };

    /// <summary>Called once the window is shown: finds the game on SteamGridDB and lists its artwork.</summary>
    public async Task StartAsync()
    {
        UpdateRestore();
        IsBusy = true;
        Status = "Looking for the game on SteamGridDB…";
        var match = await _main.Artwork.SgdbMatchAsync(_game.Entry);
        var results = await _main.Artwork.SgdbSearchAsync(SearchText);
        _suppressGameChange = true;
        Games.Clear();
        if (match is not null) Games.Add(match);
        foreach (var g in results) if (g.Id != match?.Id) Games.Add(g);
        SelectedGame = match ?? Games.FirstOrDefault();
        _suppressGameChange = false;
        IsBusy = false;
        if (SelectedGame is null)
        {
            Status = "SteamGridDB doesn't seem to list this game. Try a different name above.";
            return;
        }
        await LoadImagesAsync();
    }

    partial void OnSelectedGameChanged(SgdbGame? value)
    {
        if (!_suppressGameChange && value is not null) _ = LoadImagesAsync();
    }

    [RelayCommand]
    private async Task Search()
    {
        if (string.IsNullOrWhiteSpace(SearchText)) return;
        IsBusy = true;
        Status = "Searching…";
        var results = await _main.Artwork.SgdbSearchAsync(SearchText);
        IsBusy = false;
        _suppressGameChange = true;
        Games.Clear();
        foreach (var g in results) Games.Add(g);
        SelectedGame = Games.FirstOrDefault();
        _suppressGameChange = false;
        if (SelectedGame is null) { ClearImages(); Status = $"Nothing found for “{SearchText.Trim()}”."; return; }
        await LoadImagesAsync();
    }

    [RelayCommand]
    private Task ShowKind(string kind)
    {
        if (!Enum.TryParse<ArtKind>(kind, out var k) || k == Kind) return Task.CompletedTask;
        Kind = k;
        UpdateRestore();
        return LoadImagesAsync();
    }

    private async Task LoadImagesAsync()
    {
        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();
        ClearImages();
        if (SelectedGame is not { } game) return;
        IsBusy = true;
        Status = $"Loading {KindLabel}s for {game.Name}…";
        var list = await _main.Artwork.SgdbImagesAsync(game.Id, Kind);
        if (cts.IsCancellationRequested) return;
        IsBusy = false;
        if (list.Count == 0)
        {
            Status = $"SteamGridDB has no {KindLabel}s for {game.Name} yet.";
            return;
        }
        Status = $"{list.Count} {KindLabel}{(list.Count == 1 ? "" : "s")} · click one to use it";
        foreach (var img in list) Images.Add(new ArtChoice(img, TileWidth, TileHeight, Apply));

        // Previews load a few at a time; leaving the tab or closing the window stops the rest.
        using var gate = new SemaphoreSlim(4);
        await Task.WhenAll(Images.ToList().Select(async c =>
        {
            await gate.WaitAsync();
            try { if (!cts.IsCancellationRequested) await c.LoadThumbAsync(cts.Token); }
            finally { gate.Release(); }
        }));
    }

    private void ClearImages()
    {
        foreach (var c in Images) c.Dispose();
        Images.Clear();
    }

    private async Task Apply(ArtChoice choice)
    {
        if (IsBusy) return;
        IsBusy = true;
        var kind = Kind;
        Status = $"Downloading the {KindLabel}…";
        var bytes = await Task.Run(() => ArtworkService.SgdbDownloadAsync(choice.Image.Url));
        IsBusy = false;
        if (bytes is null || ArtworkOverrides.Apply(_game.Entry, kind, bytes) is null)
        {
            Status = $"That {KindLabel} couldn't be downloaded. Try another one.";
            return;
        }
        if (SelectedGame is { } g) _game.Entry.SgdbGameId = g.Id;
        foreach (var c in Images) c.IsCurrent = ReferenceEquals(c, choice);
        _main.OnArtworkReplaced(_game);
        UpdateRestore();
        Status = $"New {KindLabel} saved ✓ You can pick a cover, background and icon here.";
    }

    [RelayCommand]
    private void Restore()
    {
        if (!ArtworkOverrides.Restore(_game.Entry, Kind)) { UpdateRestore(); return; }
        foreach (var c in Images) c.IsCurrent = false;
        _main.OnArtworkReplaced(_game);
        UpdateRestore();
        Status = $"The previous {KindLabel} is back.";
    }

    private void UpdateRestore() => CanRestore = ArtworkOverrides.CanRestore(_game.Entry, Kind);

    public void Dispose()
    {
        _loadCts?.Cancel();
        ClearImages();
    }
}

/// <summary>One image in the picker.</summary>
public sealed partial class ArtChoice(SgdbImage image, double tileWidth, double tileHeight, Func<ArtChoice, Task> apply) : ObservableObject, IDisposable
{
    public SgdbImage Image { get; } = image;
    public double TileWidth { get; } = tileWidth;
    public double TileHeight { get; } = tileHeight;
    public string Info => Image.Width > 0 ? $"{Image.Width}×{Image.Height}{(Image.Style.Length > 0 ? " · " + Image.Style : "")}" : Image.Style;

    [ObservableProperty] public partial Bitmap? Thumb { get; set; }
    [ObservableProperty] public partial bool IsCurrent { get; set; }

    [RelayCommand]
    private Task Use() => apply(this);

    public async Task LoadThumbAsync(CancellationToken ct)
    {
        var bytes = await Task.Run(() => ArtworkService.SgdbDownloadAsync(Image.Thumb)).ConfigureAwait(false);
        if (bytes is null || ct.IsCancellationRequested) return;
        Bitmap? bmp = null;
        try
        {
            using var ms = new MemoryStream(bytes);
            bmp = Bitmap.DecodeToWidth(ms, 320);
        }
        catch (Exception ex) { Log.Error("steamgriddb preview", ex); }
        if (bmp is null) return;
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (ct.IsCancellationRequested) bmp.Dispose();
            else Thumb = bmp;
        });
    }

    public void Dispose()
    {
        var t = Thumb;
        Thumb = null;
        t?.Dispose();
    }
}
