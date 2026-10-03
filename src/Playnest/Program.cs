using Avalonia;
using Playnest.Models;
using Playnest.Services;
using Playnest.Services.Platform;

namespace Playnest;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var dataArg = Array.IndexOf(args, "--data");
        if (dataArg >= 0 && dataArg + 1 < args.Length) AppPaths.Override(args[dataArg + 1]);

        using var single = new SingleInstance();
        if (!single.IsFirst)
        {
            single.SignalFirst(); // bring the running Playnest to the front
            return 0;
        }

        AppPaths.Ensure();
        var settings = Storage.LoadSettings();
        if (settings.SettingsVersion < 2)
        {
            settings.ShowSizeOnTiles = true; // game sizes are now shown on tiles by default
            settings.SettingsVersion = 2;
            Storage.SaveSettings(settings);
        }
        App.Settings = settings;
        App.StartHidden = args.Contains("--minimized") || settings.StartMinimized;
        App.Single = single;

        try
        {
            return BuildAvaloniaApp(settings).StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
        }
        catch (Exception ex)
        {
            Log.Error("fatal", ex);
            throw;
        }
    }

    // Used by the XAML previewer.
    public static AppBuilder BuildAvaloniaApp() => BuildAvaloniaApp(new AppSettings());

    public static AppBuilder BuildAvaloniaApp(AppSettings s) =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new Win32PlatformOptions
            {
                RenderingMode = s.GpuRendering
                    ? [Win32RenderingMode.AngleEgl, Win32RenderingMode.Software]
                    : [Win32RenderingMode.Software],
            })
            .With(new SkiaOptions { MaxGpuResourceSizeBytes = (s.LowMemoryMode ? 32L : 96L) * 1024 * 1024 })
            .LogToTrace();
}
