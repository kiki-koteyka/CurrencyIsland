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
    private bool _loading = true;

    public event Action? UpdateAccepted;

    public UpdatePromptWindow(string version)
    {
        Title = "Currency Island";
        Width = 360;
        Height = 220;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ExtendsContentIntoTitleBar = true;
        WindowBackdropType = WindowBackdropType.None;
        Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F3F3F3")!);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var titleBar = new TitleBar { Title = "Currency Island", ShowMaximize = false, ShowMinimize = false };
        Grid.SetRow(titleBar, 0);
        root.Children.Add(titleBar);

        var body = new StackPanel { Margin = new Thickness(20, 8, 20, 16) };
        Grid.SetRow(body, 1);

        var questionText = new TextBlock
        {
            Text = $"Available new version: {version}. Update now?",
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8)
        };
        body.Children.Add(questionText);

        var hintText = new TextBlock
        {
            Text = "The app will download the update, close, and restart automatically.",
            FontSize = 12,
            Opacity = 0.65,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 16)
        };
        body.Children.Add(hintText);

        var autoUpdateToggle = new ToggleSwitch { IsChecked = AppSettings.Load().AutoUpdate };
        var autoUpdateCard = new CardControl
        {
            Margin = new Thickness(0, 0, 0, 16),
            Icon = new SymbolIcon { Symbol = SymbolRegular.ArrowSync24 },
            Header = new TextBlock { Text = "Install updates automatically", FontSize = 13, VerticalAlignment = VerticalAlignment.Center },
            Content = autoUpdateToggle
        };
        autoUpdateToggle.Checked += (_, _) => SetAutoUpdate(true);
        autoUpdateToggle.Unchecked += (_, _) => SetAutoUpdate(false);
        body.Children.Add(autoUpdateCard);

        var buttonRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var laterButton = new Button { Content = "Later", Appearance = ControlAppearance.Secondary, Margin = new Thickness(0, 0, 8, 0) };
        laterButton.Click += (_, _) => Close();
        var updateButton = new Button { Content = "Update", Appearance = ControlAppearance.Primary };
        updateButton.Click += (_, _) =>
        {
            UpdateAccepted?.Invoke();
            Close();
        };
        buttonRow.Children.Add(laterButton);
        buttonRow.Children.Add(updateButton);
        body.Children.Add(buttonRow);

        root.Children.Add(body);
        Content = root;

        _loading = false;
    }

    private void SetAutoUpdate(bool enabled)
    {
        if (_loading) return;
        var data = AppSettings.Load();
        data.AutoUpdate = enabled;
        AppSettings.Save(data);
    }
}
