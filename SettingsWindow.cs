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
    private readonly IIsland _island;
    private SettingsData _data;

    private readonly StackPanel _pageGeneral = new();
    private readonly StackPanel _pageAppearance = new() { Visibility = Visibility.Collapsed };
    private readonly StackPanel _pageAbout = new() { Visibility = Visibility.Collapsed };
    private Border _navAppearance = null!;
    private Border _navGeneral = null!;
    private Border _navAbout = null!;

    public SettingsWindow(IIsland island)
    {
        _island = island;
        _data = AppSettings.Load();

        Title = "Currency Island";
        Width = 460;
        Height = 560;
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
        _navAppearance = BuildNavItem(SymbolRegular.PaintBrush24, isFirst: false);
        _navAbout = BuildNavItem(SymbolRegular.Info24, isFirst: false);
        _navGeneral.MouseLeftButtonUp += (_, _) => ShowPage(0);
        _navAppearance.MouseLeftButtonUp += (_, _) => ShowPage(1);
        _navAbout.MouseLeftButtonUp += (_, _) => ShowPage(2);
        navStack.Children.Add(_navGeneral);
        navStack.Children.Add(_navAppearance);
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
        _pageGeneral.Children.Add(BuildStartTabCard());
        _pageGeneral.Children.Add(BuildResetPositionCard());
        contentPanel.Children.Add(_pageGeneral);

        _pageAppearance.Children.Add(new TextBlock
        {
            Text = "Look and feel of the island.",
            Opacity = 0.6,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 16)
        });
        _pageAppearance.Children.Add(BuildDesignCard());
        _pageAppearance.Children.Add(BuildAnimationsCard());
        contentPanel.Children.Add(_pageAppearance);

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
        _pageAbout.Children.Add(BuildLogsCard());
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

        ShowPage(0);
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
        border.MouseLeave += (_, _) => ShowPage(_pageGeneral.Visibility == Visibility.Visible ? 0 : _pageAppearance.Visibility == Visibility.Visible ? 1 : 2);
        return border;
    }

    private void ShowPage(int page)
    {
        _pageGeneral.Visibility = page == 0 ? Visibility.Visible : Visibility.Collapsed;
        _pageAppearance.Visibility = page == 1 ? Visibility.Visible : Visibility.Collapsed;
        _pageAbout.Visibility = page == 2 ? Visibility.Visible : Visibility.Collapsed;
        var active = new SolidColorBrush(Color.FromArgb(50, 0, 159, 170));
        _navGeneral.Background = page == 0 ? active : Brushes.Transparent;
        _navAppearance.Background = page == 1 ? active : Brushes.Transparent;
        _navAbout.Background = page == 2 ? active : Brushes.Transparent;
    }

    private static void RestartApp()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe == null) return;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c ping -n 3 127.0.0.1 >nul & start \"\" \"{exe}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false
            });
            Application.Current.Shutdown();
        }
        catch
        {
            // best-effort restart only
        }
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

    private CardControl BuildAnimationsCard()
    {
        var toggle = new ToggleSwitch { IsChecked = _data.Animations };
        toggle.Checked += (_, _) =>
        {
            Motion.Enabled = true;
            AppSettings.Update(d => d.Animations = true);
        };
        toggle.Unchecked += (_, _) =>
        {
            Motion.Enabled = false;
            AppSettings.Update(d => d.Animations = false);
        };

        return new CardControl
        {
            Margin = new Thickness(0, 0, 0, 8),
            Icon = new SymbolIcon { Symbol = SymbolRegular.Sparkle24 },
            Header = new TextBlock { Text = "Animations", FontSize = 14, VerticalAlignment = VerticalAlignment.Center },
            Content = toggle
        };
    }

    private CardControl BuildStartTabCard()
    {
        var combo = new System.Windows.Controls.ComboBox { MinWidth = 130, VerticalAlignment = VerticalAlignment.Center };
        foreach (var name in new[] { "Last used", "Rates", "Chart", "Swapper", "Range", "Multi calc" })
            combo.Items.Add(name);
        combo.SelectedIndex = Math.Clamp(_data.StartTab + 1, 0, 5);
        combo.SelectionChanged += (_, _) =>
        {
            var index = combo.SelectedIndex;
            if (index >= 0) AppSettings.Update(d => d.StartTab = index - 1);
        };

        return new CardControl
        {
            Margin = new Thickness(0, 0, 0, 8),
            Icon = new SymbolIcon { Symbol = SymbolRegular.Star24 },
            Header = new TextBlock { Text = "Start tab", FontSize = 14, VerticalAlignment = VerticalAlignment.Center },
            Content = combo
        };
    }

    private CardControl BuildDesignCard()
    {
        var combo = new System.Windows.Controls.ComboBox { MinWidth = 150, VerticalAlignment = VerticalAlignment.Center };
        // Stored values: 1 = Classic, 0 = Colorful. (2 = Minimal exists in code but is not offered yet.)
        var stored = new[] { 1, 0 };
        foreach (var name in new[] { "Classic", "Colorful" })
            combo.Items.Add(name);
        combo.SelectedIndex = Math.Max(0, Array.IndexOf(stored, _data.Design));
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedIndex < 0) return;
            var design = stored[combo.SelectedIndex];
            if (design == AppSettings.Load().Design) return;
            AppSettings.Update(d => d.Design = design);
            AskRestart($"The {combo.SelectedItem} design");
        };

        return new CardControl
        {
            Margin = new Thickness(0, 0, 0, 8),
            Icon = new SymbolIcon { Symbol = SymbolRegular.PaintBrush24 },
            Header = new TextBlock { Text = "Island design", FontSize = 14, VerticalAlignment = VerticalAlignment.Center },
            Content = combo
        };
    }

    private CardControl BuildIslandThemeCard()
    {
        var combo = new System.Windows.Controls.ComboBox { MinWidth = 150, VerticalAlignment = VerticalAlignment.Center };
        foreach (var name in new[] { "Dark", "Light", "Follow Windows" })
            combo.Items.Add(name);
        combo.SelectedIndex = Math.Clamp(_data.IslandTheme, 0, 2);
        combo.SelectionChanged += (_, _) =>
        {
            var index = combo.SelectedIndex;
            if (index < 0 || index == AppSettings.Load().IslandTheme) return;
            AppSettings.Update(d => d.IslandTheme = index);
            if (AppSettings.Load().Design == 2) AskRestart($"The {combo.SelectedItem.ToString()!.ToLowerInvariant()} theme");
        };

        return new CardControl
        {
            Margin = new Thickness(0, 0, 0, 8),
            Icon = new SymbolIcon { Symbol = SymbolRegular.WeatherMoon24 },
            Header = new TextBlock { Text = "Minimal theme", FontSize = 14, VerticalAlignment = VerticalAlignment.Center },
            Content = combo
        };
    }

    private void AskRestart(string what)
    {
        var prompt = new RestartPromptWindow(what) { Owner = this };
        if (prompt.ShowDialog() == true) RestartApp();
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
                    ((App)Application.Current).OfferUpdate(result.LatestVersion, result.AssetDownloadUrl, result.Notes, result.AssetSize, result.Published, result.ReleaseUrl);
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

    private CardControl BuildLogsCard()
    {
        var statusText = new TextBlock { FontSize = 11, Opacity = 0.6, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        var openButton = new Button { Content = "Open", Appearance = ControlAppearance.Secondary, Margin = new Thickness(0, 0, 6, 0) };
        var copyButton = new Button { Content = "Copy", Appearance = ControlAppearance.Secondary };

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(statusText);
        row.Children.Add(openButton);
        row.Children.Add(copyButton);

        openButton.Click += (_, _) =>
        {
            try
            {
                System.IO.Directory.CreateDirectory(LogBundle.Folder);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{LogBundle.Folder}\"") { UseShellExecute = true });
                statusText.Text = "";
            }
            catch
            {
                statusText.Text = "Could not open the folder.";
            }
        };

        copyButton.Click += (_, _) =>
        {
            try
            {
                var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
                var data = new System.Windows.DataObject();
                data.SetFileDropList(new System.Collections.Specialized.StringCollection { LogBundle.Build(scale) });
                data.SetText(LogBundle.BuildText(scale));
                var copied = false;
                for (var attempt = 0; attempt < 5 && !copied; attempt++)
                {
                    try
                    {
                        System.Windows.Clipboard.SetDataObject(data, true);
                        copied = true;
                    }
                    catch (System.Runtime.InteropServices.COMException)
                    {
                        System.Threading.Thread.Sleep(40);
                    }
                }
                statusText.Text = copied ? "Copied to clipboard" : "Copy failed.";
                if (copied)
                {
                    copyButton.Content = "Copied";
                    var reset = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1600) };
                    reset.Tick += (_, _) =>
                    {
                        reset.Stop();
                        copyButton.Content = "Copy";
                        statusText.Text = "";
                    };
                    reset.Start();
                }
            }
            catch
            {
                statusText.Text = "Copy failed.";
            }
        };

        return new CardControl
        {
            Margin = new Thickness(0, 0, 0, 8),
            Icon = new SymbolIcon { Symbol = SymbolRegular.DocumentText24 },
            Header = new TextBlock { Text = "Logs", FontSize = 14, VerticalAlignment = VerticalAlignment.Center },
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
