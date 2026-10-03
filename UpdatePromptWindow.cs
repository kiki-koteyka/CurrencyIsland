using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Microsoft.Win32;
using Wpf.Ui.Controls;
using Border = System.Windows.Controls.Border;
using Image = System.Windows.Controls.Image;
using TextBlock = System.Windows.Controls.TextBlock;

namespace DynamicIsland;

public sealed class UpdatePromptWindow : FluentWindow
{
    private sealed record Palette(Color Surface, Color Card, Color Ink, Color Ink2, Color Line, Color Accent, Color AccentInk, Color Soft, Color Bad, Color BadSoft);

    public event Action? UpdateAccepted;
    public event Action? CancelRequested;

    private readonly string _version;
    private readonly string _releaseUrl;
    private readonly Palette _p;
    private readonly Grid _availablePanel;
    private readonly Grid _downloadPanel;
    private readonly Grid _failurePanel;
    private readonly FoxProgress _fox;
    private readonly TextBlock _percentNumber;
    private readonly TextBlock _percentSign;
    private readonly StackPanel _percentRow;
    private readonly TextBlock _detailText;
    private readonly TextBlock _speedText;
    private readonly TextBlock _stageText;
    private readonly Border _cancelButton;
    private readonly TextBlock _cancelLabel;
    private readonly TextBlock _reasonText;
    private readonly TextBlock _failureMeta;
    private readonly Queue<(DateTime Time, long Bytes)> _samples = new();
    private int _shownPercent = -1;
    private bool _busy;

