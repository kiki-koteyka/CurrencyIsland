using System;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace DynamicIsland;

public sealed class FoxProgress : Grid
{
    public const double Side = 128;

    private static BitmapSource? _logo;

    private readonly Rectangle _fill;
    private readonly Rectangle _shine;
    private readonly TranslateTransform _shineShift = new(-1, 0);
    private bool _shineOn;
    private readonly DateTime _started = DateTime.UtcNow;
    private double _target;
    private double _shown;
    private bool _subscribed;

    public event Action<double>? Frame;

    public static BitmapSource Logo
    {
        get
        {
            if (_logo != null) return _logo;
            using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("logo.png")!;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            _logo = image;
            return image;
        }
    }

    public FoxProgress(Brush trackBrush, Brush fillBrush)
    {
        Width = Side;
        Height = Side;
        SnapsToDevicePixels = false;

        var mask = new ImageBrush(Logo) { Stretch = Stretch.Uniform };
        mask.Freeze();
        Children.Add(new Rectangle { Fill = trackBrush, OpacityMask = mask });
        _fill = new Rectangle { Fill = fillBrush, OpacityMask = mask };
        Children.Add(_fill);

        var band = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0.3),
            EndPoint = new Point(1, 0.7),
            RelativeTransform = _shineShift
        };
        band.GradientStops.Add(new GradientStop(Color.FromArgb(0, 255, 255, 255), 0.35));
        band.GradientStops.Add(new GradientStop(Color.FromArgb(242, 255, 255, 255), 0.5));
        band.GradientStops.Add(new GradientStop(Color.FromArgb(0, 255, 255, 255), 0.65));
        _shine = new Rectangle { Fill = band, OpacityMask = mask, Visibility = Visibility.Collapsed, IsHitTestVisible = false };
        Children.Add(_shine);
        ApplyClip(0);

        Loaded += (_, _) => UpdateSubscription();
        Unloaded += (_, _) => UpdateSubscription();
        IsVisibleChanged += (_, _) => UpdateSubscription();
    }

    public void SetTarget(double value) => _target = Math.Clamp(value, 0, 1);

    public void SetShine(bool on)
    {
        _shineOn = on;
        _shine.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
    }

    public void Reset()
    {
        SetShine(false);
        _target = 0;
        _shown = 0;
        ApplyClip(0);
    }

    private void UpdateSubscription()
    {
        var want = IsLoaded && IsVisible;
        if (want == _subscribed) return;
        _subscribed = want;
        if (want) CompositionTarget.Rendering += OnRendering;
        else CompositionTarget.Rendering -= OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var delta = _target - _shown;
        _shown = Math.Abs(delta) < 0.0004 ? _target : _shown + delta * 0.16;
        ApplyClip(_shown);
        if (_shineOn) UpdateShine();
        Frame?.Invoke(_shown);
    }

    private void UpdateShine()
    {
        var cycle = ((DateTime.UtcNow - _started).TotalSeconds % 2.4) / 2.4;
        var active = Math.Clamp((cycle - 0.35) / 0.65, 0, 1);
        var eased = active * active * (3 - 2 * active);
        _shineShift.X = -0.75 + 1.5 * eased;
    }

    private void ApplyClip(double progress)
    {
        var top = Side * (1 - progress);
        var edge = Math.Min(progress, 1 - progress);
        var amplitude = 2.6 * Math.Min(1, edge * 18);
        var phase = (DateTime.UtcNow - _started).TotalSeconds * 3.2;

        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(new Point(0, top + amplitude * Math.Sin(-phase)), true, true);
            for (var x = 4.0; x <= Side; x += 4)
                context.LineTo(new Point(x, top + amplitude * Math.Sin(x / Side * 14.4 - phase)), true, false);
            context.LineTo(new Point(Side, Side + 4), true, false);
            context.LineTo(new Point(0, Side + 4), true, false);
        }
        geometry.Freeze();
        _fill.Clip = geometry;
    }
}
