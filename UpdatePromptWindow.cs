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

    // urgent (the "URGENT" release-body marker) never forces anything - it
    // used to skip this window entirely and self-update without asking.
    // Now the update only ever starts from the button click below, same as
    // any other release; urgent just makes this window harder to miss
    // (a red accent + a plainer "this fixes something important" line)
    // instead of silently swapping the exe out from under the user.
    public UpdatePromptWindow(string version, bool urgent = false)
    {
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

        var accentColor = urgent ? "#D13438" : "#0F0F0F";
        var questionText = new TextBlock
        {
            Text = urgent
                ? $"Important update available: {version}."
                : $"Available new version: {version}. Update now?",
            FontSize = 14,
            FontWeight = urgent ? FontWeights.SemiBold : FontWeights.Normal,
            Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(accentColor)!),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8)
        };
        body.Children.Add(questionText);

        var hintText = new TextBlock
        {
            Text = urgent
                ? "This release fixes something important. The app will download the update, close, and restart - only once you click Update."
                : "The app will download the update, close, and restart - only once you click Update.",
            FontSize = 12,
            Opacity = 0.65,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 16)
        };
        body.Children.Add(hintText);

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
    }
}