    public UpdatePromptWindow(string version, bool urgent = false, string notes = "", long assetSize = 0, DateTime? published = null, string releaseUrl = "")
    {
        _version = version;
        _releaseUrl = releaseUrl;
        _p = BuildPalette(IsDark());

        Title = "Currency Island";
        Width = 420;
        Height = 404;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ExtendsContentIntoTitleBar = true;
        WindowBackdropType = WindowBackdropType.None;
        Background = Brush(_p.Surface);
        Topmost = urgent;

        var root = new Grid { Background = Brush(_p.Surface) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var titleBar = new TitleBar { Title = "Currency Island", ShowMaximize = false, ShowMinimize = false, ShowClose = true, Foreground = Brush(_p.Ink2), ButtonsForeground = Brush(_p.Ink2) };
        Grid.SetRow(titleBar, 0);
        root.Children.Add(titleBar);

        _availablePanel = BuildAvailable(urgent, notes, assetSize, published);

        _fox = new FoxProgress(Brush(_p.Soft), Brush(_p.Accent));
        _percentNumber = Text("0", 40, _p.Ink, FontWeights.SemiBold, "Segoe UI Variable Display, Segoe UI");
        _percentSign = Text("%", 18, _p.Ink2, FontWeights.Normal);
        _percentSign.Margin = new Thickness(2, 0, 0, 7);
        _percentSign.VerticalAlignment = VerticalAlignment.Bottom;
        _percentRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
        _percentRow.Children.Add(_percentNumber);
        _percentRow.Children.Add(_percentSign);
        _detailText = Text("", 12, _p.Ink2, FontWeights.Normal, "Consolas");
        _speedText = Text("", 12, _p.Ink2, FontWeights.Normal, "Consolas");
        _stageText = Text("", 18, _p.Ink, FontWeights.SemiBold, "Segoe UI Variable Display, Segoe UI");
        _stageText.Visibility = Visibility.Collapsed;
        _stageText.TextWrapping = TextWrapping.Wrap;
        _stageText.Margin = new Thickness(0, 6, 0, 6);
        _cancelLabel = Text("Cancel", 13, _p.Ink, FontWeights.SemiBold);
        _cancelButton = ActionButton(_cancelLabel, primary: false, () =>
        {
            _cancelLabel.Text = "Cancelling";
            _cancelButton.IsHitTestVisible = false;
            CancelRequested?.Invoke();
        });
        _downloadPanel = BuildDownload();

        _reasonText = Text("", 13, _p.Ink, FontWeights.Normal);
        _reasonText.TextWrapping = TextWrapping.Wrap;
        _failureMeta = Text("", 12.5, _p.Ink2, FontWeights.Normal);
        _failurePanel = BuildFailure();

        var host = new Grid();
        host.Children.Add(_availablePanel);
        host.Children.Add(_downloadPanel);
        host.Children.Add(_failurePanel);
        Grid.SetRow(host, 1);
        root.Children.Add(host);
        Content = root;

        _fox.Frame += OnFoxFrame;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape && !_busy) Close();
        };
        ShowPanel(_availablePanel, animate: false);
    }

    public void ShowAvailable()
    {
        _busy = false;
        _samples.Clear();
        ShowPanel(_availablePanel);
    }

    public void ReportProgress(long read, long total)
    {
        var now = DateTime.UtcNow;
        _samples.Enqueue((now, read));
        while (_samples.Count > 2 && (now - _samples.Peek().Time).TotalSeconds > 2) _samples.Dequeue();

        if (total > 0) _fox.SetTarget((double)read / total);
        _detailText.Text = total > 0 ? $"{FormatMb(read)} / {FormatMb(total)} MB" : $"{FormatMb(read)} MB";

        var first = _samples.Peek();
        var elapsed = (now - first.Time).TotalSeconds;
        if (elapsed < 0.4)
        {
            _speedText.Text = "";
            return;
        }

        var speed = (read - first.Bytes) / elapsed;
        if (speed <= 1)
        {
            _speedText.Text = "";
            return;
        }

        var text = $"{(speed / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture)} MB/s";
        if (total > 0)
        {
            var seconds = (int)Math.Ceiling((total - read) / speed);
            text += seconds >= 60 ? $" · {(seconds + 59) / 60} min left" : $" · {seconds} s left";
        }
        _speedText.Text = text;
    }

    public void SetStage(string stage)
    {
        _fox.SetTarget(1);
        _percentRow.Visibility = Visibility.Collapsed;
        _speedText.Visibility = Visibility.Collapsed;
        _detailText.Text = "This takes a few seconds";
        _stageText.Text = stage switch
        {
            "verifying" => "Checking the file",
            "installing" => "Installing",
            _ => "Restarting Currency Island"
        };
        _stageText.Visibility = Visibility.Visible;
        _cancelButton.Visibility = Visibility.Collapsed;
        _fox.SetShine(stage == "restarting");
    }

    public void ShowFailure(Exception error)
    {
        _busy = false;
        _reasonText.Text = DescribeFailure(error);
        _failureMeta.Text = $"Your current version {AppVersion.Current} is untouched";
        ShowPanel(_failurePanel);
    }

    private void StartDownload()
    {
        _busy = true;
        _samples.Clear();
        _shownPercent = -1;
        _fox.Reset();
        _percentRow.Visibility = Visibility.Visible;
        _speedText.Visibility = Visibility.Visible;
        _stageText.Visibility = Visibility.Collapsed;
        _percentNumber.Text = "0";
        _detailText.Text = "Starting";
        _speedText.Text = "";
        _cancelLabel.Text = "Cancel";
        _cancelButton.IsHitTestVisible = true;
        _cancelButton.Visibility = Visibility.Visible;
        ShowPanel(_downloadPanel);
        UpdateAccepted?.Invoke();
    }

    private void OnFoxFrame(double shown)
    {
        var percent = (int)Math.Floor(shown * 100 + 0.0001);
        if (percent == _shownPercent) return;
        _shownPercent = percent;
        _percentNumber.Text = percent.ToString(CultureInfo.InvariantCulture);
    }

    private void ShowPanel(Grid panel, bool animate = true)
    {
        foreach (var candidate in new[] { _availablePanel, _downloadPanel, _failurePanel })
            candidate.Visibility = ReferenceEquals(candidate, panel) ? Visibility.Visible : Visibility.Collapsed;
        if (animate) panel.BeginAnimation(OpacityProperty, Motion.Tween(0, 1, 160));
    }

    private Grid BuildAvailable(bool urgent, string notes, long assetSize, DateTime? published)
    {
        var grid = new Grid { Margin = new Thickness(22, 8, 22, 18) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var meta = new List<string>();
        if (published is { } date) meta.Add("Released " + date.ToString("MMM d", CultureInfo.InvariantCulture));
        if (assetSize > 0) meta.Add(FormatMb(assetSize) + " MB");
        var header = BuildHeader(
            urgent ? "Important update available" : "Update available",
            meta.Count > 0 ? string.Join(" · ", meta) : "Version " + _version,
            urgent ? _p.Bad : _p.Ink,
            tileColor: null);
        Grid.SetRow(header, 0);
        grid.Children.Add(header);

        var pill = new Border
        {
            CornerRadius = new CornerRadius(8),
            Background = Brush(Color.FromArgb(40, _p.Accent.R, _p.Accent.G, _p.Accent.B)),
            Padding = new Thickness(12, 6.5, 12, 3.5),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 14, 0, 12)
        };
        var pillRow = new StackPanel { Orientation = Orientation.Horizontal };
        var oldVersion = Text(AppVersion.Current, 12.5, _p.Ink2, FontWeights.Normal, "Consolas");
        var arrow = Text("→", 12.5, _p.Ink2, FontWeights.Normal, "Segoe UI Symbol");
        arrow.Margin = new Thickness(9, 0, 9, 0);
        var newVersion = Text(_version, 12.5, _p.Ink, FontWeights.Bold, "Consolas");
        pillRow.Children.Add(oldVersion);
        pillRow.Children.Add(arrow);
        pillRow.Children.Add(newVersion);
        pill.Child = pillRow;
        Grid.SetRow(pill, 1);
        grid.Children.Add(pill);

        var list = new StackPanel();
        var lines = new List<string>();
        var bulleted = false;
        foreach (var raw in notes.Replace("\r", "").Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('•'))
            {
                bulleted = true;
                line = line[1..].Trim();
            }
            else if (bulleted || notes.Contains('•'))
            {
                continue;
            }
            if (line.Length > 0) lines.Add(line);
        }

        if (lines.Count == 0)
        {
            var hint = Text("The app downloads the update, closes and restarts, only after you click Update.", 13, _p.Ink2, FontWeights.Normal);
            hint.TextWrapping = TextWrapping.Wrap;
            list.Children.Add(hint);
        }

        foreach (var line in lines)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 7) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(15) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var dot = new Ellipse { Width = 6, Height = 6, Fill = Brush(_p.Accent), VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 7, 0, 0) };
            var textBlock = Text(line, 13, _p.Ink, FontWeights.Normal);
            textBlock.TextWrapping = TextWrapping.Wrap;
            Grid.SetColumn(textBlock, 1);
            row.Children.Add(dot);
            row.Children.Add(textBlock);
            list.Children.Add(row);
        }

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = list };
        Grid.SetRow(scroll, 2);
        grid.Children.Add(scroll);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var later = ActionButton(Text("Later", 13, _p.Ink, FontWeights.SemiBold), primary: false, Close);
        later.Margin = new Thickness(0, 0, 8, 0);
        var update = ActionButton(Text("Update", 13, _p.AccentInk, FontWeights.SemiBold), primary: true, StartDownload);
        buttons.Children.Add(later);
        buttons.Children.Add(update);
        Grid.SetRow(buttons, 3);
        grid.Children.Add(buttons);
        return grid;
    }

    private Grid BuildDownload()
    {
        var grid = new Grid { Margin = new Thickness(24, 10, 22, 18) };
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var content = new Grid { VerticalAlignment = VerticalAlignment.Center };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(20, 0, 0, 0) };
        info.Children.Add(Text("Updating to " + _version, 16, _p.Ink, FontWeights.SemiBold));
        info.Children.Add(_percentRow);
        info.Children.Add(_stageText);
        info.Children.Add(_detailText);
        info.Children.Add(_speedText);
        Grid.SetColumn(info, 1);
        content.Children.Add(_fox);
        content.Children.Add(info);
        Grid.SetRow(content, 0);
        grid.Children.Add(content);

        _cancelButton.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetRow(_cancelButton, 1);
        grid.Children.Add(_cancelButton);
        return grid;
    }

    private Grid BuildFailure()
    {
        var grid = new Grid { Margin = new Thickness(22, 8, 22, 18) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = BuildHeader("Could not install the update", "", _p.Ink, tileColor: _p.Bad, metaBlock: _failureMeta);
        Grid.SetRow(header, 0);
        grid.Children.Add(header);

        var box = new Border
        {
            Background = Brush(_p.BadSoft),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(13, 11, 13, 11),
            Margin = new Thickness(0, 16, 0, 0),
            Child = _reasonText
        };
        Grid.SetRow(box, 1);
        grid.Children.Add(box);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        if (!string.IsNullOrWhiteSpace(_releaseUrl))
        {
            var page = ActionButton(Text("Open release page", 13, _p.Ink, FontWeights.SemiBold), primary: false, () =>
            {
                try { Process.Start(new ProcessStartInfo(_releaseUrl) { UseShellExecute = true }); } catch { }
            });
            page.Margin = new Thickness(0, 0, 8, 0);
            buttons.Children.Add(page);
        }
        buttons.Children.Add(ActionButton(Text("Retry", 13, _p.AccentInk, FontWeights.SemiBold), primary: true, StartDownload));
        Grid.SetRow(buttons, 3);
        grid.Children.Add(buttons);
        return grid;
    }

    private UIElement BuildHeader(string title, string meta, Color titleColor, Color? tileColor, TextBlock? metaBlock = null)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        UIElement tile;
        if (tileColor is { } bad)
        {
            tile = new Border
            {
                Width = 52, Height = 52, CornerRadius = new CornerRadius(14), Background = Brush(bad),
                Child = new TextBlock
                {
                    Text = "!", FontSize = 26, FontWeight = FontWeights.Bold, Foreground = Brushes.White,
                    HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
                }
            };
        }
        else
        {
            var mask = new ImageBrush(FoxProgress.Logo) { Stretch = Stretch.Uniform };
            mask.Freeze();
            var fox = new Rectangle { Width = 52, Height = 52, Fill = Brush(_p.Ink), OpacityMask = mask, UseLayoutRounding = false, SnapsToDevicePixels = false };
            RenderOptions.SetBitmapScalingMode(fox, BitmapScalingMode.HighQuality);
            tile = fox;
        }

        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 0, 0) };
        texts.Children.Add(Text(title, 18, titleColor, FontWeights.SemiBold, "Segoe UI Variable Display, Segoe UI"));
        texts.Children.Add(metaBlock ?? Text(meta, 12.5, _p.Ink2, FontWeights.Normal));
        Grid.SetColumn(texts, 1);
        grid.Children.Add(tile);
        grid.Children.Add(texts);
        return grid;
    }

    private Border ActionButton(TextBlock label, bool primary, Action onClick)
    {
        var idle = primary ? _p.Accent : _p.Card;
        var hover = primary
            ? Color.FromRgb((byte)Math.Min(255, _p.Accent.R + 14), (byte)Math.Min(255, _p.Accent.G + 14), (byte)Math.Min(255, _p.Accent.B + 14))
            : Color.FromArgb(255, (byte)Math.Max(0, _p.Card.R - 10), (byte)Math.Max(0, _p.Card.G - 10), (byte)Math.Max(0, _p.Card.B - 10));
        var scale = new ScaleTransform(1, 1);
        var button = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(18, 8, 18, 8),
            Background = Brush(idle),
            BorderBrush = primary ? Brushes.Transparent : Brush(_p.Line),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            Child = label,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = scale
        };
        label.HorizontalAlignment = HorizontalAlignment.Center;
        button.MouseEnter += (_, _) => button.Background = Brush(hover);
        button.MouseLeave += (_, _) =>
        {
            button.Background = Brush(idle);
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            scale.ScaleX = 1;
            scale.ScaleY = 1;
        };
        button.PreviewMouseLeftButtonDown += (_, _) =>
        {
            scale.ScaleX = 0.96;
            scale.ScaleY = 0.96;
        };
        button.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            scale.ScaleX = 1;
            scale.ScaleY = 1;
            onClick();
        };
        return button;
    }

    private static TextBlock Text(string text, double size, Color color, FontWeight? weight = null, string? family = null)
    {
        var block = new TextBlock { Text = text, FontSize = size, Foreground = Brush(color), FontWeight = weight ?? FontWeights.Normal };
        if (family != null) block.FontFamily = new FontFamily(family);
        return block;
    }

    private static SolidColorBrush Brush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static string FormatMb(long bytes) => (bytes / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture);

    private static bool IsDark()
    {
        var choice = AppSettings.Load().Theme;
        return choice == 1 || (choice == 2 && IsSystemDark());
    }

    private static bool IsSystemDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch
        {
            return false;
        }
    }

    private static Palette BuildPalette(bool dark)
    {
        var accent = Wpf.Ui.Appearance.ApplicationAccentColorManager.SystemAccent;
        var luminance = (0.299 * accent.R + 0.587 * accent.G + 0.114 * accent.B) / 255;
        var accentInk = luminance > 0.6 ? Color.FromRgb(16, 16, 18) : Colors.White;

        return dark
            ? new Palette(
                Color.FromRgb(0x20, 0x20, 0x20), Color.FromRgb(0x2D, 0x2D, 0x2D),
                Color.FromRgb(0xFF, 0xFF, 0xFF), Color.FromArgb(0x9E, 0xFF, 0xFF, 0xFF),
                Color.FromArgb(0x1A, 255, 255, 255), accent, accentInk,
                Color.FromArgb(0x1A, 255, 255, 255),
                Color.FromRgb(0xFF, 0x6B, 0x70), Color.FromArgb(0x1A, 0xFF, 0x6B, 0x70))
            : new Palette(
                Color.FromRgb(0xF7, 0xF8, 0xF9), Color.FromRgb(0xFF, 0xFF, 0xFF),
                Color.FromRgb(0x14, 0x18, 0x1A), Color.FromArgb(0x9E, 0x14, 0x18, 0x1A),
                Color.FromArgb(0x1A, 0x14, 0x18, 0x1A), accent, accentInk,
                Color.FromArgb(0x1A, 0x14, 0x18, 0x1A),
                Color.FromRgb(0xC4, 0x2B, 0x31), Color.FromArgb(0x1A, 0xC4, 0x2B, 0x31));
    }

    private static string DescribeFailure(Exception error)
    {
        switch (error)
        {
            case UnauthorizedAccessException:
                return "Windows blocked writing the new file (access denied). Check that the app folder is not read-only, then try again.";
            case InvalidDataException:
                return "The downloaded file was incomplete. Try again.";
            case HttpRequestException:
            case TaskCanceledException:
            case System.Net.Sockets.SocketException:
                return "The download failed. Check your internet connection and try again.";
            case IOException:
                return "The file could not be replaced, it may be in use. Close other copies of the app and try again.";
            case InvalidOperationException when error.Message.Contains("did not start", StringComparison.OrdinalIgnoreCase):
                return "The new version did not start, so the previous version was restored.";
            default:
                var first = (error.Message ?? "").Split('\n')[0].Trim();
                return first.Length > 0 ? first : "Something went wrong while installing the update.";
        }
    }
}
