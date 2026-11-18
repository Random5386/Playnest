using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Playnest.Models;
using Playnest.Services;
using Playnest.Services.Artwork;

namespace Playnest.ViewModels;

/// <summary>
/// "Choose artwork" dialog: shows the covers, icons or backgrounds SteamGridDB has for a game, so one can be picked
/// by hand. The chosen image is downloaded by <see cref="DownloadChoiceAsync"/> and saved by the caller as the
/// game's custom image (which "Replace all artwork" and rescans never touch).
/// </summary>
public sealed partial class ArtworkPickerViewModel : ObservableObject, IDisposable
{
    private readonly ArtworkService _art;
    private readonly GameEntry _entry;
    private readonly SemaphoreSlim _thumbGate = new(4);
    private CancellationTokenSource? _loadCts;
    private bool _disposed;

    public ArtworkPickerViewModel(GameEntry entry, ArtworkService art, ArtKind kind)
    {
        _entry = entry;
        _art = art;
        KindIndex = kind switch { ArtKind.Icon => 1, ArtKind.Hero => 2, _ => 0 };
        SearchText = entry.DisplayName;
    }

    public string Title => $"Choose artwork · {_entry.DisplayName}";
    public string[] Kinds { get; } = ["Cover", "Icon", "Background"];
    public bool HasKey => _art.HasSteamGridDbKey;

    public ObservableCollection<ArtworkPickerGame> Games { get; } = new();
    public ObservableCollection<ArtworkOption> Options { get; } = new();

    [ObservableProperty, NotifyPropertyChangedFor(nameof(Kind))]
    public partial int KindIndex { get; set; }

