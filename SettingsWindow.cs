using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;

namespace DynamicIsland;

public sealed class SettingsWindow : FluentWindow
{
    private readonly IslandWindow _island;
    private SettingsData _data;

    private readonly StackPanel _pageGeneral = new();
    private readonly StackPanel _pageAbout = new() { Visibility = Visibility.Collapsed };
    private Border _navGeneral = null!;
    private Border _navAbout = null!;

    public SettingsWindow(IslandWindow island)
    {
        _island = island;
        _data = AppSettings.Load();

        Title = "Currency Island";
        Width = 460;
        Height = 420;
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

        var body = new Grid();
        Grid.SetRow(body, 1);
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var nav = new Border
        {
            Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#EAEBEC")!),
            CornerRadius = new CornerRadius(14, 14, 0, 0),
            Margin = new Thickness(0, 12, 0, 0)
        };
        Grid.SetColumn(nav, 0);
        Grid.SetRow(nav, 0);
        Grid.SetRowSpan(nav, 2);

        var navStack = new StackPanel();
        _navGeneral = BuildNavItem(SymbolRegular.Settings24, isFirst: true);
        _navAbout = BuildNavItem(SymbolRegular.Info24, isFirst: false);
        _navGeneral.MouseLeftButtonUp += (_, _) => ShowPage(general: true);
        _navAbout.MouseLeftButtonUp += (_, _) => ShowPage(general: false);
        navStack.Children.Add(_navGeneral);
        navStack.Children.Add(_navAbout);
        nav.Child = navStack;
        body.Children.Add(nav);

        var contentPanel = new StackPanel { Margin = new Thickness(20, 12, 20, 12) };
        Grid.SetColumn(contentPanel, 1);
        Grid.SetRow(contentPanel, 0);

        _pageGeneral.Children.Add(new TextBlock
        {
            Text = "Currency Island - hover to expand, drag to move, scroll to switch tabs.",
            Opacity = 0.6,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 16)
        });
        _pageGeneral.Children.Add(BuildAutostartCard());
        _pageGeneral.Children.Add(BuildChartHoverStyleCard());
        _pageGeneral.Children.Add(BuildResetPositionCard());
        contentPanel.Children.Add(_pageGeneral);

        _pageAbout.Children.Add(new TextBlock { Text = "Currency Island", FontSize = 18, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        _pageAbout.Children.Add(new TextBlock { Text = "by Kiki", Opacity = 0.6, FontSize = 12, Margin = new Thickness(0, 0, 0, 16) });
        _pageAbout.Children.Add(new TextBlock
        {
            Text = "A macOS/iOS-style Dynamic Island for Windows, focused purely on CBR exchange rates: live USD/EUR/CNY, yesterday-vs-today, and a 5-day trend chart.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13,
            Opacity = 0.8,
            Margin = new Thickness(0, 0, 0, 16)
        });
        _pageAbout.Children.Add(new TextBlock
        {
            Text = $"Version {AppVersion.Current}",
            FontSize = 12,
            Opacity = 0.6,
            Margin = new Thickness(0, 0, 0, 8)
        });
        _pageAbout.Children.Add(BuildCheckUpdateCard());
        contentPanel.Children.Add(_pageAbout);

        body.Children.Add(contentPanel);

        var footer = new Grid { Margin = new Thickness(20, 0, 20, 16) };
        Grid.SetColumn(footer, 1);
        Grid.SetRow(footer, 1);
        var closeButton = new Button { Content = "Close", Appearance = ControlAppearance.Primary, HorizontalAlignment = HorizontalAlignment.Right };
        closeButton.Click += (_, _) => Close();
        footer.Children.Add(closeButton);
        body.Children.Add(footer);

        root.Children.Add(body);
        Content = root;

        ShowPage(general: true);
    }

    private Border BuildNavItem(SymbolRegular glyph, bool isFirst)
    {
        var border = new Border
        {
            Width = 36,
            Height = 36,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, isFirst ? 12 : 6, 0, 6),
            CornerRadius = new CornerRadius(10),
            Cursor = Cursors.Hand,
            Child = new SymbolIcon
            {
                Symbol = glyph,
                FontSize = 16,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#5F6368")!)
            }
        };
        border.MouseEnter += (_, _) => border.Background = new SolidColorBrush(Color.FromArgb(30, 0, 0, 0));
        border.MouseLeave += (_, _) => ShowPage(_pageGeneral.Visibility == Visibility.Visible);
        return border;
    }

