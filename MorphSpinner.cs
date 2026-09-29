using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;

namespace DynamicIsland;

public sealed class MorphSpinner : FrameworkElement
{
    private const int Samples = 96;
    private const double MsPerShape = 650;
    private const double ConstantRotationPerShape = 50;
    private const double ExtraRotationPerShape = 90;
    private const double Stiffness = 200;
    private const double DampingRatio = 0.6;

    private static readonly double[][] Shapes =
    {
        Radial(t => 0.88 + 0.12 * Math.Cos(10 * t)),
        Radial(t => 0.85 + 0.15 * Math.Cos(9 * t)),
        Radial(RoundedPolygon(5)),
        Radial(Superellipse(1.0, 0.62, 5)),
        Radial(t => 0.76 + 0.24 * Math.Cos(8 * t)),
        Radial(t => 0.82 + 0.18 * Math.Cos(4 * t)),
        Radial(Superellipse(1.0, 0.72, 2))
    };

    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(
        nameof(Foreground), typeof(Brush), typeof(MorphSpinner),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush Foreground
    {
        get => (Brush)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    private readonly Stopwatch _clock = new();
    private bool _running;

    public MorphSpinner()
    {
        Width = 28;
        Height = 28;
        IsHitTestVisible = false;
        Loaded += (_, _) => Sync();
        Unloaded += (_, _) => Sync();
        IsVisibleChanged += (_, _) => Sync();
    }

    private void Sync()
    {
        var shouldRun = IsLoaded && IsVisible;
        if (shouldRun && !_running)
        {
            _clock.Restart();
            CompositionTarget.Rendering += OnFrame;
            _running = true;
        }
        else if (!shouldRun && _running)
        {
            CompositionTarget.Rendering -= OnFrame;
            _running = false;
        }
    }

    private void OnFrame(object? sender, EventArgs e) => InvalidateVisual();

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0) return;

        var elapsedMs = _clock.Elapsed.TotalMilliseconds;
        var index = (int)(elapsedMs / MsPerShape);
        var local = elapsedMs % MsPerShape / MsPerShape;
        var morph = SpringStep(local * MsPerShape / 1000.0);

        var from = Shapes[index % Shapes.Length];
        var to = Shapes[(index + 1) % Shapes.Length];
        var rotation = ((ConstantRotationPerShape + ExtraRotationPerShape) * index
                        + ConstantRotationPerShape * local
                        + ExtraRotationPerShape * morph) % 360 * Math.PI / 180;

        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var radius = size / 2 * 0.94;

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            for (var k = 0; k < Samples; k++)
            {
                var r = from[k] + (to[k] - from[k]) * morph;
                var angle = 2 * Math.PI * k / Samples + rotation;
                var point = new Point(center.X + radius * r * Math.Cos(angle), center.Y + radius * r * Math.Sin(angle));
                if (k == 0) ctx.BeginFigure(point, true, true);
                else ctx.LineTo(point, false, true);
            }
        }
        geometry.Freeze();
        dc.DrawGeometry(Foreground, null, geometry);
    }

    private static double SpringStep(double seconds)
    {
        var omega = Math.Sqrt(Stiffness);
        var damped = omega * Math.Sqrt(1 - DampingRatio * DampingRatio);
        var decay = Math.Exp(-DampingRatio * omega * seconds);
        return 1 - decay * (Math.Cos(damped * seconds) + DampingRatio * omega / damped * Math.Sin(damped * seconds));
    }

    private static double[] Radial(Func<double, double> radiusAt)
    {
        var values = new double[Samples];
        var max = 0.0;
        for (var k = 0; k < Samples; k++)
        {
            values[k] = radiusAt(2 * Math.PI * k / Samples);
            max = Math.Max(max, values[k]);
        }
        for (var k = 0; k < Samples; k++) values[k] /= max;
        return values;
    }

    private static Func<double, double> Superellipse(double a, double b, double p) => t =>
        Math.Pow(Math.Pow(Math.Abs(Math.Cos(t) / a), p) + Math.Pow(Math.Abs(Math.Sin(t) / b), p), -1 / p);

    private static Func<double, double> RoundedPolygon(int sides) => t =>
    {
        var sector = 2 * Math.PI / sides;
        var offset = ((t + Math.PI / 2) % sector + sector) % sector - sector / 2;
        var polygon = Math.Cos(Math.PI / sides) / Math.Cos(offset);
        return 0.62 * polygon + 0.38 * Math.Cos(Math.PI / sides);
    };
}
