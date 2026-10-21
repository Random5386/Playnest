using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Playnest.Models;

namespace Playnest.ViewModels;

/// <summary>Things only the window can do (dialogs, pickers, hiding). Implemented by the main window.</summary>
public interface IUiService
{
    double RenderScaling { get; }
    Task<string?> PickFile(string title, string patternName, string[] patterns);
    Task<string?> PickFolder(string title);
    Task<string?> PickSaveFile(string title, string suggestedName, string patternName, string[] patterns);
    Task<bool> Confirm(string title, string message, string confirmText, bool danger = false);
    Task<bool> EditGame(EditGameViewModel vm);
    void HideForGame(LaunchBehavior behavior);
    void RestoreAfterGame();
    void ApplyWindowSettings();
    bool IsHiddenToTray { get; }
}

public enum AppPage { Library, Launchers, Settings }

/// <summary>One row of the virtualized grid (a few tiles side by side).</summary>
public sealed class GameRow(IReadOnlyList<GameViewModel> items)
{
    public IReadOnlyList<GameViewModel> Items { get; } = items;
}

public sealed partial class SidebarItem : ObservableObject
{
    public required string Key { get; init; }
    public required string Label { get; init; }
    public required string Glyph { get; init; }
    [ObservableProperty] public partial int Count { get; set; }
    [ObservableProperty] public partial bool IsSelected { get; set; }
    public bool IsHeader { get; init; }
}

public sealed partial class Toast : ObservableObject
{
    public required string Message { get; init; }
    public string? ActionText { get; init; }
    public Action? Action { get; init; }
    public bool HasAction => ActionText is not null;
    public bool IsError { get; init; }

    [RelayCommand]
    private void RunAction() => Action?.Invoke();
}

public sealed partial class IgnoredItemViewModel(IgnoredItem item, Action<IgnoredItemViewModel> restore) : ObservableObject
{
    public IgnoredItem Item { get; } = item;
    public string Name => Item.Name;
    public string Detail => Item.InstallDir ?? Item.Id;

    [RelayCommand]
    private void Restore() => restore(this);
}

public sealed partial class FolderItemViewModel(string path, Action<FolderItemViewModel> remove) : ObservableObject
{
    public string Path { get; } = path;

    [RelayCommand]
    private void Remove() => remove(this);
}

public sealed partial class AccentSwatch(string hex, Action<string> pick) : ObservableObject
{
    public string Hex { get; } = hex;
    public Avalonia.Media.IBrush Brush { get; } = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.Parse(hex));

    [RelayCommand]
    private void Pick() => pick(Hex);
}

/// <summary>Edit / add dialog data.</summary>
public sealed partial class EditGameViewModel : ObservableObject
{
    private readonly IUiService _ui;

    public EditGameViewModel(GameEntry e, IUiService ui, bool isNew)
    {
        _ui = ui;
        IsNew = isNew;
        Name = e.DisplayName;
        KindIndex = (int)e.EffectiveKind;
        ExePath = e.EffectiveExe ?? "";
        Arguments = e.EffectiveArgs ?? "";
        WorkingDir = e.EffectiveWorkingDir ?? "";
        LaunchUri = e.EffectiveUri ?? "";
        RunAsAdmin = e.RunAsAdmin;
        CoverPath = e.CustomCover ?? "";
        IconPath = e.CustomIcon ?? "";
        HeroPath = e.CustomHero ?? "";
        SourceText = GameSourceInfo.DisplayName(e.Source);
        InstallDir = e.EffectiveDir ?? "—";
        IsStoreItem = !e.IsManual && e.Source is not (GameSource.Folder or GameSource.Registry or GameSource.System);
    }

    public bool IsNew { get; }
    public string Title => IsNew ? "Add to library" : "Edit";
    public string SourceText { get; }
    public string InstallDir { get; }
    public bool IsStoreItem { get; }
    public string[] Kinds { get; } = ["Game", "Store launcher", "Gaming tool"];

    [ObservableProperty] public partial string Name { get; set; }
    [ObservableProperty] public partial int KindIndex { get; set; }
    [ObservableProperty] public partial string ExePath { get; set; }
    [ObservableProperty] public partial string Arguments { get; set; }
    [ObservableProperty] public partial string WorkingDir { get; set; }
    [ObservableProperty] public partial string LaunchUri { get; set; }
    [ObservableProperty] public partial bool RunAsAdmin { get; set; }
    [ObservableProperty] public partial string CoverPath { get; set; }
    [ObservableProperty] public partial string IconPath { get; set; }
    [ObservableProperty] public partial string HeroPath { get; set; }

    private static readonly string[] Images = ["*.png", "*.jpg", "*.jpeg", "*.webp", "*.ico", "*.exe"];

    [RelayCommand]
    private async Task BrowseExe()
    {
        var p = await _ui.PickFile("Choose program", "Programs", ["*.exe", "*.lnk", "*.url", "*.bat", "*.cmd"]);
        if (p is not null) ExePath = p;
    }

    [RelayCommand]
    private async Task BrowseWorkingDir()
    {
        var p = await _ui.PickFolder("Choose working folder");
        if (p is not null) WorkingDir = p;
    }

    [RelayCommand]
    private async Task BrowseCover()
    {
        var p = await _ui.PickFile("Choose cover image (portrait works best)", "Images", Images);
        if (p is not null) CoverPath = p;
    }

    [RelayCommand]
    private async Task BrowseIcon()
    {
        var p = await _ui.PickFile("Choose icon (image, .ico or .exe)", "Images", Images);
        if (p is not null) IconPath = p;
    }

    [RelayCommand]
    private async Task BrowseHero()
    {
        var p = await _ui.PickFile("Choose background image (wide works best)", "Images", Images);
        if (p is not null) HeroPath = p;
    }

    [RelayCommand] private void ClearCover() => CoverPath = "";
    [RelayCommand] private void ClearIcon() => IconPath = "";
    [RelayCommand] private void ClearHero() => HeroPath = "";
}
