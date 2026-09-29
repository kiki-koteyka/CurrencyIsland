// Shapes and motion ported from Aler1x/m3-loading-indicator (MIT), itself a port of material-components-android LoadingIndicator.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;

namespace DynamicIsland;

public sealed class MorphSpinner : FrameworkElement
{
    private const int ShapeCount = 7;
    private const int SamplesPerCurve = 14;
    private const int PointsPerShape = 180;
    private const double MsPerShape = 650;
    private const double Stiffness = 200;
    private const double DampingRatio = 0.6;
    private const double SizeRatio = 0.79;

    private static readonly (double ViewBox, string Path)[] Definitions =
    {
        (144, "M65.3162 3.5567C68.3922 -1.18556 75.6078 -1.18557 78.6837 3.55669L86.0569 14.9241C88.0791 18.0418 92.1588 19.3094 95.7112 17.9237L108.664 12.8715C114.067 10.7638 119.905 14.8195 119.478 20.3849L118.456 33.7255C118.175 37.3845 120.697 40.7029 124.422 41.5786L138.007 44.7714C143.674 46.1033 145.903 52.6655 142.137 56.9283L133.11 67.1465C130.634 69.9491 130.634 74.0509 133.11 76.8535L142.137 87.0716C145.903 91.3345 143.674 97.8967 138.007 99.2286L124.422 102.421C120.697 103.297 118.175 106.616 118.456 110.274L119.478 123.615C119.905 129.181 114.067 133.236 108.664 131.129L95.7112 126.076C92.1588 124.691 88.0791 125.958 86.0569 129.076L78.6838 140.443C75.6078 145.186 68.3922 145.186 65.3162 140.443L57.9431 129.076C55.9208 125.958 51.8412 124.691 48.2888 126.076L35.3365 131.129C29.933 133.236 24.0954 129.181 24.5219 123.615L25.5442 110.274C25.8246 106.616 23.3033 103.297 19.5775 102.421L5.99339 99.2286C0.326335 97.8967 -1.90342 91.3345 1.86259 87.0717L10.8899 76.8535C13.3658 74.0509 13.3658 69.9491 10.8899 67.1465L1.8626 56.9283C-1.90341 52.6655 0.326326 46.1033 5.99338 44.7714L19.5775 41.5786C23.3033 40.7029 25.8246 37.3845 25.5442 33.7255L24.5219 20.3849C24.0954 14.8195 29.933 10.7638 35.3364 12.8715L48.2888 17.9237C51.8412 19.3094 55.9208 18.0418 57.9431 14.9241L65.3162 3.5567Z"),
        (144, "M56.3679 6.02002C57.1442 5.3783 57.5324 5.05744 57.8867 4.78656C66.2354 -1.59552 77.7646 -1.59552 86.1133 4.78656C86.4676 5.05744 86.8558 5.3783 87.6321 6.02002C87.9786 6.30648 88.1519 6.44971 88.3233 6.58606C92.2522 9.71203 97.0693 11.4835 102.068 11.6405C102.286 11.6473 102.51 11.6501 102.957 11.6558C103.96 11.6684 104.462 11.6747 104.906 11.6973C115.361 12.2303 124.193 19.7179 126.528 30.0289C126.627 30.4666 126.721 30.9644 126.907 31.9602C126.99 32.4047 127.032 32.627 127.076 32.8427C128.097 37.789 130.661 42.2744 134.39 45.6409C134.552 45.7878 134.722 45.9353 135.062 46.2304C135.822 46.8914 136.202 47.2219 136.528 47.5275C144.198 54.7262 146.2 66.1979 141.429 75.6132C141.227 76.0128 140.981 76.4547 140.49 77.3386C140.271 77.7332 140.162 77.9304 140.059 78.1246C137.694 82.5768 136.804 87.6775 137.519 92.6782C137.55 92.8964 137.586 93.1196 137.658 93.5661C137.82 94.5662 137.901 95.0663 137.956 95.5117C139.252 106.008 133.488 116.096 123.843 120.21C123.434 120.385 122.965 120.564 122.026 120.922C121.608 121.082 121.398 121.162 121.196 121.244C116.552 123.119 112.625 126.448 109.991 130.743C109.876 130.93 109.762 131.125 109.533 131.514C109.021 132.385 108.765 132.821 108.523 133.198C102.839 142.08 92.0048 146.064 81.9992 142.952C81.5745 142.82 81.1011 142.652 80.1544 142.318C79.7318 142.168 79.5205 142.094 79.3133 142.025C74.5631 140.445 69.4369 140.445 64.6867 142.025C64.4795 142.094 64.2682 142.168 63.8456 142.318C62.8989 142.652 62.4255 142.82 62.0008 142.952C51.9952 146.064 41.1613 142.08 35.4766 133.198C35.2353 132.821 34.9791 132.385 34.4669 131.514C34.2382 131.125 34.1239 130.93 34.009 130.743C31.3752 126.448 27.4482 123.119 22.8044 121.244C22.6018 121.162 22.3924 121.082 21.9736 120.922C21.0354 120.564 20.5663 120.385 20.1569 120.21C10.5122 116.096 4.74763 106.008 6.04367 95.5117C6.09868 95.0663 6.17963 94.5662 6.34151 93.5661C6.41377 93.1196 6.4499 92.8964 6.48109 92.6783C7.19603 87.6775 6.30587 82.5768 3.94121 78.1246C3.83807 77.9304 3.72855 77.7332 3.50951 77.3386C3.01883 76.4547 2.77349 76.0128 2.57099 75.6132C-2.19995 66.1979 -0.197931 54.7262 7.47248 47.5275C7.79804 47.2219 8.17819 46.8914 8.93848 46.2304C9.27787 45.9353 9.44757 45.7878 9.61023 45.6409C13.3394 42.2744 15.9025 37.789 16.9235 32.8427C16.9681 32.627 17.0097 32.4047 17.0929 31.9602C17.2793 30.9644 17.3725 30.4666 17.4717 30.0289C19.8069 19.7179 28.6387 12.2303 39.0944 11.6973C39.5382 11.6747 40.0397 11.6684 41.0426 11.6558C41.4903 11.6501 41.7142 11.6473 41.9322 11.6405C46.9307 11.4835 51.7478 9.71203 55.6767 6.58606C55.8481 6.44971 56.0214 6.30648 56.3679 6.02002Z"),
        (144, "M49.3332 10.9681C56.3577 5.53061 59.8699 2.81189 63.6224 1.46315C69.0501 -0.487717 74.9499 -0.487717 80.3776 1.46315C84.1301 2.81189 87.6423 5.53062 94.6668 10.9681L110.03 22.8606L125.386 34.0038C132.67 39.2902 136.313 41.9334 138.747 45.2576C142.27 50.0661 144.119 55.9761 143.994 62.0207C143.907 66.1996 142.46 70.5678 139.564 79.3044L133.535 97.4958L127.969 116.136C125.358 124.884 124.052 129.258 121.769 132.66C118.466 137.581 113.667 141.201 108.144 142.936C104.327 144.135 99.9259 144.065 91.1241 143.926L72 143.623L52.8759 143.926C44.0741 144.065 39.6732 144.135 35.8555 142.936C30.3334 141.201 25.5338 137.581 22.2314 132.66C19.9483 129.258 18.6425 124.884 16.0307 116.136L10.4655 97.4958L4.43609 79.3044C1.54044 70.5678 0.0926215 66.1996 0.00597479 62.0207C-0.119358 55.9761 1.73035 50.0661 5.2525 45.2576C7.68747 41.9334 11.3298 39.2902 18.6143 34.0038L33.9696 22.8606L49.3332 10.9681Z"),
        (144, "M40.4355 21.4968C63.0979 -1.16559 99.8408 -1.16559 122.503 21.4968C145.166 44.1592 145.166 80.9021 122.503 103.565L103.565 122.503C80.9021 145.166 44.1592 145.166 21.4968 122.503C-1.16559 99.8408 -1.1656 63.0979 21.4968 40.4355L40.4355 21.4968Z"),
        (153, "M117.835 18.5569C122.594 18.8803 124.973 19.0419 126.896 19.883C129.679 21.0999 131.9 23.3213 133.117 26.1039C133.958 28.027 134.12 30.4062 134.443 35.1647L135.181 46.0237C135.312 47.9482 135.377 48.9105 135.586 49.8297C135.889 51.1579 136.414 52.4254 137.139 53.5784C137.641 54.3763 138.275 55.1029 139.544 56.5563L146.7 64.7565C149.837 68.3499 151.405 70.1466 152.17 72.1011C153.277 74.9292 153.277 78.0708 152.17 80.8989C151.405 82.8534 149.837 84.6501 146.7 88.2435L139.544 96.4437C138.275 97.8971 137.641 98.6237 137.139 99.4217C136.414 100.575 135.889 101.842 135.586 103.17C135.377 104.09 135.312 105.052 135.181 106.976L134.443 117.835C134.12 122.594 133.958 124.973 133.117 126.896C131.9 129.679 129.679 131.9 126.896 133.117C124.973 133.958 122.594 134.12 117.835 134.443L106.976 135.181C105.052 135.312 104.09 135.377 103.17 135.586C101.842 135.889 100.575 136.414 99.4217 137.139C98.6237 137.641 97.8971 138.275 96.4437 139.544L88.2435 146.7C84.6501 149.837 82.8534 151.405 80.8989 152.17C78.0708 153.277 74.9292 153.277 72.1011 152.17C70.1466 151.405 68.3499 149.837 64.7565 146.7L56.5563 139.544C55.1029 138.275 54.3763 137.641 53.5784 137.139C52.4254 136.414 51.1579 135.889 49.8297 135.586C48.9105 135.377 47.9482 135.312 46.0237 135.181L35.1647 134.443C30.4062 134.12 28.027 133.958 26.1039 133.117C23.3213 131.9 21.0999 129.679 19.883 126.896C19.0419 124.973 18.8803 122.594 18.5569 117.835L17.819 106.976C17.6882 105.052 17.6228 104.09 17.4136 103.17C17.1113 101.842 16.5863 100.575 15.8608 99.4217C15.3588 98.6237 14.7246 97.8971 13.4562 96.4437L6.29956 88.2435C3.16348 84.6501 1.59544 82.8534 0.830322 80.8989C-0.276774 78.0708 -0.276774 74.9292 0.830323 72.1011C1.59544 70.1466 3.16348 68.3499 6.29956 64.7565L13.4562 56.5563C14.7246 55.1029 15.3588 54.3763 15.8608 53.5784C16.5863 52.4254 17.1113 51.1579 17.4136 49.8297C17.6228 48.9105 17.6882 47.9482 17.819 46.0237L18.5569 35.1647C18.8803 30.4062 19.0419 28.027 19.883 26.1039C21.0999 23.3213 23.3213 21.0999 26.1039 19.883C28.027 19.0419 30.4062 18.8803 35.1647 18.5569L46.0237 17.819C47.9482 17.6882 48.9105 17.6228 49.8297 17.4136C51.1579 17.1113 52.4254 16.5863 53.5784 15.8608C54.3763 15.3588 55.1029 14.7246 56.5563 13.4562L64.7565 6.29957C68.3499 3.16348 70.1466 1.59544 72.1011 0.830323C74.9292 -0.276774 78.0708 -0.276774 80.8989 0.830323C82.8534 1.59544 84.6501 3.16348 88.2435 6.29957L96.4437 13.4562C97.8971 14.7246 98.6237 15.3588 99.4216 15.8608C100.575 16.5863 101.842 17.1113 103.17 17.4136C104.09 17.6228 105.052 17.6882 106.976 17.819L117.835 18.5569Z"),
        (144, "M89.4282 11.7931C116.492 0.0387955 143.961 27.5077 132.207 54.5718L130.263 59.0465C126.675 67.3094 126.675 76.6907 130.263 84.9535L132.207 89.4282C143.961 116.492 116.492 143.961 89.4282 132.207L84.9535 130.263C76.6907 126.675 67.3093 126.675 59.0465 130.263L54.5718 132.207C27.5077 143.961 0.0387983 116.492 11.7931 89.4282L13.7366 84.9535C17.3253 76.6907 17.3252 67.3093 13.7366 59.0465L11.7931 54.5718C0.0387955 27.5077 27.5077 0.0387993 54.5718 11.7931L59.0465 13.7366C67.3094 17.3252 76.6907 17.3252 84.9535 13.7366L89.4282 11.7931Z"),
    };

