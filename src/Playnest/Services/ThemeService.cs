using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Playnest.Models;

namespace Playnest.Services;

/// <summary>Applies appearance settings live (theme, accent colour, corners, fonts) by updating app resources.</summary>
public static class ThemeService
{
    public static readonly string[] AccentPresets =
    [
        "#7C5CFF", "#0A84FF", "#00B7C3", "#10B981", "#84CC16", "#F59E0B", "#F97316", "#EF4444", "#EC4899", "#A855F7", "#64748B", "#E5E7EB",
    ];

    public static void Apply(AppSettings s)
    {
        var app = Application.Current;
        if (app is null) return;

        app.RequestedThemeVariant = s.Theme switch
        {
            ThemeMode.Dark => ThemeVariant.Dark,
            ThemeMode.Light => ThemeVariant.Light,
            _ => ThemeVariant.Default,
        };

        var accent = ParseColor(s.AccentColor, Color.Parse("#7C5CFF"));
        var r = app.Resources;
        r["SystemAccentColor"] = accent;
        r["SystemAccentColorLight1"] = Shift(accent, 0.15);
        r["SystemAccentColorLight2"] = Shift(accent, 0.30);
        r["SystemAccentColorLight3"] = Shift(accent, 0.45);
        r["SystemAccentColorDark1"] = Shift(accent, -0.15);
        r["SystemAccentColorDark2"] = Shift(accent, -0.30);
        r["SystemAccentColorDark3"] = Shift(accent, -0.45);
        r["AccentBrush"] = new SolidColorBrush(accent);
        r["AccentSoftBrush"] = new SolidColorBrush(Color.FromArgb(0x33, accent.R, accent.G, accent.B));
        r["AccentForegroundBrush"] = new SolidColorBrush(Luminance(accent) > 0.6 ? Colors.Black : Colors.White);

        var radius = Math.Clamp(s.CornerRadius, 0, 24);
        r["ControlCornerRadius"] = new CornerRadius(Math.Min(radius, 8));
        r["OverlayCornerRadius"] = new CornerRadius(radius);
        r["TileCornerRadius"] = new CornerRadius(radius);
        r["CardCornerRadius"] = new CornerRadius(Math.Min(radius + 2, 26));

        var fontSize = Math.Clamp(s.FontSize, 11, 20);
        r["ControlContentThemeFontSize"] = fontSize;
        r["AppFontSize"] = fontSize;
        r["AppFontSizeSmall"] = fontSize - 2;
        r["AppFontSizeLarge"] = fontSize + 6;
        r["AppFontFamily"] = string.IsNullOrWhiteSpace(s.FontFamily)
            ? new FontFamily("Segoe UI Variable Text, Segoe UI, Inter, sans-serif")
            : new FontFamily(s.FontFamily + ", Segoe UI, sans-serif");
    }

    /// <summary>Tile sizes and visibility flags used by the library templates (DynamicResource is cheap per tile).</summary>
    public static void ApplyLayout(AppSettings s)
    {
        var app = Application.Current;
        if (app is null) return;
        var r = app.Resources;
        r["CoverWidth"] = s.CoverWidth;
        r["CoverHeight"] = Math.Round(s.CoverWidth * 1.5);
        r["IconSize"] = s.IconSize;
        r["IconTileWidth"] = s.IconSize + 36;
        r["TileMargin"] = new Thickness(0, 0, s.TileSpacing, s.TileSpacing);
        r["ShowTitles"] = s.ShowTitles;
        r["ShowBadges"] = s.ShowSourceBadges;
        r["ShowTilePlaytime"] = s.ShowPlaytimeOnTiles;
        r["ShowTileSize"] = s.ShowSizeOnTiles;
        r["ShowSidebarText"] = !s.CompactSidebar;
        r["ShowSidebarCounts"] = s.ShowSidebarCounts && !s.CompactSidebar;
    }

    public static Color ParseColor(string? hex, Color fallback)
    {
        if (!string.IsNullOrWhiteSpace(hex) && Color.TryParse(hex.Trim(), out var c)) return c;
        return fallback;
    }

    /// <summary>Lightens (amount &gt; 0) or darkens (amount &lt; 0) a colour.</summary>
    public static Color Shift(Color c, double amount)
    {
        static byte Mix(byte v, double a) => (byte)Math.Clamp(a >= 0 ? v + (255 - v) * a : v * (1 + a), 0, 255);
        return Color.FromArgb(c.A, Mix(c.R, amount), Mix(c.G, amount), Mix(c.B, amount));
    }

    public static double Luminance(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;

    /// <summary>Backdrop list handed to the window. Avalonia picks the first one the OS supports.</summary>
    public static WindowTransparencyLevel[] TransparencyFor(BackdropMode mode, bool lowMemory) =>
        lowMemory ? [WindowTransparencyLevel.None] : mode switch
        {
            BackdropMode.Mica => [WindowTransparencyLevel.Mica, WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.None],
            BackdropMode.Acrylic => [WindowTransparencyLevel.AcrylicBlur, WindowTransparencyLevel.Blur, WindowTransparencyLevel.None],
            _ => [WindowTransparencyLevel.None],
        };

    /// <summary>A pleasant gradient for games without artwork, stable per name.</summary>
    public static IBrush PlaceholderBrush(string name)
    {
        uint h = 2166136261;
        foreach (var ch in name) h = (h ^ ch) * 16777619;
        double hue = h % 360;
        var a = FromHsv(hue, 0.55, 0.55);
        var b = FromHsv((hue + 40) % 360, 0.65, 0.30);
        return new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(a, 0), new GradientStop(b, 1) },
        };
    }

    private static Color FromHsv(double h, double s, double v)
    {
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        (double r, double g, double b) = h switch
        {
            < 60 => (c, x, 0d), < 120 => (x, c, 0d), < 180 => (0d, c, x),
            < 240 => (0d, x, c), < 300 => (x, 0d, c), _ => (c, 0d, x),
        };
        return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }
}
