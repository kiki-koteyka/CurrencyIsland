using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DynamicIsland;

internal static class SoftTint
{
    public static readonly Color Base = Color.FromRgb(0x1C, 0x1C, 0x1E);

    public static Color Mix(Color from, Color to, double amount) => Color.FromRgb(
        (byte)Math.Round(from.R + (to.R - from.R) * amount),
        (byte)Math.Round(from.G + (to.G - from.G) * amount),
        (byte)Math.Round(from.B + (to.B - from.B) * amount));

    public static Color Light(Color accent) => Mix(accent, Colors.White, 0.55);

    public static Brush Frozen(Brush brush)
    {
        brush.Freeze();
        return brush;
    }

    public static Brush ShellRim() => Frozen(new LinearGradientBrush(Color.FromArgb(50, 255, 255, 255), Color.FromArgb(18, 255, 255, 255), 90));

    public static Brush TileRim() => Frozen(new LinearGradientBrush(Color.FromArgb(46, 255, 255, 255), Color.FromArgb(14, 255, 255, 255), 90));

    public static Brush Tint(Color accent, double from = 0.34, double to = 0.14) => Frozen(new LinearGradientBrush(
        Mix(Base, accent, from), Mix(Base, accent, to), new Point(0, 0), new Point(1, 1)));

    public static Brush Glass(byte alpha) => Frozen(new SolidColorBrush(Color.FromArgb(alpha, 255, 255, 255)));

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
            _restA = Mix(Base, accent, _restFrom);
            _restB = Mix(Base, accent, _restTo);
            _hoverA = Mix(Base, accent, _hoverFrom);
            _hoverB = Mix(Base, accent, _hoverTo);
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
