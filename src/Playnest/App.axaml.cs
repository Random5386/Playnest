using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using Playnest.Models;
using Playnest.Services;
using Playnest.Services.Platform;
using Playnest.ViewModels;
using Playnest.Views;

namespace Playnest;

public partial class App : Application
{
    public static AppSettings Settings { get; set; } = new();
    public static bool StartHidden { get; set; }
    public static SingleInstance? Single { get; set; }

    private MainWindow? _window;
    private MainViewModel? _vm;
    private TrayIcon? _tray;
    private IClassicDesktopStyleApplicationLifetime? _desktop;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            _desktop = desktop;
            ThemeService.Apply(Settings);
            ThemeService.ApplyLayout(Settings);

            _window = new MainWindow();
            _vm = new MainViewModel(Settings, _window);
            _window.Attach(_vm, Shutdown);
            _vm.Initialize();
            _vm.SettingsPage.RefreshIgnored();
            SettingsViewModel.ApplyStartup(Settings);

            SetupTray();
            desktop.MainWindow = _window;
            desktop.ShutdownRequested += (_, _) => _vm.FlushOnExit();
            desktop.Exit += (_, _) => { _tray?.Dispose(); Single?.Dispose(); };
            Single?.ListenForActivation(() => Dispatcher.UIThread.Post(ShowMainWindow));

            if (StartHidden)
            {
                // Stay in the tray; nothing is rendered until the window is opened.
                _vm.SuspendImages();
            }
            else
            {
                _window.Show();
            }

            // After start-up work settles, give unused memory back to Windows.
            DispatcherTimer.RunOnce(Native.TrimWorkingSet, TimeSpan.FromSeconds(10));
        }
        base.OnFrameworkInitializationCompleted();
    }

    private void SetupTray()
    {
        try
        {
            using var icon = AssetLoader.Open(new Uri("avares://Playnest/Assets/playnest.ico"));
            _tray = new TrayIcon
            {
                Icon = new WindowIcon(icon),
                ToolTipText = "Playnest",
                Menu = new NativeMenu(),
            };
            _tray.Clicked += (_, _) => ShowMainWindow();
            _tray.Menu.NeedsUpdate += (_, _) => RebuildTrayMenu();
            RebuildTrayMenu();
            TrayIcon.SetIcons(this, [_tray]);
        }
        catch (Exception ex)
        {
            Log.Error("tray", ex);
        }
    }

    private void RebuildTrayMenu()
    {
        if (_tray?.Menu is not { } menu || _vm is null) return;
        menu.Items.Clear();

        var open = new NativeMenuItem("Open Playnest");
        open.Click += (_, _) => ShowMainWindow();
        menu.Items.Add(open);
        menu.Items.Add(new NativeMenuItemSeparator());

        var recent = _vm.TrayGames().ToList();
        foreach (var g in recent)
        {
            var item = new NativeMenuItem("Play " + g.Name);
            var game = g;
            item.Click += (_, _) => _vm.Play(game);
            menu.Items.Add(item);
        }
        if (recent.Count > 0) menu.Items.Add(new NativeMenuItemSeparator());

        var scan = new NativeMenuItem("Scan for games");
        scan.Click += (_, _) => _ = _vm.ScanAsync(quiet: false);
        menu.Items.Add(scan);
        var settings = new NativeMenuItem("Settings");
        settings.Click += (_, _) => { ShowMainWindow(); _vm.CurrentPage = AppPage.Settings; };
        menu.Items.Add(settings);
        menu.Items.Add(new NativeMenuItemSeparator());
        var exit = new NativeMenuItem("Exit");
        exit.Click += (_, _) => Shutdown();
        menu.Items.Add(exit);
    }

    public void ShowMainWindow()
    {
        if (_window is null) return;
        _vm?.ResumeImages();
        if (!_window.IsVisible) _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = Settings.WindowMaximized ? WindowState.Maximized : WindowState.Normal;
        _window.Activate();
        _window.Topmost = true;
        _window.Topmost = false;
    }

    private void Shutdown()
    {
        _vm?.FlushOnExit();
        _desktop?.Shutdown();
    }
}
