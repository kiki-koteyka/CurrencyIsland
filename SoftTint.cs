using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DynamicIsland;

internal static class SoftTint
{
    public static Color Base => Skin.Base;

    public static Color Mix(Color from, Color to, double amount) => Color.FromRgb(
        (byte)Math.Round(from.R + (to.R - from.R) * amount),
        (byte)Math.Round(from.G + (to.G - from.G) * amount),
        (byte)Math.Round(from.B + (to.B - from.B) * amount));

    public static Color Light(Color accent) => Skin.Minimal ? Skin.InkSolid : Mix(accent, Colors.White, 0.55);

    public static Brush Frozen(Brush brush)
    {
        brush.Freeze();
        return brush;
    }

    public static Brush ShellRim() => Skin.Minimal
        ? Frozen(new SolidColorBrush(Skin.Hairline))
        : Frozen(new LinearGradientBrush(Color.FromArgb(50, 255, 255, 255), Color.FromArgb(18, 255, 255, 255), 90));

    public static Brush TileRim() => Skin.Minimal
        ? Frozen(new SolidColorBrush(Skin.Hairline))
        : Frozen(new LinearGradientBrush(Color.FromArgb(46, 255, 255, 255), Color.FromArgb(14, 255, 255, 255), 90));

    // Minimal replaces every accent-tinted gradient with one flat neutral veil:
    // a faint one at rest, a slightly stronger one for selected or hovered tiles.
    private static double FlatAmount(double strength) => (strength >= 0.5 ? 0.22 : strength >= 0.3 ? 0.11 : 0.07) * (Skin.Light ? 0.75 : 1);

    private static Color FlatTint(double strength) => Mix(Base, Skin.InkSolid, FlatAmount(strength));

    public static Brush Tint(Color accent, double from = 0.34, double to = 0.14) => Skin.Minimal
        ? Frozen(new SolidColorBrush(FlatTint(from)))
        : Frozen(new LinearGradientBrush(Mix(Base, accent, from), Mix(Base, accent, to), new Point(0, 0), new Point(1, 1)));

    public static Brush Glass(byte alpha) => Frozen(new SolidColorBrush(Skin.Wash(alpha)));

    public sealed class TintHover
    {
        private readonly GradientStop _a = new();
        private readonly GradientStop _b = new() { Offset = 1 };
        private readonly double _restFrom, _restTo, _hoverFrom, _hoverTo;
        private Color _restA, _restB, _hoverA, _hoverB;

        public LinearGradientBrush Brush { get; }

        public TintHover(Color accent, double restFrom = 0.34, double restTo = 0.14, double hoverFrom = 0.48, double hoverTo = 0.24)
        {
            _restFrom = restFrom;
            _restTo = restTo;
            _hoverFrom = hoverFrom;
            _hoverTo = hoverTo;
            Brush = new LinearGradientBrush(new GradientStopCollection { _a, _b }, new Point(0, 0), new Point(1, 1));
            SetAccent(accent);
        }

        public void SetAccent(Color accent)
        {
            if (Skin.Minimal)
            {
                _restA = _restB = FlatTint(_restFrom);
                _hoverA = _hoverB = Mix(Base, Skin.InkSolid, Skin.Light ? 0.10 : 0.12);
            }
            else
            {
                _restA = Mix(Base, accent, _restFrom);
                _restB = Mix(Base, accent, _restTo);
                _hoverA = Mix(Base, accent, _hoverFrom);
                _hoverB = Mix(Base, accent, _hoverTo);
            }
            _a.BeginAnimation(GradientStop.ColorProperty, null);
            _b.BeginAnimation(GradientStop.ColorProperty, null);
            _a.Color = _restA;
            _b.Color = _restB;
        }

        public void Hover(bool on)
        {
            var ms = on ? 110 : 200;
            _a.BeginAnimation(GradientStop.ColorProperty, Fade(on ? _hoverA : _restA, ms));
            _b.BeginAnimation(GradientStop.ColorProperty, Fade(on ? _hoverB : _restB, ms));
        }

        public void Attach(UIElement target)
        {
            target.MouseEnter += (_, _) => Hover(true);
            target.MouseLeave += (_, _) => Hover(false);
        }

        private static System.Windows.Media.Animation.ColorAnimation Fade(Color to, int ms) =>
            new(to, Motion.Span(ms)) { EasingFunction = Motion.OutStrong };
    }

    public static Border Tile(Color accent, double radius, Thickness padding, UIElement child, double from = 0.34, double to = 0.14) => new()
    {
        Background = Tint(accent, from, to),
        BorderBrush = TileRim(),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(radius),
        Padding = padding,
        Child = child
    };
}
