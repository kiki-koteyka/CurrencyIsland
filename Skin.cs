using System;
using System.Windows.Media;

namespace DynamicIsland;

// Palette switch for the Colorful (Design 0) and Minimal (Design 2) islands.
// Colorful keeps the 1.9.0 look untouched; Minimal is the same layout in a
// neutral palette, dark or light, with colour reserved for up/down moves.
internal static class Skin
{
    public static readonly bool Minimal = AppSettings.Load().Design == 2;

    public static readonly bool Light = Minimal && ResolveLight(AppSettings.Load().IslandTheme);

    private static bool ResolveLight(int theme)
    {
        if (theme == 1) return true;
        if (theme != 2) return false;
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is int value && value == 1;
        }
        catch
        {
            return false;
        }
    }

    private static readonly Color ColorfulBase = Color.FromRgb(0x1C, 0x1C, 0x1E);
    private static readonly Color DarkBase = Color.FromRgb(0x10, 0x10, 0x12);
    private static readonly Color LightBase = Color.FromRgb(0xFA, 0xFA, 0xF9);
    private static readonly Color DarkInk = Color.FromRgb(0xF2, 0xF2, 0xF3);
    private static readonly Color LightInk = Color.FromRgb(0x16, 0x16, 0x16);

    public static Color Base => !Minimal ? ColorfulBase : Light ? LightBase : DarkBase;

    private static Color RawInk => Light ? LightInk : DarkInk;

    public static Color InkSolid => Minimal ? RawInk : Colors.White;

    public static Brush ShellBrush { get; } = SoftTint.Frozen(new SolidColorBrush(!Minimal ? Colors.Black : Light ? LightBase : DarkBase));

    public static Brush InkBrush { get; } = SoftTint.Frozen(new SolidColorBrush(Minimal ? (Light ? LightInk : DarkInk) : Colors.White));

    public static Brush BaseBrush { get; } = SoftTint.Frozen(new SolidColorBrush(!Minimal ? ColorfulBase : Light ? LightBase : DarkBase));

    public static Brush RingBrush => Minimal ? ShellBrush : Brushes.White;

    public static Brush BadgeBrush => Minimal ? InkBrush : Brushes.White;

    public static Color BadgeText => Minimal ? Base : Color.FromRgb(17, 17, 17);

    public static Color Accent => Minimal ? RawInk : Wpf.Ui.Appearance.ApplicationAccentColorManager.SystemAccent;

    // Text and glyph colour at a given alpha. Light text on dark, dark on light.
    public static Color Ink(byte alpha)
    {
        if (!Minimal) return Color.FromArgb(alpha, 235, 235, 240);
        // Text must stay readable: anything that is not a faint hairline-level veil
        // is lifted to a near-full alpha so secondary labels never fade out.
        var lifted = alpha < 60 ? alpha : (byte)Math.Min(255, 175 + alpha * 0.32);
        var c = RawInk;
        if (Light) return Color.FromArgb(lifted, c.R, c.G, c.B);
        return Color.FromArgb(lifted, 235, 235, 240);
    }

    // The pure-white variants (icons, strokes) of the same idea.
    public static Color InkColor(byte alpha)
    {
        var c = Minimal ? RawInk : Colors.White;
        return Color.FromArgb(alpha, c.R, c.G, c.B);
    }

    // A translucent overlay that lifts a surface: white on dark, a faint ink veil on light.
    public static Color Wash(byte alpha)
    {
        if (!Minimal) return Color.FromArgb(alpha, 255, 255, 255);
        if (Light) return Color.FromArgb((byte)Math.Min(255, alpha * 0.5), LightInk.R, LightInk.G, LightInk.B);
        return Color.FromArgb((byte)Math.Min(255, alpha * 0.8), 255, 255, 255);
    }

    public static Color ShellA(byte alpha) => !Minimal ? Color.FromArgb(alpha, 12, 12, 14) : Color.FromArgb(alpha, Base.R, Base.G, Base.B);

    public static Color BaseA(byte alpha) => Color.FromArgb(alpha, Base.R, Base.G, Base.B);

    public static Color Hairline => Light ? Color.FromRgb(0xE6, 0xE6, 0xE3) : Color.FromArgb(24, 255, 255, 255);

    // Series colours. Colorful keeps its hues; Minimal uses three greys that are
    // told apart by line weight and dash instead.
    public static Color Usd => !Minimal ? Color.FromRgb(0x5F, 0xD0, 0x68) : Light ? Color.FromRgb(0x16, 0x16, 0x16) : Color.FromRgb(0xF2, 0xF2, 0xF3);
    public static Color Eur => !Minimal ? Color.FromRgb(0x5A, 0xC8, 0xFA) : Light ? Color.FromRgb(0x55, 0x55, 0x5A) : Color.FromRgb(0xC2, 0xC2, 0xC8);
    public static Color Cny => !Minimal ? Color.FromRgb(0xFF, 0xD1, 0x66) : Light ? Color.FromRgb(0x86, 0x86, 0x8B) : Color.FromRgb(0x9C, 0x9C, 0xA4);
    public static Color Aed => !Minimal ? Color.FromRgb(0xC3, 0x9B, 0xFF) : Light ? Color.FromRgb(0x55, 0x55, 0x5A) : Color.FromRgb(0xC2, 0xC2, 0xC8);
    public static Color Try => !Minimal ? Color.FromRgb(0xFF, 0x4D, 0x5E) : Light ? Color.FromRgb(0x86, 0x86, 0x8B) : Color.FromRgb(0x9C, 0x9C, 0xA4);
    public static Color TrendUp => !Minimal ? Color.FromRgb(0x30, 0xD1, 0x58) : Light ? Color.FromRgb(0x2F, 0x7D, 0x4F) : Color.FromRgb(0x6F, 0xBF, 0x8A);
    public static Color TrendDown => !Minimal ? Color.FromRgb(0xFF, 0x45, 0x3A) : Light ? Color.FromRgb(0xB4, 0x42, 0x4A) : Color.FromRgb(0xD8, 0x70, 0x7A);
}