    [ObservableProperty] public partial string SearchText { get; set; }
    [ObservableProperty] public partial ArtworkPickerGame? SelectedGame { get; set; }
    [ObservableProperty] public partial string Status { get; set; } = "";
    [ObservableProperty] public partial bool IsBusy { get; set; }

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasChoice))]
    public partial ArtworkOption? Choice { get; set; }

    public bool HasChoice => Choice is not null;

    public ArtKind Kind => KindIndex switch { 1 => ArtKind.Icon, 2 => ArtKind.Hero, _ => ArtKind.Cover };

    /// <summary>Preview tile size for the current kind (covers are portrait, backgrounds wide, icons square).</summary>
    public double TileWidth => Kind switch { ArtKind.Icon => 120, ArtKind.Hero => 300, _ => 150 };
    public double TileHeight => Kind switch { ArtKind.Icon => 120, ArtKind.Hero => 97, _ => 225 };

    /// <summary>Finds the game on SteamGridDB and lists its images. Called once when the dialog opens.</summary>
    public async Task StartAsync()
    {
        if (!HasKey)
        {
            Status = "Add your SteamGridDB API key in Settings → Artwork to choose artwork from SteamGridDB.";
            return;
        }
        IsBusy = true;
        Status = "Looking for the game on SteamGridDB…";
        var match = await _art.FindSteamGridDbGameAsync(_entry);
        var results = await _art.SearchSteamGridDbAsync(_entry.DisplayName);
        if (_disposed) return;
        Games.Clear();
        if (match is not null) Games.Add(new ArtworkPickerGame(match));
        foreach (var g in results.Where(g => g.Id != match?.Id).Take(20)) Games.Add(new ArtworkPickerGame(g));
        IsBusy = false;
        if (Games.Count == 0)
        {
            Status = "SteamGridDB didn't find this game. Try another name and press Search.";
            return;
        }
        SelectedGame = Games[0];
    }

    [RelayCommand]
    private async Task Search()
    {
        if (!HasKey || string.IsNullOrWhiteSpace(SearchText)) return;
        IsBusy = true;
        Status = "Searching…";
        var results = await _art.SearchSteamGridDbAsync(SearchText);
        if (_disposed) return;
        IsBusy = false;
        Games.Clear();
        foreach (var g in results.Take(20)) Games.Add(new ArtworkPickerGame(g));
        if (Games.Count == 0)
        {
            SelectedGame = null;
            ClearOptions();
            Status = $"Nothing found for “{SearchText.Trim()}”.";
            return;
        }
        SelectedGame = Games[0];
    }

    partial void OnSelectedGameChanged(ArtworkPickerGame? value) => _ = LoadOptionsAsync();
    partial void OnKindIndexChanged(int value) => _ = LoadOptionsAsync();

    partial void OnChoiceChanged(ArtworkOption? oldValue, ArtworkOption? newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is not null) newValue.IsSelected = true;
    }

    private async Task LoadOptionsAsync()
    {
        _loadCts?.Cancel();
        ClearOptions();
        if (SelectedGame is not { } game || _disposed) return;
        var cts = _loadCts = new CancellationTokenSource();
        var kind = Kind;
        IsBusy = true;
        Status = "Loading artwork…";
        var images = await _art.SteamGridDbImagesAsync(game.Game.Id, kind);
        if (cts.IsCancellationRequested || _disposed) return;
        IsBusy = false;
        if (images.Count == 0)
        {
            Status = kind switch
            {
                ArtKind.Icon => "SteamGridDB has no icons for this game.",
                ArtKind.Hero => "SteamGridDB has no backgrounds for this game.",
                _ => "SteamGridDB has no covers for this game.",
            };
            return;
        }
        Status = images.Count == 1 ? "1 image. Click one, then Use this." : $"{images.Count} images. Click one, then Use this.";
        int decodeWidth = (int)TileWidth * 2;
        foreach (var img in images)
        {
            var option = new ArtworkOption(img, TileWidth, TileHeight);
            Options.Add(option);
            _ = LoadThumbAsync(option, decodeWidth, cts.Token);
        }
    }

    private async Task LoadThumbAsync(ArtworkOption option, int decodeWidth, CancellationToken ct)
    {
        await _thumbGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (ct.IsCancellationRequested) return;
            var bytes = await ArtworkService.DownloadSteamGridDbImageAsync(option.Image.Thumb).ConfigureAwait(false);
            if (bytes is null || ct.IsCancellationRequested) return;
            Bitmap? bmp = null;
            try
            {
                using var ms = new MemoryStream(bytes);
                bmp = Bitmap.DecodeToWidth(ms, Math.Clamp(decodeWidth, 64, 800), BitmapInterpolationMode.MediumQuality);
            }
            catch (Exception ex) { Log.Error("artwork preview", ex); }
            if (bmp is null) return;
            Dispatcher.UIThread.Post(() =>
            {
                // The list may have changed (another game/kind) or the dialog closed while this was decoding.
                if (ct.IsCancellationRequested || _disposed || !Options.Contains(option)) bmp.Dispose();
                else option.Thumb = bmp;
            });
        }
        finally { _thumbGate.Release(); }
    }

    private void ClearOptions()
    {
        Choice = null;
        foreach (var o in Options) o.DisposeThumb();
        Options.Clear();
    }

    /// <summary>Downloads the full-size chosen image (null if it failed).</summary>
    public async Task<byte[]?> DownloadChoiceAsync()
    {
        if (Choice is not { } c) return null;
        IsBusy = true;
        Status = "Downloading…";
        var bytes = await ArtworkService.DownloadSteamGridDbImageAsync(c.Image.Url);
        IsBusy = false;
        Status = bytes is null ? "That image couldn't be downloaded. Pick another one." : "";
        return bytes;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _loadCts?.Cancel();
        ClearOptions();
    }
}

/// <summary>A game in the picker's game list.</summary>
public sealed class ArtworkPickerGame(ArtworkService.SgdbGame game)
{
    public ArtworkService.SgdbGame Game { get; } = game;
    public string Label => Game.Label;
}

/// <summary>One image in the picker.</summary>
public sealed partial class ArtworkOption(ArtworkService.SgdbImage image, double tileWidth, double tileHeight) : ObservableObject
{
    public ArtworkService.SgdbImage Image { get; } = image;
    public double TileWidth { get; } = tileWidth;
    public double TileHeight { get; } = tileHeight;
    public string SizeText => Image.Width > 0 && Image.Height > 0 ? $"{Image.Width} × {Image.Height}" : "";

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsLoading))]
    public partial Bitmap? Thumb { get; set; }

    [ObservableProperty] public partial bool IsSelected { get; set; }

    public bool IsLoading => Thumb is null;

    public void DisposeThumb()
    {
        var t = Thumb;
        Thumb = null;
        t?.Dispose();
    }
}