    private static readonly Lazy<Point[][]> Shapes = new(BuildShapes);

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
    private double _lastSeconds;
    private double _elapsedMs;
    private int _previousCycle;
    private double _morphTarget = 1;
    private double _springPos;
    private double _springVel;
    private double _rotation;

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
            _lastSeconds = 0;
            _elapsedMs = 0;
            _previousCycle = 0;
            _morphTarget = 1;
            _springPos = 0;
            _springVel = 0;
            CompositionTarget.Rendering += OnFrame;
            _running = true;
        }
        else if (!shouldRun && _running)
        {
            CompositionTarget.Rendering -= OnFrame;
            _running = false;
        }
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        var now = _clock.Elapsed.TotalSeconds;
        var dt = Math.Min(now - _lastSeconds, 0.1);
        _lastSeconds = now;
        if (dt > 0) Advance(dt);
        InvalidateVisual();
    }

    private void Advance(double dt)
    {
        _elapsedMs += dt * 1000;
        var cycle = (int)Math.Floor(_elapsedMs / MsPerShape);
        if (cycle > _previousCycle)
        {
            _morphTarget += cycle - _previousCycle;
            _previousCycle = cycle;
        }

        var fraction = _elapsedMs % MsPerShape / MsPerShape;
        var damping = DampingRatio * 2 * Math.Sqrt(Stiffness);
        const int subSteps = 12;
        var sub = dt / subSteps;
        for (var i = 0; i < subSteps; i++)
        {
            var accel = -Stiffness * (_springPos - _morphTarget) - damping * _springVel;
            _springVel += accel * sub;
            _springPos += _springVel * sub;
        }

        var baseIndex = _morphTarget - 1;
        var perShape = _springPos - baseIndex;
        _rotation = ((50 + 90) * baseIndex + 50 * fraction + 90 * perShape) % 360;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0) return;

        var shapes = Shapes.Value;
        var index = (int)Math.Floor(_springPos);
        var from = shapes[((index % ShapeCount) + ShapeCount) % ShapeCount];
        var to = shapes[(((index % ShapeCount) + ShapeCount) % ShapeCount + 1) % ShapeCount];
        var t = Math.Clamp(_springPos - index, 0, 1);

        var scale = size * SizeRatio / 2;
        var angle = _rotation * Math.PI / 180;
        var cos = Math.Cos(angle);
        var sin = Math.Sin(angle);
        var center = new Point(ActualWidth / 2, ActualHeight / 2);

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            for (var i = 0; i < PointsPerShape; i++)
            {
                var x = from[i].X + (to[i].X - from[i].X) * t;
                var y = from[i].Y + (to[i].Y - from[i].Y) * t;
                var point = new Point(center.X + (x * cos - y * sin) * scale, center.Y + (x * sin + y * cos) * scale);
                if (i == 0) ctx.BeginFigure(point, true, true);
                else ctx.LineTo(point, false, true);
            }
        }
        geometry.Freeze();
        dc.DrawGeometry(Foreground, null, geometry);
    }

    private static Point[][] BuildShapes()
    {
        var result = new Point[Definitions.Length + 1][];
        for (var i = 0; i < Definitions.Length; i++)
            result[i] = Resample(Normalize(PathToPoints(Definitions[i].Path), Definitions[i].ViewBox), PointsPerShape);

        var oval = new Point[PointsPerShape];
        for (var i = 0; i < PointsPerShape; i++)
        {
            var theta = (double)i / PointsPerShape * 2 * Math.PI;
            oval[i] = new Point(Math.Cos(theta), 0.74 * Math.Sin(theta));
        }
        result[Definitions.Length] = oval;
        return result;
    }

    private static readonly Regex CommandPattern = new(@"([MCLZmclz])([^MCLZmclz]*)", RegexOptions.Compiled);
    private static readonly Regex NumberPattern = new(@"-?\d+\.?\d*(?:e[+-]?\d+)?", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static List<Point> PathToPoints(string path)
    {
        var points = new List<Point>();
        double cx = 0, cy = 0;
        foreach (Match command in CommandPattern.Matches(path))
        {
            var args = NumberPattern.Matches(command.Groups[2].Value)
                .Select(m => double.Parse(m.Value, CultureInfo.InvariantCulture)).ToArray();
            switch (command.Groups[1].Value[0])
            {
                case 'M':
                    cx = args[0]; cy = args[1];
                    points.Add(new Point(cx, cy));
                    break;
                case 'L':
                    for (var i = 0; i + 1 < args.Length; i += 2)
                    {
                        cx = args[i]; cy = args[i + 1];
                        points.Add(new Point(cx, cy));
                    }
                    break;
                case 'C':
                    for (var i = 0; i + 5 < args.Length; i += 6)
                    {
                        var p0 = new Point(cx, cy);
                        var p1 = new Point(args[i], args[i + 1]);
                        var p2 = new Point(args[i + 2], args[i + 3]);
                        var p3 = new Point(args[i + 4], args[i + 5]);
                        for (var step = 1; step <= SamplesPerCurve; step++)
                        {
                            var t = (double)step / SamplesPerCurve;
                            var u = 1 - t;
                            points.Add(new Point(
                                u * u * u * p0.X + 3 * u * u * t * p1.X + 3 * u * t * t * p2.X + t * t * t * p3.X,
                                u * u * u * p0.Y + 3 * u * u * t * p1.Y + 3 * u * t * t * p2.Y + t * t * t * p3.Y));
                        }
                        cx = p3.X; cy = p3.Y;
                    }
                    break;
            }
        }
        return points;
    }

    private static Point[] Normalize(List<Point> points, double viewBox)
    {
        var half = viewBox / 2;
        var centered = points.Select(p => new Point((p.X - half) / half, (p.Y - half) / half)).ToArray();
        var minX = centered.Min(p => p.X);
        var maxX = centered.Max(p => p.X);
        var minY = centered.Min(p => p.Y);
        var maxY = centered.Max(p => p.Y);
        var scale = Math.Min(2 / (maxX - minX), 2 / (maxY - minY));
        var ox = (minX + maxX) / 2;
        var oy = (minY + maxY) / 2;
        return centered.Select(p => new Point((p.X - ox) * scale, (p.Y - oy) * scale)).ToArray();
    }

    private static Point[] Resample(Point[] points, int count)
    {
        var all = points.Append(points[0]).ToArray();
        var arc = new double[all.Length];
        for (var i = 1; i < all.Length; i++)
        {
            var dx = all[i].X - all[i - 1].X;
            var dy = all[i].Y - all[i - 1].Y;
            arc[i] = arc[i - 1] + Math.Sqrt(dx * dx + dy * dy);
        }

        var total = arc[^1];
        var result = new Point[count];
        for (var i = 0; i < count; i++)
        {
            var target = (double)i / count * total;
            var j = 1;
            while (j < arc.Length - 1 && arc[j] < target) j++;
            var t = arc[j] > arc[j - 1] ? (target - arc[j - 1]) / (arc[j] - arc[j - 1]) : 0;
            result[i] = new Point(all[j - 1].X + t * (all[j].X - all[j - 1].X), all[j - 1].Y + t * (all[j].Y - all[j - 1].Y));
        }
        return result;
    }
}
