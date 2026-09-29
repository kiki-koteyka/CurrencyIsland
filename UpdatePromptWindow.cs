using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;

namespace DynamicIsland;

public sealed class UpdatePromptWindow : FluentWindow
{
    public event Action? UpdateAccepted;

    private readonly string _version;
    private readonly TextBlock _questionText;
    private readonly TextBlock _hintText;
    private readonly StackPanel _buttonRow;
    private readonly StackPanel _progressRow;
    private readonly Button _updateButton;
    private readonly Brush _normalBrush;

    public UpdatePromptWindow(string version, bool urgent = false)
    {
        _version = version;
        Title = "Currency Island";
        Width = 360;
        Height = 190;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ExtendsContentIntoTitleBar = true;
        WindowBackdropType = WindowBackdropType.None;
        Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F3F3F3")!);
        Topmost = urgent;

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var titleBar = new TitleBar { Title = "Currency Island", ShowMaximize = false, ShowMinimize = false };
        Grid.SetRow(titleBar, 0);
        root.Children.Add(titleBar);

        var body = new StackPanel { Margin = new Thickness(20, 8, 20, 16) };
        Grid.SetRow(body, 1);

        _normalBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(urgent ? "#D13438" : "#0F0F0F")!);
        _questionText = new TextBlock
        {
            Text = urgent
                ? $"Important update available: {version}."
                : $"Available new version: {version}. Update now?",
            FontSize = 14,
            FontWeight = urgent ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = _normalBrush,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8)
        };
        body.Children.Add(_questionText);

        _hintText = new TextBlock
        {
            Text = urgent
                ? "This release fixes something important. The app will download the update, close, and restart - only once you click Update."
                : "The app will download the update, close, and restart - only once you click Update.",
            FontSize = 12,
            Opacity = 0.65,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 16)
        };
        body.Children.Add(_hintText);

        _progressRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Left,
            Visibility = Visibility.Collapsed
        };
        _progressRow.Children.Add(new MorphSpinner
        {
            Width = 34,
            Height = 34,
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#0F0F0F")!)
        });
        _progressRow.Children.Add(new TextBlock
        {
            Text = "Downloading the update. The app will restart by itself.",
            FontSize = 12,
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 250,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 0, 0)
        });
        body.Children.Add(_progressRow);

        _buttonRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var laterButton = new Button { Content = "Later", Appearance = ControlAppearance.Secondary, Margin = new Thickness(0, 0, 8, 0) };
        laterButton.Click += (_, _) => Close();
        _updateButton = new Button { Content = "Update", Appearance = ControlAppearance.Primary };
        _updateButton.Click += (_, _) =>
        {
            ShowDownloading();
            UpdateAccepted?.Invoke();
        };
        _buttonRow.Children.Add(laterButton);
        _buttonRow.Children.Add(_updateButton);
        body.Children.Add(_buttonRow);

        root.Children.Add(body);
        Content = root;
    }

    private void ShowDownloading()
    {
        _questionText.Text = $"Updating to {_version}";
        _questionText.Foreground = _normalBrush;
        _hintText.Visibility = Visibility.Collapsed;
        _buttonRow.Visibility = Visibility.Collapsed;
        _progressRow.Visibility = Visibility.Visible;
    }

    public void ShowFailure()
    {
        _progressRow.Visibility = Visibility.Collapsed;
        _questionText.Text = "Could not install the update.";
        _questionText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#D13438")!);
        _hintText.Text = "Try again, or download the latest version manually from the release page.";
        _hintText.Visibility = Visibility.Visible;
        _updateButton.Content = "Retry";
        _buttonRow.Visibility = Visibility.Visible;
    }
}
