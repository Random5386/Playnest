using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Playnest.Models;
using Playnest.Services;
using Playnest.ViewModels;

namespace Playnest.Views;

public partial class MainWindow : Window, IUiService
{
    private MainViewModel? _vm;
    private Action? _shutdown;
    private bool _hiddenToTray;
    private Bitmap? _backgroundBitmap;

    public MainWindow()
    {
        InitializeComponent();
        // Smooth scaling for artwork (applies to every image in the window; icons are often scaled up or down).
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.HighQuality);
        TitleBar.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                if (e.ClickCount == 2) WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
                else BeginMoveDrag(e);
            }
        };
        Activated += (_, _) => _vm?.OnWindowActivated();
        KeyDown += OnKeyDown;
    }

    public void Attach(MainViewModel vm, Action shutdown)
    {
        _vm = vm;
        _shutdown = shutdown;
        DataContext = vm;
        var s = vm.Settings;
        Width = Math.Max(MinWidth, s.WindowWidth);
        Height = Math.Max(MinHeight, s.WindowHeight);
        if (s.WindowMaximized) WindowState = WindowState.Maximized;
        ApplyWindowSettings();
    }

    // ------------------------------------------------------------------ window behaviour

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_vm is null) return;
        RememberBounds();
        if (_vm.Settings.CloseToTray && !e.IsProgrammatic)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }
        if (!e.IsProgrammatic)
        {
            e.Cancel = true;
            _shutdown?.Invoke();
        }
    }

    private void RememberBounds()
    {
        if (_vm is null) return;
        var s = _vm.Settings;
        s.WindowMaximized = WindowState == WindowState.Maximized;
        if (WindowState == WindowState.Normal)
        {
            s.WindowWidth = Bounds.Width;
            s.WindowHeight = Bounds.Height;
        }
        _vm.SaveSettingsSoon();
    }

    private void HideToTray()
    {
        _hiddenToTray = true;
        Hide();
        _vm?.SuspendImages();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        _hiddenToTray = false;
    }

    public bool IsHiddenToTray => _hiddenToTray || !IsVisible;

    public void HideForGame(LaunchBehavior behavior)
    {
        switch (behavior)
        {
            case LaunchBehavior.Minimize:
                WindowState = WindowState.Minimized;
                break;
            case LaunchBehavior.MinimizeToTray:
                RememberBounds();
                HideToTray();
                break;
            case LaunchBehavior.Close:
                // Small delay so the launch request reaches the store first.
                Avalonia.Threading.DispatcherTimer.RunOnce(() => _shutdown?.Invoke(), TimeSpan.FromSeconds(2));
                break;
        }
    }

    public void RestoreAfterGame()
    {
        if (Application.Current is App app) app.ShowMainWindow();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_vm is null) return;
        bool ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        if (ctrl && e.Key == Key.F)
        {
            _vm.CurrentPage = AppPage.Library;
            LibraryPage.FocusSearch();
            e.Handled = true;
        }
        else if (e.Key == Key.F5)
        {
            _ = _vm.ScanAsync(quiet: false);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            if (!string.IsNullOrEmpty(_vm.SearchText)) _vm.SearchText = "";
            else _vm.SelectedGame = null;
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && _vm.IsLibraryPage && _vm.SelectedGame is not null && FocusManager?.GetFocusedElement() is not TextBox)
        {
            _vm.Play(_vm.SelectedGame);
            e.Handled = true;
        }
    }

    // ------------------------------------------------------------------ appearance

    double IUiService.RenderScaling
    {
        get
        {
            var s = RenderScaling;
            if (s <= 0 || !IsVisible) s = Screens?.Primary?.Scaling ?? 1.0;
            return Math.Clamp(s, 1.0, 3.0);
        }
    }

    public void ApplyWindowSettings()
    {
        if (_vm is null) return;
        var s = _vm.Settings;
        TransparencyLevelHint = ThemeService.TransparencyFor(s.Backdrop, s.LowMemoryMode);
        Classes.Set("noanim", !s.Animations || s.LowMemoryMode);

        bool transparent = ActualTransparencyLevel != WindowTransparencyLevel.None &&
                           s.Backdrop is BackdropMode.Mica or BackdropMode.Acrylic && !s.LowMemoryMode;
        SolidLayer.IsVisible = !transparent;

        // Custom background picture
        _backgroundBitmap?.Dispose();
        _backgroundBitmap = null;
        ImageLayer.Source = null;
        ImageLayer.IsVisible = false;
        if (s.Backdrop == BackdropMode.Image && !string.IsNullOrWhiteSpace(s.BackgroundImage) && File.Exists(s.BackgroundImage)
            && new FileInfo(s.BackgroundImage).Length < 60L * 1024 * 1024)
        {
            try
            {
                using var fs = File.OpenRead(s.BackgroundImage);
                var width = (int)Math.Min(2560, (Screens?.Primary?.Bounds.Width ?? 1920));
                _backgroundBitmap = Bitmap.DecodeToWidth(fs, width, BitmapInterpolationMode.MediumQuality);
                ImageLayer.Source = _backgroundBitmap;
                ImageLayer.Opacity = Math.Clamp(s.BackgroundImageOpacity, 0.05, 1);
                ImageLayer.IsVisible = true;
            }
            catch (Exception ex) { Log.Error("background image", ex); }
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ActualTransparencyLevelProperty && _vm is not null)
        {
            var s = _vm.Settings;
            bool transparent = ActualTransparencyLevel != WindowTransparencyLevel.None &&
                               s.Backdrop is BackdropMode.Mica or BackdropMode.Acrylic && !s.LowMemoryMode;
            SolidLayer.IsVisible = !transparent;
        }
    }

    // ------------------------------------------------------------------ dialogs & pickers

    public async Task<string?> PickFile(string title, string patternName, string[] patterns)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(patternName) { Patterns = patterns }, FilePickerFileTypes.All],
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickFolder(string title)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickSaveFile(string title, string suggestedName, string patternName, string[] patterns)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            FileTypeChoices = [new FilePickerFileType(patternName) { Patterns = patterns }],
        });
        return file?.TryGetLocalPath();
    }

    public async Task<bool> Confirm(string title, string message, string confirmText, bool danger = false)
    {
        EnsureVisibleForDialog();
        var dlg = new ConfirmWindow(title, message, confirmText, danger);
        return await dlg.ShowDialog<bool>(this);
    }

    public async Task<bool> EditGame(EditGameViewModel vm)
    {
        EnsureVisibleForDialog();
        var dlg = new EditGameWindow { DataContext = vm };
        return await dlg.ShowDialog<bool>(this);
    }

    private void EnsureVisibleForDialog()
    {
        if (!IsVisible && Application.Current is App app) app.ShowMainWindow();
    }
}