    private void ShowPage(bool general)
    {
        _pageGeneral.Visibility = general ? Visibility.Visible : Visibility.Collapsed;
        _pageAbout.Visibility = general ? Visibility.Collapsed : Visibility.Visible;
        _navGeneral.Background = general ? new SolidColorBrush(Color.FromArgb(50, 0, 159, 170)) : Brushes.Transparent;
        _navAbout.Background = general ? Brushes.Transparent : new SolidColorBrush(Color.FromArgb(50, 0, 159, 170));
    }

    private CardControl BuildAutostartCard()
    {
        var toggle = new ToggleSwitch { IsChecked = AppSettings.IsAutostartEnabled() };
        toggle.Checked += (_, _) => AppSettings.SetAutostart(true);
        toggle.Unchecked += (_, _) => AppSettings.SetAutostart(false);

        return new CardControl
        {
            Margin = new Thickness(0, 0, 0, 8),
            Icon = new SymbolIcon { Symbol = SymbolRegular.Power24 },
            Header = new TextBlock { Text = "Launch with Windows", FontSize = 14, VerticalAlignment = VerticalAlignment.Center },
            Content = toggle
        };
    }

    private CardControl BuildChartHoverStyleCard()
    {
        var toggle = new ToggleSwitch { IsChecked = _data.ChartCrosshairHover };
        toggle.Checked += (_, _) =>
        {
            _data.ChartCrosshairHover = true;
            AppSettings.Save(_data);
            _island.SetChartHoverStyle(true);
        };
        toggle.Unchecked += (_, _) =>
        {
            _data.ChartCrosshairHover = false;
            AppSettings.Save(_data);
            _island.SetChartHoverStyle(false);
        };

        return new CardControl
        {
            Margin = new Thickness(0, 0, 0, 8),
            Icon = new SymbolIcon { Symbol = SymbolRegular.CursorHover24 },
            Header = new TextBlock { Text = "Crosshair chart hover", FontSize = 14, VerticalAlignment = VerticalAlignment.Center },
            Content = toggle
        };
    }

    private CardControl BuildCheckUpdateCard()
    {
        var statusText = new TextBlock { FontSize = 11, Opacity = 0.6, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        var button = new Button { Content = "Check now", Appearance = ControlAppearance.Secondary };

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(statusText);
        row.Children.Add(button);

        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            statusText.Text = "Checking...";
            try
            {
                var result = await UpdateChecker.CheckAsync();
                if (result.UpdateAvailable && result.AssetDownloadUrl is not null)
                {
                    statusText.Text = $"Update available: {result.LatestVersion}";
                    ((App)Application.Current).OfferUpdate(result.LatestVersion, result.AssetDownloadUrl);
                }
                else
                {
                    statusText.Text = "You're up to date.";
                }
            }
            catch
            {
                statusText.Text = "Check failed.";
            }
            finally
            {
                button.IsEnabled = true;
            }
        };

        return new CardControl
        {
            Margin = new Thickness(0, 0, 0, 8),
            Icon = new SymbolIcon { Symbol = SymbolRegular.ArrowClockwise24 },
            Header = new TextBlock { Text = "Check for updates", FontSize = 14, VerticalAlignment = VerticalAlignment.Center },
            Content = row
        };
    }

    private CardControl BuildResetPositionCard()
    {
        var button = new Button { Content = "Reset", Appearance = ControlAppearance.Secondary };
        button.Click += (_, _) => _island.ResetPosition();

        return new CardControl
        {
            Margin = new Thickness(0, 0, 0, 8),
            Icon = new SymbolIcon { Symbol = SymbolRegular.ArrowReset24 },
            Header = new TextBlock { Text = "Reset position", FontSize = 14, VerticalAlignment = VerticalAlignment.Center },
            Content = button
        };
    }
}
