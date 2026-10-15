using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;

namespace DynamicIsland;

public sealed class RestartPromptWindow : FluentWindow
{
    public RestartPromptWindow(string what)
    {
        Title = "Currency Island";
        Width = 380;
        Height = 172;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ExtendsContentIntoTitleBar = true;
        WindowBackdropType = WindowBackdropType.None;
        ShowInTaskbar = false;
        Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F3F3F3")!);

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        var titleBar = new TitleBar { Title = "Currency Island", ShowMaximize = false, ShowMinimize = false };
        Grid.SetRow(titleBar, 0);
        root.Children.Add(titleBar);

        var body = new StackPanel { Margin = new Thickness(24, 0, 24, 16), VerticalAlignment = VerticalAlignment.Top };
        body.Children.Add(new SymbolIcon
        {
            Symbol = SymbolRegular.ArrowClockwise24,
            FontSize = 20,
            HorizontalAlignment = HorizontalAlignment.Left,
            Foreground = new SolidColorBrush(Color.FromRgb(0x00, 0x9F, 0xAA)),
            Margin = new Thickness(0, 0, 0, 6)
        });
        body.Children.Add(new TextBlock
        {
            Text = "Restart to apply",
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 4)
        });
        body.Children.Add(new TextBlock
        {
            Text = $"{what} takes effect after Currency Island restarts. It only takes a moment.",
            FontSize = 12.5,
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 14)
        });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var later = new Button { Content = "Later", Margin = new Thickness(0, 0, 8, 0), MinWidth = 84 };
        later.Click += (_, _) => { DialogResult = false; };
        var restart = new Button { Content = "Restart now", Appearance = ControlAppearance.Primary, MinWidth = 110 };
        restart.Click += (_, _) => { DialogResult = true; };
        buttons.Children.Add(later);
        buttons.Children.Add(restart);
        body.Children.Add(buttons);

        Grid.SetRow(body, 1);
        root.Children.Add(body);
        Content = root;
    }
}
