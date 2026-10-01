using System;
using System.Windows;
using System.Windows.Media.Animation;

namespace DynamicIsland;

public readonly record struct SpringSpec(double Stiffness, double Damping, double Seconds);

public sealed class SpringEase : EasingFunctionBase
{
    public double Stiffness { get; set; } = 240;
    public double Damping { get; set; } = 22;
    public double Seconds { get; set; } = 0.42;

    protected override double EaseInCore(double normalizedTime)
    {
        if (normalizedTime >= 1) return 1;
        var t = normalizedTime * Seconds;
        var omega = Math.Sqrt(Stiffness);
        var zeta = Damping / (2 * omega);
        if (zeta >= 1) return Math.Max(0, 1 - Math.Exp(-omega * t) * (1 + omega * t));

        var damped = omega * Math.Sqrt(1 - zeta * zeta);
        return Math.Max(0, 1 - Math.Exp(-zeta * omega * t) * (Math.Cos(damped * t) + zeta * omega / damped * Math.Sin(damped * t)));
    }

    protected override Freezable CreateInstanceCore() => new SpringEase { Stiffness = Stiffness, Damping = Damping, Seconds = Seconds };
}

public static class Motion
{
    public static readonly SpringSpec Bouncy = new(420, 15, 0.55);
    public static readonly SpringSpec Soft = new(240, 22, 0.42);
    public static readonly SpringSpec Snappy = new(520, 30, 0.30);
    public static readonly SpringSpec Pin = new(620, 25, 0.32);

    public static SpringEase Ease(SpringSpec spec) => new() { Stiffness = spec.Stiffness, Damping = spec.Damping, Seconds = spec.Seconds };

    public static DoubleAnimation Spring(double from, double to, SpringSpec spec) =>
        new(from, to, TimeSpan.FromSeconds(spec.Seconds)) { EasingFunction = Ease(spec) };

    public static DoubleAnimation Tween(double from, double to, int ms, IEasingFunction? ease = null, int delayMs = 0) =>
        new(from, to, TimeSpan.FromMilliseconds(ms))
        {
            EasingFunction = ease ?? new CubicEase { EasingMode = EasingMode.EaseOut },
            BeginTime = TimeSpan.FromMilliseconds(delayMs)
        };
}

public static class Reveal
{
    public static readonly DependencyProperty FractionProperty = DependencyProperty.RegisterAttached(
        "Fraction", typeof(double), typeof(Reveal), new PropertyMetadata(1.0, OnFractionChanged));

    public static double GetFraction(DependencyObject element) => (double)element.GetValue(FractionProperty);

    public static void SetFraction(DependencyObject element, double value) => element.SetValue(FractionProperty, value);

    private static void OnFractionChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not FrameworkElement target) return;

        var fraction = (double)e.NewValue;
        if (fraction >= 1)
        {
            target.Clip = null;
            return;
        }

        var height = target.ActualHeight > 0 ? target.ActualHeight : target.DesiredSize.Height;
        var width = target.ActualWidth > 0 ? target.ActualWidth : target.DesiredSize.Width;
        target.Clip = new System.Windows.Media.RectangleGeometry(new Rect(0, 0, width, Math.Max(0, height * fraction)), 8, 8);
    }
}

public static class Squircle
{
    public static readonly DependencyProperty FractionProperty = DependencyProperty.RegisterAttached(
        "Fraction", typeof(double), typeof(Squircle), new PropertyMetadata(0.0, OnFractionChanged));

    public static double GetFraction(DependencyObject element) => (double)element.GetValue(FractionProperty);

    public static void SetFraction(DependencyObject element, double value) => element.SetValue(FractionProperty, value);

    private static void OnFractionChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not System.Windows.Controls.Border border) return;

        var half = Math.Min(border.Width, border.Height) / 2;
        var fraction = Math.Clamp((double)e.NewValue, 0, 1.2);
        border.CornerRadius = new CornerRadius(Math.Max(0, half - half * 0.5 * fraction));
    }
}

public static class Counter
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.RegisterAttached(
        "Value", typeof(double), typeof(Counter), new PropertyMetadata(0.0, OnValueChanged));

    public static readonly DependencyProperty FormatterProperty = DependencyProperty.RegisterAttached(
        "Formatter", typeof(Func<double, string>), typeof(Counter));

    public static double GetValue(DependencyObject element) => (double)element.GetValue(ValueProperty);

    public static void SetValue(DependencyObject element, double value) => element.SetValue(ValueProperty, value);

    public static void SetFormatter(DependencyObject element, Func<double, string> formatter) => element.SetValue(FormatterProperty, formatter);

    private static void OnValueChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is System.Windows.Controls.TextBlock text && element.GetValue(FormatterProperty) is Func<double, string> format)
            text.Text = format((double)e.NewValue);
    }
}
