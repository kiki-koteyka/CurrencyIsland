using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using System.Linq;
using Ellipse = System.Windows.Shapes.Ellipse;
using ShapePath = System.Windows.Shapes.Path;
using Line = System.Windows.Shapes.Line;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace DynamicIsland;

public sealed class PositionState
{
    public double Left { get; set; } = double.NaN;
    public double Top { get; set; } = double.NaN;
    public string AnchorH { get; set; } = "Right";
    public string AnchorV { get; set; } = "Top";
}

public sealed class IslandWindow : Window
{
    private const double CollapsedWidth = 130;
    private const double CollapsedHeight = 34;
    private const double ExpandedWidth = 380;
    // The fixed OS window is sized once for whichever tab needs the most
    // room (the chart tab). Content is vertically centered within it, so
    // this has to track whatever TabExpandedHeight's own max actually is.
    private const double ExpandedHeight = 178;

    // Per-tab expanded heights - the table only needs ~95px of content, the
    // chart needs ~170px, the calculator (two amount rows + swap + rate
    // line) sits in between. Index matches
    // CurrencyTabIndex/ChartTabIndex/SwapperTabIndex. ExpandedHeight
    // above stays the conservative MAX of this array, still used by the
    // pre-expand corner/growth-alignment math (deciding which corner to
    // grow from before anything has actually resized) so that math never
    // under-estimates how much room the pill might need.
    private static readonly double[] TabExpandedHeight = { 138, 178, 152, 168, 168 };
    private const double EdgeMargin = 0;
    private const double TopEdgeMargin = 0;
    private const double BottomEdgeMargin = 0;
    private const double SnapThreshold = 14;
    private const int TabCount = 5;
    // Content column starts at the root Grid's 18px left margin, so this
    // is what puts a ChartWidth-wide view on the pill's own horizontal center.
    private const double CenteredTabLeftMargin = (ExpandedWidth - 300) / 2 - 18;
    private const int CurrencyTabIndex = 0;
    private const int ChartTabIndex = 1;
    private const int SwapperTabIndex = 2;
    private const int RangeTabIndex = 3;
    private const int MultiCalcTabIndex = 4;
    private static readonly TimeSpan CurrencyCacheLifetime = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan AnimDuration = TimeSpan.FromMilliseconds(220);

    private readonly Border _shell;
    private double _hoverZoneWidth;
    private double _hoverZoneHeight;
    private Grid _content = null!;
    private ContentControl _collapsedIcon = null!;
    private readonly ContentControl _tabHost = new();
    private readonly Ellipse[] _dots = new Ellipse[TabCount];
    private readonly FrameworkElement[] _tabViews = new FrameworkElement[TabCount];
    private readonly CbrRatesProvider _rates = new();
    private const int TableDays = 3;
    private readonly TextBlock[] _tableHeaderTexts = new TextBlock[TableDays];
    private readonly TextBlock[] _usdValueTexts = new TextBlock[TableDays];
    private readonly TextBlock[] _eurValueTexts = new TextBlock[TableDays];
    private readonly TextBlock[] _cnyValueTexts = new TextBlock[TableDays];
    private DateTime _historyFetchedAt = DateTime.MinValue;
    private List<CbrHistoryPoint>? _latestHistory;
    private readonly string _stateFile;
    private PositionState _pos = new();
    private Border _settingsButton = null!;
    private Border _pinButton = null!;
    private ShapePath _pinIcon = null!;
    private readonly ScaleTransform _pinScale = new(1, 1);
    private readonly RotateTransform _pinRotate = new(45);
    private bool _pinHover;
    private readonly SolidColorBrush _pinBgBrush = new();
    private readonly SolidColorBrush _pinIconBrush = new();

    public static readonly DependencyProperty PinMorphProperty = DependencyProperty.Register(
        nameof(PinMorph), typeof(double), typeof(IslandWindow),
        new PropertyMetadata(0.0, (d, _) => ((IslandWindow)d).ApplyPinMorph()));

    public double PinMorph
    {
        get => (double)GetValue(PinMorphProperty);
        set => SetValue(PinMorphProperty, value);
    }
    private bool _pinned;
    private int _startTab;
    private bool _tabPersistenceReady;
    private int _currentTab;
    private bool _tabAnimating;
    private bool _forcedHidden;
    private bool _isExpanded;
    private bool _dragging;
    private bool _isMouseOverShell;
    private bool _resizingTab;
    private bool _dragCandidate;
    private bool _positionUpdateInProgress;
    private bool _snapped;
    private bool _snapArmed;
    private Point _dragMouseStart;
    private Point _dragWindowStart;
    private HorizontalAlignment _dragBaseH;
    private VerticalAlignment _dragBaseV;

    public IslandWindow()
    {
        var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CurrencyIsland");
        Directory.CreateDirectory(folder);
        _stateFile = Path.Combine(folder, "position.json");
        LoadPosition();
        var startupSettings = AppSettings.Load();
        _chartCrosshairHover = startupSettings.ChartCrosshairHover;
        _pinned = startupSettings.Pinned;
        Motion.Enabled = startupSettings.Animations;
        _startTab = Math.Clamp(startupSettings.StartTab < 0 ? startupSettings.LastTab : startupSettings.StartTab, 0, TabCount - 1);

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        ShowActivated = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        SizeToContent = SizeToContent.Manual;

        // Width is fixed at the expanded footprint forever - it's never
        // touched outside this line, unlike Height (see AnimateWindowHeight),
        // so the phantom-event risk that comment used to warn about only
        // ever applied to Height. Starting Height at CollapsedHeight (not
        // ExpandedHeight) matters now for a reason that has nothing to do
        // with that: the app always STARTS collapsed (_isExpanded defaults
        // false), and IsCursorOverHoverZone()/PillTopFromWindow assume the
        // window's real Height already matches whatever the pill is
        // currently resting at. Starting it at the wrong (expanded) value
        // left Height stale until the first real resize ever ran - for a
        // Center/Bottom anchor specifically, that meant the hover check was
        // testing the cursor against a rectangle offset from where _shell
        // actually rendered (Center-aligned in a window taller than it),
        // so the very first hover after launch could never register as
        // "over" the pill at all.
        Width = ExpandedWidth;
        Height = CollapsedHeight;
        HorizontalContentAlignment = HorizontalAlignment.Right;
        VerticalContentAlignment = VerticalAlignment.Top;

        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
        UseLayoutRounding = true;

        _shell = new Border
        {
            // Plain flat dark gradient - an earlier "glossy sheen" (a bright
            // white gradient stop at the top edge) was mistaken for a stray
            // shadow/glow artifact and is gone for good, not just muted.
            Background = new LinearGradientBrush(
                new GradientStopCollection
                {
                    new GradientStop(Color.FromArgb(248, 32, 32, 35), 0),
                    new GradientStop(Color.FromArgb(248, 8, 8, 10), 1)
                },
                new Point(0, 0), new Point(0, 1)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(CollapsedHeight / 2),
            Width = CollapsedWidth,
            Height = CollapsedHeight,
            ClipToBounds = true,
            SnapsToDevicePixels = true
        };
        RenderOptions.SetBitmapScalingMode(_shell, BitmapScalingMode.HighQuality);
        // Window.HorizontalContentAlignment/VerticalContentAlignment does not
        // reliably reposition Content for a WindowStyle=None+AllowsTransparency
        // window - it silently stays centered regardless of the property value.
        // So Content is a stretching Grid, and _shell is positioned within it
        // via its OWN HorizontalAlignment/VerticalAlignment (a plain
        // FrameworkElement arrange, which always works). See SetAnchor().
        // No Effect here (DropShadowEffect was tried on contentHost for a
        // premium floating look) - any Effect on this Grid forces WPF to
        // rasterize it into an intermediate bitmap, and that intermediate
        // surface doesn't reliably preserve per-pixel alpha when the parent
        // Window is AllowsTransparency=true. The result was a solid
        // white/opaque rectangle showing through instead of the transparent
        // background, reproduced across two different rendering
        // configurations - not worth it for a shadow.
        _hoverZoneWidth = CollapsedWidth;
        _hoverZoneHeight = CollapsedHeight;

        var contentHost = new Grid();
        contentHost.Children.Add(_shell);
        Content = contentHost;
        SetAnchor(HorizontalContentAlignment, VerticalContentAlignment);

        BuildInnerContent();

        // A quick pass of the cursor over the shell can enter AND leave
        // entirely within a single resize's guard window - that Leave event
        // was simply swallowed (guarded, same as a phantom one), and since
        // the cursor has already left, no further Leave event was ever going
        // to arrive to retry it, leaving the pill stuck open. _isMouseOverShell
        // tracks the real, un-guarded hover state on every Enter/Leave; once
        // a resize's guard clears (see ResizeForTab/CollapseShellAndWindow), it's checked
        // against _isExpanded and reconciled - so a hover change that arrived
        // mid-resize still takes effect once it's safe to act on it.
        //
        // Enter/Leave themselves are NOT trusted at face value though -
        // _shell's own Width/Height animate for the grow/shrink motion, and
        // WPF re-synchronizes MouseEnter/MouseLeave on every layout pass, not
        // just on real mouse input. A cursor sitting in the gap between the
        // collapsed and expanded footprints (326 vs 380 wide) got flipped in
        // and out of "over the shell" on every animation frame as _shell's
        // own bounds swept across it mid-resize - each flip re-triggering
        // Expand()/Collapse(), which starts another animation, which flips
        // it again: a self-sustaining oscillation with the cursor never
        // actually moving (seen as the pill rapidly resizing on its own).
        // Enter/Leave still fire and still drive a re-check, but what they
        // trigger is IsCursorOverHoverZone() - the TRUE OS cursor position
        // (GetCursorScreenDip, unaffected by which element WPF thinks is
        // "hit") against _hoverZoneWidth/Height, which snap instantly
        // (see ResizeForTab/CollapseShellAndWindow) to each resize's TARGET size instead of
        // animating - a stable rectangle, so only genuine cursor movement
        // can change the result.
        _shell.MouseEnter += (_, _) =>
        {
            _isMouseOverShell = IsCursorOverHoverZone();
            if (_isMouseOverShell && !_dragging && !_resizingTab) Expand();
        };
        _shell.MouseLeave += (_, _) =>
        {
            _isMouseOverShell = IsCursorOverHoverZone();
            if (!_isMouseOverShell && !_dragging && !_resizingTab && _openDropdown is not { IsOpen: true }) Collapse();
        };
        _shell.MouseLeftButtonDown += OnShellMouseDown;
        _shell.MouseMove += OnShellMouseMove;
        _shell.MouseLeftButtonUp += OnShellMouseUp;
        _shell.MouseWheel += OnShellMouseWheel;

        Loaded += (_, _) => ApplyPosition();

        // RefreshHistoryIfStale() used to only ever get CALLED from hover/tab-
        // switch handlers - so a pill left sitting collapsed for hours (the
        // normal way a desktop widget like this actually gets used) never
        // refreshed at all, no matter how short CurrencyCacheLifetime was.
        // This ticks on its own, independent of any user interaction, so the
        // rate actually gets re-checked on a real clock instead of only when
        // someone happens to touch the pill.
        _currencyRefreshTimer = new DispatcherTimer { Interval = CurrencyCacheLifetime };
        _currencyRefreshTimer.Tick += (_, _) => RefreshHistoryIfStale();
        _currencyRefreshTimer.Start();

        // Safety net for a MouseLeave that WPF never actually dispatches -
        // rare but real for an AllowsTransparency layered window with a
        // constantly-changing hit-test region, and when it happens there is
        // no event left to react to: the pill sits expanded forever with
        // the cursor long gone, because nothing else was ever going to
        // re-check. This polls IsCursorOverHoverZone() on a plain clock
        // instead of trusting the event to arrive - only acts when it
        // actually disagrees with the current state, so on the normal path
        // (events firing fine) it never does anything.
        _hoverPollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _hoverPollTimer.Tick += (_, _) =>
        {
            if (_dragging || _resizingTab || _forcedHidden) return;
            var over = IsCursorOverHoverZone();
            if (over == _isExpanded) return;
            _isMouseOverShell = over;
            if (over) Expand(); else Collapse();
        };
        _hoverPollTimer.Start();
    }

    private readonly DispatcherTimer _currencyRefreshTimer;
    private readonly DispatcherTimer _hoverPollTimer;

    private void BuildInnerContent()
    {
        // Top/bottom margins equal (10/10) - they used to be 10/6, and with
        // the settings button no longer eating a row out of this Grid (see
        // below), that leftover asymmetry alone was enough to shift row 0's
        // vertical center - and everything centered in it, including the
        // tab dots - a couple pixels off the capsule's true center.
        var root = new Grid { Margin = new Thickness(18, 10, 14, 10), Opacity = 0, Visibility = Visibility.Hidden };
        _content = root;
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        _tabHost.HorizontalContentAlignment = HorizontalAlignment.Left;
        _tabHost.VerticalContentAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_tabHost, 0);
        Grid.SetRow(_tabHost, 0);
        root.Children.Add(_tabHost);

        // A Grid of fixed-height rows, not a StackPanel - at 125% DPI, 6 DIP
        // dots are 7.5 physical pixels, a fractional value that can't land
        // on a whole pixel. A StackPanel's cumulative vertical offsets meant
        // each dot's fractional remainder differed slightly from the last,
        // so their anti-aliased edges rounded to different sub-pixel
        // horizontal positions - a visible diagonal drift even though every
        // dot's logical HorizontalAlignment was identically Center. Uniform
        // fixed rows give every dot the exact same arrange rectangle.
        var dotsPanel = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0),
            UseLayoutRounding = true,
            SnapsToDevicePixels = true
        };
        for (var i = 0; i < TabCount; i++)
        {
            dotsPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(14) });
            var dot = new Ellipse
            {
                Width = 6,
                Height = 6,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                SnapsToDevicePixels = true,
                UseLayoutRounding = true
            };
            Grid.SetRow(dot, i);
            _dots[i] = dot;
            dotsPanel.Children.Add(dot);
        }
        Grid.SetColumn(dotsPanel, 1);
        Grid.SetRow(dotsPanel, 0);
        root.Children.Add(dotsPanel);

        // Lives directly in shellRoot (below), NOT in root - root's own
        // Margin is tuned for the tab content's breathing room (asymmetric
        // left/right on purpose) and used to also stretch a Grid row
        // specifically to fit this button, which pushed root's content area
        // off-center vertically as a side effect. An equal 14/14 margin
        // here, measured straight from the capsule's real edge, is what
        // actually makes the corner look symmetric.
        // shellRoot has no opacity/visibility gating of its own (root does,
        // for the tab content) - moving this out of root for the corner-
        // margin fix meant it was no longer hidden by root's Opacity=0/
        // Visibility=Hidden default, so it stayed visible (and badly
        // clipped) even in the collapsed pill. Starts hidden here; Expand()/
        // Collapse()/CollapseFast() toggle it explicitly alongside _content.
        var settingsButton = new Border
        {
            Width = 26,
            Height = 26,
            CornerRadius = new CornerRadius(13),
            Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 6, 6),
            Opacity = 0,
            Visibility = Visibility.Hidden,
            Cursor = Cursors.Hand,
            Child = new ShapePath
            {
                Data = Geometry.Parse("M19.14,12.94c0.04-0.3,0.06-0.61,0.06-0.94c0-0.32-0.02-0.64-0.07-0.94l2.03-1.58c0.18-0.14,0.23-0.41,0.12-0.61 l-1.92-3.32c-0.12-0.22-0.37-0.29-0.59-0.22l-2.39,0.96c-0.5-0.38-1.03-0.7-1.62-0.94L14.4,2.81c-0.04-0.24-0.24-0.41-0.48-0.41 h-3.84c-0.24,0-0.43,0.17-0.47,0.41L9.25,5.35C8.66,5.59,8.12,5.92,7.63,6.29L5.24,5.33c-0.22-0.08-0.47,0-0.59,0.22L2.74,8.87 C2.62,9.08,2.66,9.34,2.86,9.48l2.03,1.58C4.84,11.36,4.8,11.69,4.8,12s0.02,0.64,0.07,0.94l-2.03,1.58 c-0.18,0.14-0.23,0.41-0.12,0.61l1.92,3.32c0.12,0.22,0.37,0.29,0.59,0.22l2.39-0.96c0.5,0.38,1.03,0.7,1.62,0.94l0.36,2.54 c0.05,0.24,0.24,0.41,0.48,0.41h3.84c0.24,0,0.44-0.17,0.47-0.41l0.36-2.54c0.59-0.24,1.13-0.56,1.62-0.94l2.39,0.96 c0.22,0.08,0.47,0,0.59-0.22l1.92-3.32c0.12-0.22,0.07-0.47-0.12-0.61L19.14,12.94z M12,15.6c-1.98,0-3.6-1.62-3.6-3.6 s1.62-3.6,3.6-3.6s3.6,1.62,3.6,3.6S13.98,15.6,12,15.6z"),
                Fill = new SolidColorBrush(Color.FromArgb(190, 255, 255, 255)),
                Stretch = Stretch.Uniform,
                Width = 14, Height = 14,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        settingsButton.MouseEnter += (_, _) => settingsButton.Background = new SolidColorBrush(Color.FromArgb(75, 255, 255, 255));
        settingsButton.MouseLeave += (_, _) => settingsButton.Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255));
        _settingsButton = settingsButton;

        _pinIcon = new ShapePath
        {
            Data = Geometry.Parse("M16,9V4h1c0.55,0,1-0.45,1-1c0-0.55-0.45-1-1-1H7C6.45,2,6,2.45,6,3c0,0.55,0.45,1,1,1h1v5c0,1.66-1.34,3-3,3v2h5.97v7l1,1l1-1v-7H19v-2C17.34,12,16,10.66,16,9z"),
            Stretch = Stretch.Uniform,
            Width = 14, Height = 14,
            RenderTransformOrigin = new Point(0.5, 0.5),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        _pinButton = new Border
        {
            Width = 26,
            Height = 26,
            CornerRadius = new CornerRadius(13),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 6, 6, 0),
            Opacity = 0,
            Visibility = Visibility.Hidden,
            Cursor = Cursors.Hand,
            Child = _pinIcon
        };
        _pinIcon.RenderTransform = _pinRotate;
        _pinButton.RenderTransformOrigin = new Point(0.5, 0.5);
        _pinButton.RenderTransform = _pinScale;
        PinMorph = _pinned ? 1 : 0;
        ApplyPinMorph();
        _pinButton.MouseEnter += (_, _) => { _pinHover = true; ApplyPinMorph(); };
        _pinButton.MouseLeave += (_, _) => { _pinHover = false; ApplyPinMorph(); };
        _pinButton.PreviewMouseDown += (_, e) => e.Handled = true;
        _pinButton.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            SetPinned(!_pinned);
        };
        settingsButton.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true; // stop this bubbling to _shell's own MouseLeftButtonDown, which starts a drag
            SettingsRequested?.Invoke();
        };
        _tabViews[CurrencyTabIndex] = BuildRatesView();
        _tabViews[ChartTabIndex] = BuildChartView();
        _tabViews[SwapperTabIndex] = BuildCalculatorView();
        _tabViews[RangeTabIndex] = BuildRangeView();
        _tabViews[MultiCalcTabIndex] = BuildMultiCalcView();

        _collapsedIcon = new ContentControl
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };

        var shellRoot = new Grid();
        shellRoot.Children.Add(_collapsedIcon);
        shellRoot.Children.Add(root);
        shellRoot.Children.Add(settingsButton);
        shellRoot.Children.Add(_pinButton);

        _shell.Child = shellRoot;
        SetTab(_startTab);
        _tabPersistenceReady = true;
    }

    private static readonly FontFamily LabelFont = new("Segoe UI Semibold");
    private static readonly FontFamily ValueFont = new("Consolas");

    // A little table: a caption reading "Курс ЦБ на" sits above the currency
    // column, and the two value columns are headed by the actual dates they
    // cover (not "Вчера"/"Сегодня") - so it doubles as the "as of" date this
    // used to show in a separate footer line, without spending extra rows.
    private FrameworkElement BuildRatesView()
    {
        // Top-anchored, not centered - the settings gear is a fixed 26px
        // circle pinned to the shell's own bottom-right corner regardless of
        // which tab is showing, and centering this short table in the
        // (also short) table-tab window put the CNY row's rightmost value
        // right under it. Hugging the top instead keeps the whole table
        // clear of that reserved corner. See TabExpandedHeight.
        var grid = new Grid { VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 4, 16, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < TableDays; i++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
        for (var i = 0; i < 4; i++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var dimHeader = new SolidColorBrush(Color.FromArgb(120, 235, 235, 240));
        var captionText = new TextBlock
        {
            Text = "CBR rate on", FontSize = 10, FontFamily = LabelFont, Foreground = dimHeader,
            VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 3)
        };
        Grid.SetRow(captionText, 0);
        Grid.SetColumn(captionText, 0);
        grid.Children.Add(captionText);

        for (var d = 0; d < TableDays; d++)
        {
            var header = new TextBlock
            {
                FontSize = 10, FontFamily = LabelFont, Foreground = dimHeader,
                HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 0, 3)
            };
            Grid.SetRow(header, 0);
            Grid.SetColumn(header, d + 1);
            _tableHeaderTexts[d] = header;
            grid.Children.Add(header);
        }

        AddCurrencyRow(grid, 1, "$", UsdAccent, _usdValueTexts);
        AddCurrencyRow(grid, 2, "€", EurAccent, _eurValueTexts);
        AddCurrencyRow(grid, 3, "¥", CnyAccent, _cnyValueTexts);

        return grid;
    }

    private static void AddCurrencyRow(Grid grid, int row, string symbol, Color accent, TextBlock[] valueTexts)
    {
        // A single colored currency glyph replaces the old dot+"USD" pair -
        // same information (which line/color this row is), a lot less
        // horizontal space spent on it.
        var labelRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };
        labelRow.Children.Add(new TextBlock
        {
            Text = symbol, FontSize = 13, FontWeight = FontWeights.Bold, FontFamily = ValueFont,
            Foreground = new SolidColorBrush(accent),
            VerticalAlignment = VerticalAlignment.Center
        });
        Grid.SetRow(labelRow, row);
        Grid.SetColumn(labelRow, 0);
        grid.Children.Add(labelRow);

        for (var d = 0; d < TableDays; d++)
        {
            // The most recent (rightmost) day is the only one in full white -
            // the two before it fade progressively dimmer, so the eye lands
            // on "today" first without the older columns looking like dead
            // filler.
            var isLatest = d == TableDays - 1;
            var text = new TextBlock
            {
                FontSize = isLatest ? 14 : 13,
                FontWeight = FontWeights.SemiBold,
                FontFamily = ValueFont,
                Foreground = isLatest ? Brushes.White : new SolidColorBrush(Color.FromArgb((byte)(90 + d * 40), 235, 235, 240)),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, d == TableDays - 1 ? 0 : 4, 0)
            };
            Grid.SetRow(text, row);
            Grid.SetColumn(text, d + 1);
            grid.Children.Add(text);
            valueTexts[d] = text;
        }
    }

    private async void RefreshHistoryIfStale()
    {
        if (DateTime.UtcNow - _historyFetchedAt < CurrencyCacheLifetime) return;

        var points = await _rates.FetchHistoryAsync(ChartDays);
        if (points.Count == 0) return;

        _historyFetchedAt = DateTime.UtcNow;
        _latestHistory = points;
        ApplyHistoryToViews();
    }

    private void ApplyHistoryToViews()
    {
        if (_latestHistory is not { Count: > 0 } points) return;

        var tableSlice = points.Count >= TableDays ? points.Skip(points.Count - TableDays).ToList() : points;
        var pad = TableDays - tableSlice.Count;
        for (var d = 0; d < TableDays; d++)
        {
            if (d < pad)
            {
                _tableHeaderTexts[d].Text = "";
                _usdValueTexts[d].Text = "";
                _eurValueTexts[d].Text = "";
                _cnyValueTexts[d].Text = "";
                continue;
            }

            var p = tableSlice[d - pad];
            _tableHeaderTexts[d].Text = p.Date.ToString("dd.MM");
            _usdValueTexts[d].Text = $"{p.UsdRub:0.0000}";
            _eurValueTexts[d].Text = $"{p.EurRub:0.0000}";
            _cnyValueTexts[d].Text = $"{p.CnyRub:0.0000}";
        }

        UpdateChartVisual(points);
        UpdateCalculatorRate(points[^1]);
        McRenderResult();

        _collapsedIcon.Content = BuildCollapsedIcon(_currentTab);
        FitCollapsedPillWidth();
    }

    private void FitCollapsedPillWidth()
    {
        if (_isExpanded || _resizingTab || _dragging) return;

        var target = GetCollapsedWidth();
        if (Math.Abs(_shell.Width - target) < 0.5) return;

        _hoverZoneWidth = target;
        AnimateShell(target, CollapsedHeight, CollapsedHeight / 2, AnimDuration);
    }

    // A 5-day trend line per currency, normalized to its OWN min/max (not a
    // shared ruble scale - USD/EUR/CNY sit at wildly different absolute
    // values, so a shared axis would flatten CNY to a barely-visible sliver
    // at the bottom). Normalizing independently shows each currency's own
    // shape of movement, which is what "is it trending up or down" actually
    // needs - not a literal ruler between them. Smoothed (Catmull-Rom into
    // cubic beziers) with a soft gradient fill under each curve, per-point
    // markers and a value callout on the latest point - the plain straight-
    // line/no-numbers version read as a placeholder, not a finished chart.
    private const int ChartDays = 5;
    private const int SeriesCount = 3;
    private const double ChartWidth = 300;
    private const double ChartHeight = 116;
    private const double ChartLabelGutter = 40; // reserved on the right for value callouts
    private const double ChartPlotWidth = ChartWidth - ChartLabelGutter;

    private readonly ShapePath[] _seriesFill = new ShapePath[SeriesCount];
    private readonly ShapePath[] _seriesLine = new ShapePath[SeriesCount];
    private readonly Ellipse[][] _seriesMarkers = new Ellipse[SeriesCount][];
    private readonly TextBlock[] _seriesValueLabel = new TextBlock[SeriesCount];
    private readonly TextBlock[] _chartDateLabels = new TextBlock[ChartDays];
    private Line _chartTodayGuide = null!;
    private Line _chartHoverGuide = null!;
    private Canvas _chartCanvas = null!;
    private Border _chartTooltip = null!;
    private TextBlock _tooltipDate = null!;
    private readonly TextBlock[] _tooltipValues = new TextBlock[SeriesCount];
    private readonly Border[] _hoverChips = new Border[SeriesCount];
    private readonly TextBlock[] _hoverChipTexts = new TextBlock[SeriesCount];
    private readonly List<Point>[] _seriesPoints = new List<Point>[SeriesCount];
    private bool _chartCrosshairHover;

    private Color[] SeriesAccents => new[] { UsdAccent, EurAccent, CnyAccent };

    public void SetChartHoverStyle(bool crosshair)
    {
        _chartCrosshairHover = crosshair;
        // Switching styles mid-hover would leave the other style's elements
        // stuck visible until the next mouse move - just hide everything
        // both styles could have shown and let the next hover redraw fresh.
        _chartTooltip.Visibility = Visibility.Hidden;
        foreach (var chip in _hoverChips) chip.Visibility = Visibility.Hidden;
        _chartHoverGuide.Visibility = Visibility.Hidden;
        SetHoveredMarkerIndex(-1);
    }

    private FrameworkElement BuildChartView()
    {
        var root = new Grid { Width = ChartWidth, VerticalAlignment = VerticalAlignment.Center };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(ChartHeight) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var legend = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 7) };
        AddLegendItem(legend, "USD", UsdAccent);
        AddLegendItem(legend, "EUR", EurAccent);
        AddLegendItem(legend, "CNY", CnyAccent);
        Grid.SetRow(legend, 0);
        root.Children.Add(legend);

        var canvas = new Canvas { Width = ChartWidth, Height = ChartHeight, ClipToBounds = false, Background = Brushes.Transparent };
        _chartCanvas = canvas;
        Grid.SetRow(canvas, 1);

        // Two very faint horizontal guides purely for depth/premium feel -
        // not calibrated to any value, just breaks up the empty background.
        for (var g = 1; g <= 2; g++)
        {
            canvas.Children.Add(new Line
            {
                X1 = 0, X2 = ChartPlotWidth,
                Y1 = ChartHeight * g / 3.0, Y2 = ChartHeight * g / 3.0,
                Stroke = new SolidColorBrush(Color.FromArgb(18, 255, 255, 255)),
                StrokeThickness = 1
            });
        }

        _chartTodayGuide = new Line
        {
            Y1 = 0, Y2 = ChartHeight,
            Stroke = new SolidColorBrush(Color.FromArgb(22, 255, 255, 255)),
            StrokeThickness = 1,
            StrokeDashArray = new DoubleCollection { 2, 2 }
        };
        canvas.Children.Add(_chartTodayGuide);

        _chartHoverGuide = new Line
        {
            Y1 = 0, Y2 = ChartHeight,
            Stroke = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)),
            StrokeThickness = 1,
            Visibility = Visibility.Hidden,
            IsHitTestVisible = false
        };
        canvas.Children.Add(_chartHoverGuide);

        var accents = SeriesAccents;
        for (var s = 0; s < SeriesCount; s++)
        {
            var accent = accents[s];
            _seriesFill[s] = new ShapePath
            {
                Fill = new LinearGradientBrush(
                    new GradientStopCollection
                    {
                        new GradientStop(Color.FromArgb(70, accent.R, accent.G, accent.B), 0),
                        new GradientStop(Color.FromArgb(0, accent.R, accent.G, accent.B), 1)
                    },
                    new Point(0, 0), new Point(0, 1))
            };
            canvas.Children.Add(_seriesFill[s]);
        }
        for (var s = 0; s < SeriesCount; s++)
        {
            var accent = accents[s];
            _seriesLine[s] = new ShapePath
            {
                Stroke = new SolidColorBrush(accent),
                StrokeThickness = 2.2,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round
            };
            canvas.Children.Add(_seriesLine[s]);

            _seriesMarkers[s] = new Ellipse[ChartDays];
            for (var d = 0; d < ChartDays; d++)
            {
                var marker = new Ellipse
                {
                    Width = 5.5, Height = 5.5,
                    Fill = new SolidColorBrush(accent),
                    Stroke = new SolidColorBrush(Color.FromArgb(248, 12, 12, 14)),
                    StrokeThickness = 1.2,
                    Visibility = Visibility.Hidden
                };
                _seriesMarkers[s][d] = marker;
                canvas.Children.Add(marker);
            }

            _seriesValueLabel[s] = new TextBlock
            {
                FontSize = 10.5, FontWeight = FontWeights.Bold, FontFamily = ValueFont,
                Foreground = new SolidColorBrush(accent)
            };
            canvas.Children.Add(_seriesValueLabel[s]);
        }

        // Value/marker callout for the point nearest the cursor - hovering
        // (there's no click target, just a plain hover-follow like every
        // real charting app) shows the exact date and all three prices for
        // that day instead of forcing a guess from the curve's shape alone.
        _chartTooltip = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(26, 26, 29)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(9, 6, 10, 7),
            Visibility = Visibility.Hidden,
            IsHitTestVisible = false
        };
        var tooltipStack = new StackPanel();
        _tooltipDate = new TextBlock
        {
            FontSize = 11, FontFamily = LabelFont, FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromArgb(190, 235, 235, 240)),
            Margin = new Thickness(0, 0, 0, 2)
        };
        tooltipStack.Children.Add(_tooltipDate);
        tooltipStack.Children.Add(new Border
        {
            Height = 1, Background = new SolidColorBrush(Color.FromArgb(35, 255, 255, 255)),
            Margin = new Thickness(0, 0, 0, 3)
        });
        var accents2 = SeriesAccents;
        var seriesSymbols = new[] { "$", "€", "¥" };
        for (var s = 0; s < SeriesCount; s++)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0.5, 0, 0.5) };
            row.Children.Add(new TextBlock
            {
                Text = seriesSymbols[s], FontSize = 13, FontWeight = FontWeights.Bold, FontFamily = ValueFont,
                Foreground = new SolidColorBrush(accents2[s]),
                Margin = new Thickness(0, 0, 3, 0), VerticalAlignment = VerticalAlignment.Center
            });
            _tooltipValues[s] = new TextBlock
            {
                FontSize = 12.5, FontFamily = ValueFont, FontWeight = FontWeights.SemiBold,
                Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center
            };
            row.Children.Add(_tooltipValues[s]);
            tooltipStack.Children.Add(row);
        }
        _chartTooltip.Child = tooltipStack;
        canvas.Children.Add(_chartTooltip);

        // Variant E ("crosshair") - no box at all, just three small colored
        // chips sitting right next to each line's own point. Built here
        // alongside the tooltip box; OnChartCanvasMouseMove shows whichever
        // one _chartCrosshairHover currently points to and keeps the other
        // hidden.
        var hoverChipSymbols = new[] { "$", "€", "¥" };
        for (var s = 0; s < SeriesCount; s++)
        {
            var chipRow = new StackPanel { Orientation = Orientation.Horizontal };
            chipRow.Children.Add(new TextBlock
            {
                Text = hoverChipSymbols[s],
                FontSize = 10, FontFamily = ValueFont, FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(12, 12, 14)),
                Margin = new Thickness(0, 0, 3, 0),
                Opacity = 0.75
            });
            _hoverChipTexts[s] = new TextBlock
            {
                FontSize = 10, FontFamily = ValueFont, FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(12, 12, 14))
            };
            chipRow.Children.Add(_hoverChipTexts[s]);
            _hoverChips[s] = new Border
            {
                Background = new SolidColorBrush(accents2[s]),
                CornerRadius = new CornerRadius(9),
                Padding = new Thickness(6, 2, 6, 2),
                Visibility = Visibility.Hidden,
                IsHitTestVisible = false,
                Child = chipRow
            };
            canvas.Children.Add(_hoverChips[s]);
        }

        canvas.MouseMove += OnChartCanvasMouseMove;
        canvas.MouseLeave += (_, _) =>
        {
            _chartHoverGuide.Visibility = Visibility.Hidden;
            _chartTooltip.Visibility = Visibility.Hidden;
            foreach (var chip in _hoverChips) chip.Visibility = Visibility.Hidden;
            SetHoveredMarkerIndex(-1);
        };

        root.Children.Add(canvas);

        // Date labels live in their own Canvas, positioned at the EXACT same
        // X as each point on the plot above (not spread across 5 equal Grid
        // columns) - the old Grid-column approach edge-aligned the first/
        // last labels but center-aligned the middle ones inside differently
        // sized effective slots, so the gaps between labels weren't uniform
        // and didn't line up with what they were labeling either.
        var dateCanvas = new Canvas { Width = ChartPlotWidth, Height = 12, Margin = new Thickness(0, 6, ChartLabelGutter, 0) };
        for (var i = 0; i < ChartDays; i++)
        {
            var label = new TextBlock
            {
                FontSize = 9,
                FontFamily = LabelFont,
                Foreground = new SolidColorBrush(Color.FromArgb(120, 235, 235, 240))
            };
            _chartDateLabels[i] = label;
            dateCanvas.Children.Add(label);
        }
        Grid.SetRow(dateCanvas, 2);
        root.Children.Add(dateCanvas);

        return root;
    }

    private static void AddLegendItem(Panel parent, string label, Color accent)
    {
        var item = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(parent.Children.Count == 0 ? 0 : 10, 0, 0, 0) };
        item.Children.Add(new Ellipse { Width = 6, Height = 6, Fill = new SolidColorBrush(accent), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 4, 0) });
        item.Children.Add(new TextBlock
        {
            Text = label, FontSize = 9, FontFamily = LabelFont,
            Foreground = new SolidColorBrush(Color.FromArgb(150, 235, 235, 240)),
            VerticalAlignment = VerticalAlignment.Center
        });
        parent.Children.Add(item);
    }

    // Currency codes/symbols/accents for the calculator's two selector
    // chips - RUB is the base currency, so it always converts 1:1 and
    // never gets its own trend chart (see LoadCalcRangeAsync).
    private static readonly string[] CalcCodes = { "USD", "EUR", "CNY", "AED", "RUB", "TRY" };
    private static readonly string[] CalcSymbols = { "$", "€", "¥", "Dh", "₽", "₺" };
    private Color CalcAccent(int i) => i switch { 0 => UsdAccent, 1 => EurAccent, 2 => CnyAccent, 3 => AedAccent, 5 => TryAccent, _ => Colors.White };

    private int _calcFromCurrency;
    private const int CalcRubIndex = 4;
    private int _calcToCurrency = CalcRubIndex;
    private CbrHistoryPoint? _calcLatest;
    private TextBox _calcAmountBox = null!;
    private TextBlock _calcResultText = null!;
    private Action<int> _calcFromRefresh = null!;
    private Action<int> _calcToRefresh = null!;
    private TextBlock _calcRateText = null!;

    private FrameworkElement BuildCalculatorView()
    {
        var root = new StackPanel { Width = ChartWidth, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(CenteredTabLeftMargin, 0, 0, 0) };

        // One card behind the whole calculator (both amount fields, the
        // swap button, rate/change lines) - visually separates it from the
        // range chart below, same idea as Yandex's converter. Each amount
        // row ALSO gets its own, slightly lighter field background inside
        // that card - without one, the TextBox (transparent by design, so
        // it doesn't paint its own chrome - see its template comment
        // below) had nothing behind it at all, reading as "invisible".
        var card = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(16, 255, 255, 255)),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(10, 7, 10, 7)
        };
        var cardContent = new StackPanel();
        card.Child = cardContent;

        var amountField = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(28, 255, 255, 255)),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(8, 5, 6, 5),
            Margin = new Thickness(0, 0, 0, 3)
        };
        var amountRow = new Grid();
        amountRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        amountRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _calcAmountBox = new TextBox
        {
            Text = "1",
            FontSize = 17, FontWeight = FontWeights.SemiBold, FontFamily = ValueFont,
            Foreground = Brushes.White, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            CaretBrush = Brushes.White, SelectionBrush = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)),
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(0)
        };
        // A plain TextBox's default template paints its own chrome on
        // focus (a solid, theme-colored background board underneath the
        // text) regardless of the Background set above - that's what
        // showed as "goes white and the text disappears" the instant it
        // was clicked. Stripping the template down to just the bare
        // ScrollViewer TextBoxBase actually needs (PART_ContentHost) means
        // there's no chrome left to paint - Background/BorderThickness
        // above are the only things left drawing anything, in every state.
        var bareTemplate = new ControlTemplate(typeof(TextBox));
        var contentHost = new FrameworkElementFactory(typeof(ScrollViewer));
        contentHost.Name = "PART_ContentHost";
        bareTemplate.VisualTree = contentHost;
        _calcAmountBox.Template = bareTemplate;
        _calcAmountBox.TextChanged += (_, _) => RecalculateCalculator();
        _calcAmountBox.PreviewTextInput += (_, e) => e.Handled = !IsValidAmountInput(_calcAmountBox.Text, e.Text);
        DataObject.AddPastingHandler(_calcAmountBox, (_, e) =>
        {
            var pasted = e.DataObject.GetData(DataFormats.UnicodeText) as string ?? "";
            if (pasted.Length == 0 || !pasted.All("0123456789.,".Contains)) e.CancelCommand();
        });
        Grid.SetColumn(_calcAmountBox, 0);
        amountRow.Children.Add(_calcAmountBox);
        var fromChip = BuildCurrencyChip(SwapperCurrencies, _calcFromCurrency, c => { _calcFromCurrency = c; RecalculateCalculator(); }, out _calcFromRefresh);
        _calcFromChip = fromChip;
        Grid.SetColumn(fromChip, 1);
        amountRow.Children.Add(fromChip);
        amountField.Child = amountRow;
        _calcAmountField = amountField;
        cardContent.Children.Add(amountField);

        var swapButton = new Border
        {
            Width = 20, Height = 20, CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
            HorizontalAlignment = HorizontalAlignment.Center,
            Cursor = Cursors.Hand,
            Margin = new Thickness(0),
            Child = new ShapePath
            {
                Data = Geometry.Parse("M1,4 L10,4 M7,1 L10,4 L7,7 M11,8 L2,8 M5,5 L2,8 L5,11"),
                Stroke = new SolidColorBrush(Color.FromArgb(200, 235, 235, 240)),
                StrokeThickness = 1.3,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                Stretch = Stretch.Uniform,
                Width = 12, Height = 12,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            }
        };
        swapButton.MouseLeftButtonDown += (_, e) => { e.Handled = true; _ = PulseSwapAsync(swapButton); };
        cardContent.Children.Add(swapButton);

        var resultField = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(28, 255, 255, 255)),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(8, 5, 6, 5),
            Margin = new Thickness(0, 3, 0, 5)
        };
        var resultRow = new Grid();
        resultRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        resultRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        resultRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _calcWipe = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(70, Wpf.Ui.Appearance.ApplicationAccentColorManager.SystemAccent.R, Wpf.Ui.Appearance.ApplicationAccentColorManager.SystemAccent.G, Wpf.Ui.Appearance.ApplicationAccentColorManager.SystemAccent.B)),
            CornerRadius = new CornerRadius(9),
            Margin = new Thickness(-8, -5, -6, -5),
            Opacity = 0,
            IsHitTestVisible = false,
            RenderTransformOrigin = new Point(0, 0.5),
            RenderTransform = _calcWipeScale
        };
        Grid.SetColumnSpan(_calcWipe, 3);
        resultRow.Children.Add(_calcWipe);
        _calcResultText = new TextBlock
        {
            FontSize = 17, FontWeight = FontWeights.SemiBold, FontFamily = ValueFont,
            Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis
        };
        Grid.SetColumn(_calcResultText, 0);
        _calcResultText.Cursor = Cursors.Hand;
        _calcResultText.PreviewMouseDown += (_, e) => e.Handled = true;
        _calcResultText.MouseLeftButtonUp += (_, e) => { e.Handled = true; CopyCalcResult(); };
        resultRow.Children.Add(_calcResultText);

        _calcCopyIcon = new ShapePath
        {
            Data = Geometry.Parse(CopyIconData),
            Fill = new SolidColorBrush(Color.FromArgb(150, 235, 235, 240)),
            Stretch = Stretch.Uniform,
            Width = 12, Height = 12,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = _calcCopyScale,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };
        _calcCheckIcon = new ShapePath
        {
            Data = Geometry.Parse("M2.6,7.3 L5.8,10.5 L11.4,3.8"),
            Stroke = new SolidColorBrush(Wpf.Ui.Appearance.ApplicationAccentColorManager.SystemAccent),
            StrokeThickness = 1.6,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeDashArray = new DoubleCollection { CheckDash, 20 },
            StrokeDashOffset = CheckDash,
            Width = 14, Height = 14,
            Opacity = 0,
            IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };
        var copyGlyphs = new Grid();
        copyGlyphs.Children.Add(_calcCopyIcon);
        copyGlyphs.Children.Add(_calcCheckIcon);
        var copyButton = new Border
        {
            Width = 24, Height = 24, CornerRadius = new CornerRadius(8),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            Margin = new Thickness(0, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = copyGlyphs
        };
        copyButton.MouseEnter += (_, _) => copyButton.Background = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255));
        copyButton.MouseLeave += (_, _) => copyButton.Background = Brushes.Transparent;
        copyButton.PreviewMouseDown += (_, e) => e.Handled = true;
        copyButton.MouseLeftButtonUp += (_, e) => { e.Handled = true; CopyCalcResult(); };
        Grid.SetColumn(copyButton, 1);
        resultRow.Children.Add(copyButton);
        var toChip = BuildCurrencyChip(SwapperCurrencies, _calcToCurrency, c => { _calcToCurrency = c; RecalculateCalculator(); }, out _calcToRefresh);
        _calcToChip = toChip;
        Grid.SetColumn(toChip, 2);
        resultRow.Children.Add(toChip);
        resultField.Child = resultRow;
        _calcResultField = resultField;
        cardContent.Children.Add(resultField);

        _calcRateText = new TextBlock
        {
            FontSize = 11, FontFamily = LabelFont, Foreground = new SolidColorBrush(Color.FromArgb(190, 235, 235, 240)),
            Margin = new Thickness(2, 0, 0, 1)
        };
        cardContent.Children.Add(_calcRateText);

        root.Children.Add(card);
        return root;
    }

    private int _rangeCurrency;
    private bool _rangeIsYear;
    private List<(DateTime Date, double Rate)>? _rangePoints;
    private readonly List<Point> _rangeChartPoints = new();
    private Color _rangeLineColor = Colors.White;
    private Action<int> _rangeChipRefresh = null!;
    private TextBlock _rangeRateText = null!;
    private TextBlock _rangeChangeText = null!;
    private Border _rangeMonthButton = null!;
    private Border _rangeYearButton = null!;
    private Canvas _rangeCanvas = null!;
    private ShapePath _rangeLine = null!;
    private ShapePath _rangeFill = null!;
    private TextBlock _rangeEmptyText = null!;
    private MorphSpinner _rangeSpinner = null!;
    private Line _rangeHoverGuide = null!;
    private Ellipse _rangeHoverMarker = null!;
    private Ellipse _rangeEndMarker = null!;
    private Border _rangeHoverPill = null!;
    private TextBlock _rangeHoverPillText = null!;
    private const double RangeChartHeight = 92;
    private const double RangeChartWidth = ChartWidth - 34;

    private FrameworkElement BuildRangeView()
    {
        var root = new StackPanel { Width = ChartWidth, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(CenteredTabLeftMargin, 0, 0, 0) };

        var header = new Grid { Margin = new Thickness(0, 0, 0, 6) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var chip = BuildCurrencyChip(ChartCurrencies, _rangeCurrency, c => { _rangeCurrency = c; _ = LoadRangeAsync(); }, out _rangeChipRefresh);
        chip.HorizontalAlignment = HorizontalAlignment.Left;
        Grid.SetColumn(chip, 0);
        header.Children.Add(chip);

        _rangeMonthButton = BuildRangeSegment("Month", isYear: false);
        _rangeYearButton = BuildRangeSegment("Year", isYear: true);
        var segRow = new StackPanel { Orientation = Orientation.Horizontal };
        segRow.Children.Add(_rangeMonthButton);
        segRow.Children.Add(_rangeYearButton);
        var segSwitch = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(20, 255, 255, 255)),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(2),
            VerticalAlignment = VerticalAlignment.Center,
            Child = segRow
        };
        Grid.SetColumn(segSwitch, 1);
        header.Children.Add(segSwitch);
        root.Children.Add(header);

        var rateRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 0, 0, 5) };
        _rangeRateText = new TextBlock
        {
            FontSize = 16, FontWeight = FontWeights.SemiBold, FontFamily = ValueFont,
            Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Bottom
        };
        _rangeChangeText = new TextBlock
        {
            FontSize = 10, FontFamily = LabelFont, Margin = new Thickness(8, 0, 0, 2),
            VerticalAlignment = VerticalAlignment.Bottom
        };
        rateRow.Children.Add(_rangeRateText);
        rateRow.Children.Add(_rangeChangeText);
        root.Children.Add(rateRow);

        var chartHost = new Grid { Height = RangeChartHeight, Width = RangeChartWidth, HorizontalAlignment = HorizontalAlignment.Left };
        _rangeCanvas = new Canvas { Width = RangeChartWidth, Height = RangeChartHeight, Background = Brushes.Transparent };
        for (var g = 1; g <= 2; g++)
        {
            _rangeCanvas.Children.Add(new Line
            {
                X1 = 0, X2 = RangeChartWidth,
                Y1 = RangeChartHeight * g / 3.0, Y2 = RangeChartHeight * g / 3.0,
                Stroke = new SolidColorBrush(Color.FromArgb(18, 255, 255, 255)),
                StrokeThickness = 1
            });
        }
        _rangeFill = new ShapePath { StrokeThickness = 0 };
        _rangeLine = new ShapePath { StrokeThickness = 2.2, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round };
        _rangeCanvas.Children.Add(_rangeFill);
        _rangeCanvas.Children.Add(_rangeLine);
        _rangeHoverGuide = new Line
        {
            Y1 = 0, Y2 = RangeChartHeight,
            Stroke = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)),
            StrokeThickness = 1, Visibility = Visibility.Hidden, IsHitTestVisible = false
        };
        _rangeCanvas.Children.Add(_rangeHoverGuide);
        _rangeEndMarker = new Ellipse
        {
            Width = 5.5, Height = 5.5, Visibility = Visibility.Hidden, IsHitTestVisible = false,
            Stroke = new SolidColorBrush(Color.FromArgb(248, 12, 12, 14)), StrokeThickness = 1.2
        };
        _rangeCanvas.Children.Add(_rangeEndMarker);
        _rangeHoverMarker = new Ellipse
        {
            Width = 7, Height = 7, Visibility = Visibility.Hidden, IsHitTestVisible = false,
            Stroke = new SolidColorBrush(Color.FromArgb(248, 12, 12, 14)), StrokeThickness = 1.2
        };
        _rangeCanvas.Children.Add(_rangeHoverMarker);
        _rangeHoverPillText = new TextBlock { FontSize = 10.5, FontFamily = LabelFont, Foreground = Brushes.White };
        _rangeHoverPill = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(26, 26, 29)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(7, 2, 7, 3),
            Visibility = Visibility.Hidden, IsHitTestVisible = false,
            Child = _rangeHoverPillText
        };
        _rangeCanvas.Children.Add(_rangeHoverPill);
        _rangeCanvas.MouseMove += OnRangeChartMouseMove;
        _rangeCanvas.MouseLeave += (_, _) => HideRangeHover();
        chartHost.Children.Add(_rangeCanvas);
        _rangeEmptyText = new TextBlock
        {
            Text = "No data", FontSize = 11, FontFamily = LabelFont,
            Foreground = new SolidColorBrush(Color.FromArgb(120, 235, 235, 240)),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed
        };
        chartHost.Children.Add(_rangeEmptyText);
        _rangeSpinner = new MorphSpinner
        {
            Width = 34, Height = 34,
            Foreground = new SolidColorBrush(Wpf.Ui.Appearance.ApplicationAccentColorManager.SystemAccent),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };
        chartHost.Children.Add(_rangeSpinner);
        root.Children.Add(chartHost);

        UpdateRangeSegments();
        return root;
    }

    private Border BuildRangeSegment(string label, bool isYear)
    {
        var text = new TextBlock { Text = label, FontSize = 10, FontFamily = LabelFont, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center };
        var button = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(11, 2, 11, 2),
            Cursor = Cursors.Hand,
            Child = text
        };
        button.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            if (_rangeIsYear == isYear) return;
            _rangeIsYear = isYear;
            UpdateRangeSegments();
            _ = LoadRangeAsync();
        };
        return button;
    }

    private void UpdateRangeSegments()
    {
        var activeBg = new SolidColorBrush(Color.FromArgb(70, 255, 255, 255));
        var inactiveText = new SolidColorBrush(Color.FromArgb(150, 235, 235, 240));
        _rangeMonthButton.Background = !_rangeIsYear ? activeBg : Brushes.Transparent;
        _rangeYearButton.Background = _rangeIsYear ? activeBg : Brushes.Transparent;
        ((TextBlock)_rangeMonthButton.Child).Foreground = !_rangeIsYear ? Brushes.White : inactiveText;
        ((TextBlock)_rangeYearButton.Child).Foreground = _rangeIsYear ? Brushes.White : inactiveText;
    }

    private async Task LoadRangeAsync()
    {
        var requestedCurrency = _rangeCurrency;
        var requestedYear = _rangeIsYear;
        _rangePoints = null;
        RenderRangeChart();

        var to = DateTime.Today;
        var from = to.AddDays(requestedYear ? -365 : -30);
        var points = await _rates.FetchDynamicRangeAsync(CalcCodes[requestedCurrency], from, to);

        if (requestedCurrency != _rangeCurrency || requestedYear != _rangeIsYear) return;

        _rangePoints = points;
        RenderRangeChart();
        RevealRangeChart();
    }

    private void RevealRangeChart()
    {
        if (!Motion.Enabled || _rangePoints is not { Count: >= 2 }) return;

        var shift = new TranslateTransform(-0.2, 0);
        var mask = new LinearGradientBrush(Colors.Black, Colors.Transparent, new Point(0, 0.5), new Point(0.2, 0.5))
        {
            RelativeTransform = shift
        };
        _rangeCanvas.OpacityMask = mask;

        var sweep = Motion.Tween(-0.2, 1.0, 800, new CubicEase { EasingMode = EasingMode.EaseInOut });
        sweep.Completed += (_, _) =>
        {
            if (ReferenceEquals(_rangeCanvas.OpacityMask, mask)) _rangeCanvas.OpacityMask = null;
        };
        shift.BeginAnimation(TranslateTransform.XProperty, sweep);

        var easeOut = new CubicEase { EasingMode = EasingMode.EaseOut };
        _rangeRateText.BeginAnimation(OpacityProperty, Motion.Tween(0, 1, 320, easeOut));
        _rangeChangeText.BeginAnimation(OpacityProperty, Motion.Tween(0, 1, 380, easeOut, 80));
        _rangeEndMarker.BeginAnimation(OpacityProperty, Motion.Tween(0, 1, 220, easeOut, 650));

        Motion.Settle(1500, () =>
        {
            if (ReferenceEquals(_rangeCanvas.OpacityMask, mask)) _rangeCanvas.OpacityMask = null;
            Motion.Clear(_rangeRateText, OpacityProperty, 1.0);
            Motion.Clear(_rangeChangeText, OpacityProperty, 1.0);
            Motion.Clear(_rangeEndMarker, OpacityProperty, 1.0);
        });
    }

    private void RenderRangeChart()
    {
        if (_rangePoints is not { Count: >= 2 } points)
        {
            var loading = _rangePoints == null;
            _rangeSpinner.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
            _rangeEmptyText.Visibility = loading ? Visibility.Collapsed : Visibility.Visible;
            _rangeLine.Data = null;
            _rangeFill.Data = null;
            _rangeChartPoints.Clear();
            _rangeEndMarker.Visibility = Visibility.Hidden;
            HideRangeHover();
            _rangeRateText.Text = "";
            _rangeChangeText.Text = "";
            return;
        }

        _rangeEmptyText.Visibility = Visibility.Collapsed;
        _rangeSpinner.Visibility = Visibility.Collapsed;

        const double pad = 3;
        var height = RangeChartHeight;
        var width = RangeChartWidth;
        var min = points.Min(p => p.Rate);
        var max = points.Max(p => p.Rate);
        var range = Math.Max(max - min, 0.0001);

        _rangeChartPoints.Clear();
        for (var i = 0; i < points.Count; i++)
            _rangeChartPoints.Add(new Point(width * i / (points.Count - 1), pad + (1 - (points[i].Rate - min) / range) * (height - 2 * pad)));

        var rising = points[^1].Rate >= points[0].Rate;
        var lineColor = rising ? TrendUpColor : TrendDownColor;
        _rangeLineColor = lineColor;
        _rangeLine.Data = BuildSmoothGeometry(_rangeChartPoints, fillToY: null);
        _rangeLine.Stroke = new SolidColorBrush(lineColor);
        _rangeFill.Data = BuildSmoothGeometry(_rangeChartPoints, fillToY: height);
        _rangeFill.Fill = new LinearGradientBrush(
            new GradientStopCollection
            {
                new GradientStop(Color.FromArgb(70, lineColor.R, lineColor.G, lineColor.B), 0),
                new GradientStop(Color.FromArgb(0, lineColor.R, lineColor.G, lineColor.B), 1)
            },
            new Point(0, 0), new Point(0, 1));

        var last = _rangeChartPoints[^1];
        _rangeEndMarker.Fill = new SolidColorBrush(lineColor);
        Canvas.SetLeft(_rangeEndMarker, last.X - 2.75);
        Canvas.SetTop(_rangeEndMarker, last.Y - 2.75);
        _rangeEndMarker.Visibility = Visibility.Visible;
        HideRangeHover();

        var delta = points[^1].Rate - points[0].Rate;
        var deltaPct = points[0].Rate == 0 ? 0 : delta / points[0].Rate * 100;
        var arrow = delta >= 0 ? "▲" : "▼";
        _rangeRateText.Text = $"{points[^1].Rate:0.00} ₽";
        _rangeChangeText.Text = $"{arrow} {delta:+0.00;-0.00} ₽  {deltaPct:+0.0;-0.0}% past {(_rangeIsYear ? "year" : "month")}";
        _rangeChangeText.Foreground = new SolidColorBrush(lineColor);
    }

    private void HideRangeHover()
    {
        _rangeHoverGuide.Visibility = Visibility.Hidden;
        _rangeHoverMarker.Visibility = Visibility.Hidden;
        _rangeHoverPill.Visibility = Visibility.Hidden;
    }

    private void OnRangeChartMouseMove(object sender, MouseEventArgs e)
    {
        if (_rangePoints is not { Count: >= 2 } points || _rangeChartPoints.Count != points.Count) return;

        var n = points.Count;
        var idx = Math.Clamp((int)Math.Round(e.GetPosition(_rangeCanvas).X / (RangeChartWidth / (n - 1))), 0, n - 1);
        var pt = _rangeChartPoints[idx];

        _rangeHoverGuide.X1 = pt.X;
        _rangeHoverGuide.X2 = pt.X;
        _rangeHoverGuide.Visibility = Visibility.Visible;

        _rangeHoverMarker.Fill = new SolidColorBrush(_rangeLineColor);
        Canvas.SetLeft(_rangeHoverMarker, pt.X - 3.5);
        Canvas.SetTop(_rangeHoverMarker, pt.Y - 3.5);
        _rangeHoverMarker.Visibility = Visibility.Visible;

        var date = points[idx].Date.ToString("d MMM yyyy", System.Globalization.CultureInfo.GetCultureInfo("en-US"));
        _rangeHoverPillText.Text = $"{date}   {points[idx].Rate:0.00}";
        _rangeHoverPill.Visibility = Visibility.Visible;
        _rangeHoverPill.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = _rangeHoverPill.DesiredSize;
        Canvas.SetLeft(_rangeHoverPill, Math.Clamp(pt.X - size.Width / 2, 0, RangeChartWidth - size.Width));
        Canvas.SetTop(_rangeHoverPill, pt.Y < size.Height + 8 ? RangeChartHeight - size.Height : 0);
    }

    private sealed class McTerm
    {
        public double Amount;
        public int Currency = -1;
        public bool IsPercent;
    }

    private readonly List<object> _mcTokens = new();
    private WrapPanel _mcTokenPanel = null!;
    private ScrollViewer _mcScroll = null!;
    private Border _mcGhost = null!;
    private ShapePath _mcCopyIcon = null!;
    private ShapePath _mcCheckIcon = null!;
    private Border _mcCopyButton = null!;
    private Border _mcWipe = null!;
    private readonly ScaleTransform _mcWipeScale = new(0, 1);
    private readonly ScaleTransform _mcCopyScale = new(1, 1);
    private DispatcherTimer? _mcCopyTimer;
    private bool _mcCopyBusy;

    private void McCopyResult()
    {
        if (McEvaluateResult() is not { } value) return;
        try { Clipboard.SetText(FormatTotal(value)); } catch { return; }
        if (_mcCopyBusy) return;
        _mcCopyBusy = true;

        _mcWipe.BeginAnimation(OpacityProperty, null);
        _mcWipe.Opacity = 1;
        _mcWipeScale.BeginAnimation(ScaleTransform.ScaleXProperty, Motion.Tween(0, 1, 460, new CubicEase { EasingMode = EasingMode.EaseOut }));
        _mcWipe.BeginAnimation(OpacityProperty, Motion.Tween(1, 0, 320, new CubicEase { EasingMode = EasingMode.EaseIn }, delayMs: 420));

        var easeIn = new CubicEase { EasingMode = EasingMode.EaseIn };
        _mcCopyScale.BeginAnimation(ScaleTransform.ScaleXProperty, Motion.Tween(1, 0, 160, easeIn));
        _mcCopyScale.BeginAnimation(ScaleTransform.ScaleYProperty, Motion.Tween(1, 0, 160, easeIn));
        _mcCheckIcon.BeginAnimation(OpacityProperty, null);
        _mcCheckIcon.Opacity = 1;
        _mcCheckIcon.BeginAnimation(ShapePath.StrokeDashOffsetProperty, Motion.Tween(CheckDash, 0, 320, delayMs: 120));

        _mcCopyTimer?.Stop();
        _mcCopyTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1100) };
        _mcCopyTimer.Tick += (_, _) =>
        {
            _mcCopyTimer!.Stop();
            _mcCheckIcon.BeginAnimation(OpacityProperty, Motion.Tween(1, 0, 140));
            _mcCopyScale.BeginAnimation(ScaleTransform.ScaleXProperty, Motion.Spring(0, 1, Motion.Bouncy));
            _mcCopyScale.BeginAnimation(ScaleTransform.ScaleYProperty, Motion.Spring(0, 1, Motion.Bouncy));

            var release = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(320) };
            release.Tick += (_, _) =>
            {
                release.Stop();
                _mcCheckIcon.BeginAnimation(ShapePath.StrokeDashOffsetProperty, null);
                _mcCheckIcon.StrokeDashOffset = CheckDash;
                _mcCopyBusy = false;
            };
            release.Start();
        };
        _mcCopyTimer.Start();

        if (Motion.Enabled) Motion.Settle(1800, () =>
        {
            Motion.Clear(_mcWipe, OpacityProperty, 0.0);
            Motion.Clear(_mcWipeScale, ScaleTransform.ScaleXProperty, 0.0);
            Motion.Clear(_mcCopyScale, ScaleTransform.ScaleXProperty, 1.0);
            Motion.Clear(_mcCopyScale, ScaleTransform.ScaleYProperty, 1.0);
            Motion.Clear(_mcCheckIcon, OpacityProperty, 0.0);
            Motion.Clear(_mcCheckIcon, ShapePath.StrokeDashOffsetProperty, CheckDash);
            _mcCopyBusy = false;
        });
    }
    private string _mcLastResultKey = "";
    private string _mcLastNote = "";
    private bool _mcRevealPending;
    private readonly ScaleTransform _mcResultScale = new(1, 1);
    private readonly SolidColorBrush _mcResultBrush = new(Colors.White);
    private readonly TranslateTransform _mcNoteShift = new();

    private static void AnimateNewTerm(StackPanel row, bool hasSymbol)
    {
        if (hasSymbol && row.Children.Count > 1 && row.Children[row.Children.Count - 1] is FrameworkElement symbol)
        {
            var slide = new TranslateTransform(-8, 0);
            symbol.RenderTransform = slide;
            var easeOut = new CubicEase { EasingMode = EasingMode.EaseOut };
            slide.BeginAnimation(TranslateTransform.XProperty, Motion.Tween(-8, 0, 200, easeOut));
            symbol.BeginAnimation(OpacityProperty, Motion.Tween(0, 1, 200, easeOut));
        }
    }

    private static void AnimateNewOperator(FrameworkElement operatorText)
    {
        var slide = new TranslateTransform(-8, 0);
        operatorText.RenderTransform = slide;
        var easeOut = new CubicEase { EasingMode = EasingMode.EaseOut };
        slide.BeginAnimation(TranslateTransform.XProperty, Motion.Tween(-8, 0, 200, easeOut));
        operatorText.BeginAnimation(OpacityProperty, Motion.Tween(0, 1, 200, easeOut));
    }

    private void PopResult()
    {
        _mcResultScale.BeginAnimation(ScaleTransform.ScaleXProperty, Motion.Spring(1.15, 1, Motion.Bouncy));
        _mcResultScale.BeginAnimation(ScaleTransform.ScaleYProperty, Motion.Spring(1.15, 1, Motion.Bouncy));
        _mcResultBrush.BeginAnimation(SolidColorBrush.ColorProperty,
            new ColorAnimation(Color.FromRgb(0, 229, 242), Colors.White, Motion.Span(420)));
    }

    private void SweepClearedTokens()
    {
        if (!Motion.Enabled || _mcTokenPanel.ActualWidth < 1 || _mcTokenPanel.Children.Count <= 1) return;
        var shift = new TranslateTransform();
        _mcGhost.Width = _mcTokenPanel.ActualWidth;
        _mcGhost.Height = _mcTokenPanel.ActualHeight;
        _mcGhost.Background = new VisualBrush(_mcTokenPanel) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top };
        _mcGhost.RenderTransform = shift;
        _mcGhost.Visibility = Visibility.Visible;
        var easeIn = new CubicEase { EasingMode = EasingMode.EaseIn };
        var fade = Motion.Tween(1, 0, 240, easeIn);
        fade.Completed += (_, _) => _mcGhost.Visibility = Visibility.Collapsed;
        shift.BeginAnimation(TranslateTransform.XProperty, Motion.Tween(0, -30, 240, easeIn));
        _mcGhost.BeginAnimation(OpacityProperty, fade);
    }
    private TextBox _mcInput = null!;
    private TextBlock _mcPlaceholder = null!;
    private Border[] _mcChips = null!;
    private StackPanel _mcResultRow = null!;
    private TextBlock _mcResultHint = null!;
    private TextBlock _mcResultText = null!;
    private bool _mcShowResult;
    private int _mcResultCurrency = CalcRubIndex;
    private const string BackspaceIconData = "M22,3H7C6.31,3,5.77,3.35,5.41,3.88L0,12l5.41,8.11C5.77,20.64,6.31,21,7,21h15c1.1,0,2-0.9,2-2V5C24,3.9,23.1,3,22,3z M19,15.59L17.59,17L14,13.41L10.41,17L9,15.59L12.59,12L9,8.41L10.41,7L14,10.59L17.59,7L19,8.41L15.41,12L19,15.59z";

    private Border MakeMcButton(UIElement content, Action onClick, bool accent = false, Func<bool>? selected = null)
    {
        var accentColor = Wpf.Ui.Appearance.ApplicationAccentColorManager.SystemAccent;
        Color Idle() => selected?.Invoke() == true
            ? Color.FromArgb(120, accentColor.R, accentColor.G, accentColor.B)
            : accent ? Color.FromArgb(90, accentColor.R, accentColor.G, accentColor.B) : Color.FromArgb(34, 255, 255, 255);
        Color Hover() => accent || selected?.Invoke() == true
            ? Color.FromArgb(140, accentColor.R, accentColor.G, accentColor.B)
            : Color.FromArgb(64, 255, 255, 255);
        var button = new Border
        {
            CornerRadius = new CornerRadius(9),
            Margin = new Thickness(2),
            Background = new SolidColorBrush(Idle()),
            Cursor = Cursors.Hand,
            Child = content
        };
        var press = new ScaleTransform(1, 1);
        button.RenderTransformOrigin = new Point(0.5, 0.5);
        button.RenderTransform = press;
        void Release(SpringSpec spec)
        {
            if (press.ScaleX >= 0.995) return;
            press.BeginAnimation(ScaleTransform.ScaleXProperty, Motion.Spring(press.ScaleX, 1, spec));
            press.BeginAnimation(ScaleTransform.ScaleYProperty, Motion.Spring(press.ScaleY, 1, spec));
        }
        button.MouseEnter += (_, _) => button.Background = new SolidColorBrush(Hover());
        button.MouseLeave += (_, _) => { button.Background = new SolidColorBrush(Idle()); Release(Motion.Snappy); };
        button.PreviewMouseDown += (_, e) =>
        {
            e.Handled = true;
            press.BeginAnimation(ScaleTransform.ScaleXProperty, Motion.Tween(press.ScaleX, 0.9, 70));
            press.BeginAnimation(ScaleTransform.ScaleYProperty, Motion.Tween(press.ScaleY, 0.9, 70));
        };
        button.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            Release(Motion.Bouncy);
            onClick();
        };
        return button;
    }

    private FrameworkElement BuildMultiCalcView()
    {
        var root = new StackPanel { Width = ChartWidth, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(CenteredTabLeftMargin, 0, 0, 0) };

        _mcTokenPanel = new WrapPanel { Orientation = Orientation.Horizontal, ItemHeight = 24, VerticalAlignment = VerticalAlignment.Center };
        _mcScroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            Focusable = false,
            FocusVisualStyle = null,
            Content = _mcTokenPanel
        };
        _mcInput = new TextBox
        {
            Width = 10, Height = 24,
            FontSize = 16, FontWeight = FontWeights.SemiBold, FontFamily = ValueFont,
            Foreground = Brushes.White, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            CaretBrush = Brushes.White, SelectionBrush = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)),
            VerticalAlignment = VerticalAlignment.Center,
            Padding = new Thickness(0)
        };
        var bareTemplate = new ControlTemplate(typeof(TextBox));
        var contentHost = new FrameworkElementFactory(typeof(ScrollViewer));
        contentHost.Name = "PART_ContentHost";
        contentHost.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
        bareTemplate.VisualTree = contentHost;
        _mcInput.Template = bareTemplate;
        _mcInput.PreviewTextInput += (_, e) =>
        {
            var typedOperator = e.Text switch
            {
                "+" => '+',
                "-" or "\u2212" => '-',
                "*" or "x" or "X" or "\u0445" or "\u0425" or "\u00D7" => '*',
                "/" or ":" or "\u00F7" => '/',
                _ => '\0'
            };
            if (typedOperator != '\0')
            {
                e.Handled = true;
                McAddOperator(typedOperator);
                return;
            }
            if (e.Text == "=")
            {
                e.Handled = true;
                McEquals();
                return;
            }
            if (e.Text == "%")
            {
                e.Handled = true;
                McAddPercent();
                return;
            }
            e.Handled = !IsValidAmountInput(_mcInput.Text, e.Text);
        };
        _mcInput.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { e.Handled = true; McEquals(); }
            else if (e.Key == Key.Escape) { e.Handled = true; McClear(); }
            else if (e.Key == Key.Back && _mcInput.Text.Length == 0) { e.Handled = true; McBackspace(); }
        };
        _mcInput.TextChanged += (_, _) =>
        {
            if (_mcShowResult && _mcInput.Text.Length > 0)
            {
                var typed = _mcInput.Text;
                McClear();
                _mcInput.Text = typed;
                _mcInput.CaretIndex = typed.Length;
            }
            McUpdatePlaceholder();
            var probe = new TextBlock { Text = _mcInput.Text, FontSize = 16, FontWeight = FontWeights.SemiBold, FontFamily = ValueFont };
            probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            _mcInput.Width = Math.Max(10, Math.Ceiling(probe.DesiredSize.Width) + 8);
            _mcScroll.Dispatcher.BeginInvoke(new Action(_mcScroll.ScrollToEnd), DispatcherPriority.Loaded);
        };

        _mcPlaceholder = new TextBlock
        {
            Text = "Type a number, then pick its currency",
            FontSize = 12, FontFamily = LabelFont,
            Foreground = new SolidColorBrush(Color.FromArgb(110, 235, 235, 240)),
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false
        };
        var tokenHost = new Grid();
        _mcGhost = new Border { Visibility = Visibility.Collapsed, IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
        tokenHost.Children.Add(_mcScroll);
        tokenHost.Children.Add(_mcGhost);
        tokenHost.Children.Add(_mcPlaceholder);
        var tokenField = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(28, 255, 255, 255)),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(10, 3, 8, 3),
            Height = 54,
            Child = tokenHost
        };
        tokenField.PreviewMouseDown += (_, e) =>
        {
            e.Handled = true;
            Activate();
            _mcInput.Focus();
            Keyboard.Focus(_mcInput);
        };
        root.Children.Add(tokenField);

        var chips = new System.Windows.Controls.Primitives.UniformGrid { Columns = AllCurrencies.Length, Margin = new Thickness(0, 5, 0, 0) };
        _mcChips = new Border[AllCurrencies.Length];
        for (var i = 0; i < AllCurrencies.Length; i++)
        {
            var idx = AllCurrencies[i];
            var label = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 4) };
            var symbol = CurrencySymbol(idx);
            symbol.VerticalAlignment = VerticalAlignment.Center;
            label.Children.Add(symbol);
            label.Children.Add(new TextBlock
            {
                Text = CalcCodes[idx], FontSize = 10.5, FontFamily = LabelFont, Foreground = Brushes.White,
                Margin = new Thickness(3, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center
            });
            _mcChips[i] = MakeMcButton(label, () => McAddCurrency(idx), selected: () => _mcShowResult && _mcResultCurrency == idx);
            chips.Children.Add(_mcChips[i]);
        }
        root.Children.Add(chips);

        var ops = new System.Windows.Controls.Primitives.UniformGrid { Columns = 8 };
        foreach (var (symbol, op) in new[] { ("+", '+'), ("\u2212", '-'), ("\u00D7", '*'), ("\u00F7", '/') })
        {
            var captured = op;
            ops.Children.Add(MakeMcButton(new TextBlock
            {
                Text = symbol, FontSize = 15, FontFamily = ValueFont, FontWeight = FontWeights.Bold, Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 2, 0, 3)
            }, () => McAddOperator(captured)));
        }
        ops.Children.Add(MakeMcButton(new TextBlock
        {
            Text = "%", FontSize = 14, FontFamily = ValueFont, FontWeight = FontWeights.Bold, Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 2, 0, 3)
        }, McAddPercent));
        ops.Children.Add(MakeMcButton(new ShapePath
        {
            Data = Geometry.Parse(BackspaceIconData),
            Fill = new SolidColorBrush(Color.FromArgb(200, 235, 235, 240)),
            Stretch = Stretch.Uniform, Width = 15, Height = 15,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 5, 0, 5)
        }, McBackspace));
        ops.Children.Add(MakeMcButton(new TextBlock
        {
            Text = "C", FontSize = 13, FontFamily = LabelFont, Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 3, 0, 3)
        }, McClear));
        ops.Children.Add(MakeMcButton(new TextBlock
        {
            Text = "=", FontSize = 16, FontFamily = ValueFont, FontWeight = FontWeights.Bold, Foreground = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 2, 0, 3)
        }, McEquals, accent: true));
        ops.Margin = new Thickness(0, 3, 0, 0);
        root.Children.Add(ops);

        _mcResultText = new TextBlock
        {
            FontSize = 17, FontWeight = FontWeights.SemiBold, FontFamily = ValueFont, Foreground = _mcResultBrush,
            VerticalAlignment = VerticalAlignment.Center, Cursor = Cursors.Hand, TextTrimming = TextTrimming.CharacterEllipsis,
        };
        _mcResultText.PreviewMouseDown += (_, e) => e.Handled = true;
        _mcResultText.MouseLeftButtonUp += (_, e) => { e.Handled = true; McCopyResult(); };
        _mcCopyIcon = new ShapePath
        {
            Data = Geometry.Parse(CopyIconData),
            Fill = new SolidColorBrush(Color.FromArgb(150, 235, 235, 240)),
            Stretch = Stretch.Uniform,
            Width = 12, Height = 12,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = _mcCopyScale,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };
        _mcCheckIcon = new ShapePath
        {
            Data = Geometry.Parse("M2.6,7.3 L5.8,10.5 L11.4,3.8"),
            Stroke = new SolidColorBrush(Wpf.Ui.Appearance.ApplicationAccentColorManager.SystemAccent),
            StrokeThickness = 1.6,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeDashArray = new DoubleCollection { CheckDash, 20 },
            StrokeDashOffset = CheckDash,
            Width = 14, Height = 14,
            Opacity = 0,
            IsHitTestVisible = false,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };
        var mcCopyGlyphs = new Grid();
        mcCopyGlyphs.Children.Add(_mcCopyIcon);
        mcCopyGlyphs.Children.Add(_mcCheckIcon);
        _mcCopyButton = new Border
        {
            Width = 24, Height = 24, CornerRadius = new CornerRadius(8),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            Margin = new Thickness(4, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = mcCopyGlyphs
        };
        _mcCopyButton.MouseEnter += (_, _) => _mcCopyButton.Background = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255));
        _mcCopyButton.MouseLeave += (_, _) => _mcCopyButton.Background = Brushes.Transparent;
        _mcCopyButton.PreviewMouseDown += (_, e) => e.Handled = true;
        _mcCopyButton.MouseLeftButtonUp += (_, e) => { e.Handled = true; McCopyResult(); };
        _mcResultHint = new TextBlock
        {
            FontSize = 11, FontFamily = LabelFont, Foreground = new SolidColorBrush(Color.FromArgb(120, 235, 235, 240)),
            VerticalAlignment = VerticalAlignment.Center, RenderTransform = _mcNoteShift
        };
        _mcResultRow = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var resultField = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(16, 255, 255, 255)),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(10, 0, 8, 0),
            Height = 34,
            Margin = new Thickness(0, 4, 0, 0)
        };
        var mcAccent = Wpf.Ui.Appearance.ApplicationAccentColorManager.SystemAccent;
        _mcWipe = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(70, mcAccent.R, mcAccent.G, mcAccent.B)),
            CornerRadius = new CornerRadius(9),
            Margin = new Thickness(-10, 0, -8, 0),
            Opacity = 0,
            IsHitTestVisible = false,
            RenderTransformOrigin = new Point(0, 0.5),
            RenderTransform = _mcWipeScale
        };
        var resultGrid = new Grid();
        resultGrid.Children.Add(_mcWipe);
        resultGrid.Children.Add(_mcResultHint);
        resultGrid.Children.Add(_mcResultRow);
        resultField.Child = resultGrid;
        root.Children.Add(resultField);

        McRebuildTokens();
        McRenderResult();
        return root;
    }

    private double? McEvaluateResult()
    {
        if (McEvaluateRub() is not { } rub) return null;
        var rate = RateToRub(_mcResultCurrency);
        return rate <= 0 ? null : rub / rate;
    }

    private double? McEvaluateRub() => McEvaluateRub(_mcTokens);

    private double? McEvaluateRub(IList<object> tokens)
    {
        if (tokens.Count == 0 || tokens.Count % 2 == 0) return null;
        var baseCurrency = tokens.OfType<McTerm>().FirstOrDefault(t => !t.IsPercent && t.Currency >= 0)?.Currency ?? CalcRubIndex;

        var values = new List<double>();
        var percentOperand = new List<bool>();
        var operators = new List<char>();
        for (var i = 0; i < tokens.Count; i++)
        {
            if (i % 2 == 1)
            {
                if (tokens[i] is not char op) return null;
                operators.Add(op);
                continue;
            }

            if (tokens[i] is not McTerm term) return null;
            var previous = i > 0 ? operators[^1] : '+';
            var next = i + 1 < tokens.Count && tokens[i + 1] is char following ? following : '\0';
            var multiplicative = previous is '*' or '/';

            if (term.IsPercent)
            {
                var additive = i > 0 && !multiplicative && next is not ('*' or '/');
                values.Add(additive ? term.Amount : term.Amount / 100);
                percentOperand.Add(additive);
                continue;
            }

            percentOperand.Add(false);
            if (multiplicative)
            {
                values.Add(term.Amount);
                continue;
            }

            var rate = RateToRub(term.Currency >= 0 ? term.Currency : baseCurrency);
            if (rate <= 0) return null;
            values.Add(term.Amount * rate);
        }

        var terms = new List<double> { values[0] };
        var termIsPercent = new List<bool> { false };
        var sumOperators = new List<char>();
        for (var i = 0; i < operators.Count; i++)
        {
            if (operators[i] == '*') terms[^1] *= values[i + 1];
            else if (operators[i] == '/')
            {
                if (values[i + 1] == 0) return null;
                terms[^1] /= values[i + 1];
            }
            else
            {
                sumOperators.Add(operators[i]);
                terms.Add(values[i + 1]);
                termIsPercent.Add(percentOperand[i + 1]);
            }
        }

        var result = terms[0];
        for (var i = 0; i < sumOperators.Count; i++)
        {
            var operand = termIsPercent[i + 1] ? result * terms[i + 1] / 100 : terms[i + 1];
            result = sumOperators[i] == '+' ? result + operand : result - operand;
        }
        return result;
    }

    private static string FormatTotal(double value) =>
        Math.Abs(value) >= 1 ? value.ToString("#,##0.00", CalcNumberFormat) : FormatCalcNumber(value);

    private static string FormatPlain(double value) =>
        value == Math.Floor(value) ? value.ToString("#,##0", CalcNumberFormat) : FormatCalcNumber(value);

    private int McBaseCurrency() =>
        _mcTokens.OfType<McTerm>().FirstOrDefault(t => !t.IsPercent && t.Currency >= 0)?.Currency ?? CalcRubIndex;

    private string? McPercentNote()
    {
        var index = -1;
        for (var i = _mcTokens.Count - 1; i >= 0; i -= 2)
        {
            if (_mcTokens[i] is McTerm { IsPercent: true }) { index = i; break; }
        }
        if (index < 0) return null;

        var term = (McTerm)_mcTokens[index];
        var percent = FormatPlain(term.Amount);
        var previous = index > 0 && _mcTokens[index - 1] is char before ? before : '+';
        var next = index + 1 < _mcTokens.Count && _mcTokens[index + 1] is char after ? after : '\0';
        var multiplicative = previous is '*' or '/' || next is '*' or '/';
        if (index == 0 || multiplicative)
            return $"{percent}% = {FormatPlain(term.Amount / 100)} as a plain number";

        var baseCurrency = McBaseCurrency();
        var rate = RateToRub(baseCurrency);
        if (rate <= 0 || McEvaluateRub(_mcTokens.Take(index - 1).ToList()) is not { } rub) return null;

        var subtotal = rub / rate;
        var delta = subtotal * term.Amount / 100;
        var sign = previous == '+' ? "+" : "\u2212";
        var symbol = CalcSymbols[baseCurrency];
        return $"{percent}% of {FormatPlain(subtotal)} {symbol} = {sign}{FormatPlain(delta)} {symbol}";
    }

    private bool McCommitInput(int currency, bool percent = false)
    {
        var text = _mcInput.Text.Replace(',', '.');
        if (text.Length == 0 || !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var amount)) return false;
        if (_mcTokens.Count % 2 == 1) return false;
        _mcTokens.Add(new McTerm { Amount = amount, Currency = currency, IsPercent = percent });
        _mcInput.Text = "";
        return true;
    }

    private void McAddCurrency(int currency)
    {
        if (_mcShowResult)
        {
            _mcResultCurrency = currency;
            McRenderResult();
            return;
        }

        if (McCommitInput(currency))
        {
            McRebuildTokens(_mcTokens.Count - 1);
            McRenderResult();
        }
        else if (_mcInput.Text.Length == 0 && _mcTokens.Count > 0 && _mcTokens[^1] is McTerm last && !last.IsPercent && last.Currency != currency)
        {
            last.Currency = currency;
            McRebuildTokens(_mcTokens.Count - 1);
            McRenderResult();
        }
        _mcInput.Focus();
    }

    private void McAddPercent()
    {
        if (_mcShowResult) return;
        if (McCommitInput(-1, percent: true))
        {
            McRebuildTokens(_mcTokens.Count - 1);
            McRenderResult();
        }
        _mcInput.Focus();
    }

    private void McAddOperator(char op)
    {
        if (_mcShowResult)
        {
            if (McEvaluateResult() is not { } value) return;
            _mcTokens.Clear();
            _mcTokens.Add(new McTerm { Amount = value, Currency = _mcResultCurrency });
            _mcShowResult = false;
        }
        else if (_mcInput.Text.Length > 0)
        {
            McCommitInput(-1);
        }

        if (_mcTokens.Count == 0) return;
        var animateFrom = Math.Max(0, _mcTokens.Count - (_mcTokens[^1] is char ? 1 : 0));
        if (_mcTokens[^1] is char) _mcTokens[^1] = op;
        else _mcTokens.Add(op);
        McRebuildTokens(animateFrom);
        McRenderResult();
        _mcInput.Focus();
    }

    private void McEquals()
    {
        if (_mcInput.Text.Length > 0) McCommitInput(-1);
        if (_mcTokens.Count % 2 == 0 && _mcTokens.Count > 0 && _mcTokens[^1] is char) _mcTokens.RemoveAt(_mcTokens.Count - 1);
        if (_mcTokens.Count == 0) return;

        _mcShowResult = true;
        _mcRevealPending = true;
        _mcResultCurrency = _mcTokens.OfType<McTerm>().FirstOrDefault(t => t.Currency >= 0)?.Currency ?? CalcRubIndex;
        McRebuildTokens();
        McRenderResult();
        _mcInput.Dispatcher.BeginInvoke(new Action(() =>
        {
            _mcInput.Focus();
            Keyboard.Focus(_mcInput);
        }), DispatcherPriority.Input);
    }

    private void McBackspace()
    {
        if (_mcShowResult) _mcShowResult = false;
        else if (_mcInput.Text.Length > 0) _mcInput.Text = _mcInput.Text[..^1];
        else if (_mcTokens.Count > 0) _mcTokens.RemoveAt(_mcTokens.Count - 1);
        McRebuildTokens();
        McRenderResult();
        _mcInput.Focus();
    }

    private void McClear()
    {
        SweepClearedTokens();
        _mcTokens.Clear();
        _mcShowResult = false;
        _mcInput.Text = "";
        McRebuildTokens();
        McRenderResult();
    }

    private FrameworkElement McCreateTokenElement(object token)
    {
        if (token is McTerm term)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
            row.Children.Add(new TextBlock
            {
                Text = FormatPlain(term.Amount), FontSize = 16, FontWeight = FontWeights.SemiBold, FontFamily = ValueFont,
                Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center
            });
            if (term.IsPercent)
            {
                row.Children.Add(new TextBlock
                {
                    Text = "%", FontSize = 16, FontWeight = FontWeights.Bold, FontFamily = ValueFont,
                    Foreground = new SolidColorBrush(Wpf.Ui.Appearance.ApplicationAccentColorManager.SystemAccent),
                    Margin = new Thickness(2, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center
                });
            }
            else if (term.Currency >= 0)
            {
                var symbol = CurrencySymbol(term.Currency);
                symbol.Margin = new Thickness(4, 0, 0, 0);
                symbol.VerticalAlignment = VerticalAlignment.Center;
                row.Children.Add(symbol);
            }
            return row;
        }

        return new TextBlock
        {
            Text = token is char op ? op switch { '*' => "\u00D7", '/' => "\u00F7", '-' => "\u2212", _ => "+" } : "",
            FontSize = 16, FontFamily = ValueFont, FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromArgb(170, 235, 235, 240)),
            Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center
        };
    }

    private void McUpdatePlaceholder()
    {
        if (_mcPlaceholder == null) return;
        _mcPlaceholder.Visibility = _mcTokens.Count == 0 && _mcInput.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void McRebuildTokens(int animateFrom = -1)
    {
        _mcTokenPanel.Children.Clear();
        for (var i = 0; i < _mcTokens.Count; i++)
        {
            var element = McCreateTokenElement(_mcTokens[i]);
            _mcTokenPanel.Children.Add(element);
            if (animateFrom < 0 || i < animateFrom) continue;
            if (element is StackPanel row && _mcTokens[i] is McTerm term) AnimateNewTerm(row, term.IsPercent || term.Currency >= 0);
            else AnimateNewOperator(element);
        }
        _mcTokenPanel.Children.Add(_mcInput);
        _mcInput.Visibility = Visibility.Visible;
        _mcInput.Opacity = _mcShowResult ? 0 : 1;
        McUpdatePlaceholder();
        _mcScroll.Dispatcher.BeginInvoke(new Action(_mcScroll.ScrollToEnd), DispatcherPriority.Loaded);
    }

    private void McRenderResult()
    {
        if (_mcResultRow == null) return;

        var accent = Wpf.Ui.Appearance.ApplicationAccentColorManager.SystemAccent;
        for (var i = 0; i < _mcChips.Length; i++)
        {
            var selected = _mcShowResult && AllCurrencies[i] == _mcResultCurrency;
            _mcChips[i].Background = new SolidColorBrush(selected
                ? Color.FromArgb(120, accent.R, accent.G, accent.B)
                : Color.FromArgb(34, 255, 255, 255));
        }

        _mcResultRow.Children.Clear();
        if (!_mcShowResult)
        {
            var note = McPercentNote();
            _mcResultHint.Text = note != null ? note + "   (press =)" : "Press = for the total, % for percentages";
            if (note != null && _mcResultHint.Text != _mcLastNote)
            {
                _mcNoteShift.BeginAnimation(TranslateTransform.YProperty, Motion.Spring(-6, 0, Motion.Soft));
                _mcResultHint.BeginAnimation(OpacityProperty, Motion.Tween(0, 1, 200));
            }
            _mcLastNote = _mcResultHint.Text;
            _mcLastResultKey = "";
            _mcResultHint.Visibility = Visibility.Visible;
            _mcResultRow.Visibility = Visibility.Collapsed;
            return;
        }

        if (McEvaluateResult() is not { } value)
        {
            _mcResultHint.Text = "Cannot calculate";
            _mcResultHint.Visibility = Visibility.Visible;
            _mcResultRow.Visibility = Visibility.Collapsed;
            return;
        }

        _mcResultHint.Visibility = Visibility.Collapsed;
        _mcResultRow.Visibility = Visibility.Visible;
        var resultKey = $"{_mcResultCurrency}|{value:F4}";
        var changed = resultKey != _mcLastResultKey;
        var switched = false;
        _mcLastResultKey = resultKey;
        var probe = new TextBlock { Text = FormatTotal(value), FontSize = 17, FontWeight = FontWeights.SemiBold, FontFamily = ValueFont };
        probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        _mcResultText.Width = Math.Ceiling(probe.DesiredSize.Width) + 1;
        _mcResultText.TextAlignment = TextAlignment.Right;
        Counter.SetFormatter(_mcResultText, v => FormatTotal(v));
        if (changed && _mcRevealPending)
        {
            _mcRevealPending = false;
            _mcResultText.BeginAnimation(Counter.ValueProperty, Motion.Tween(0, value, 520));
        }
        else
        {
            _mcResultText.BeginAnimation(Counter.ValueProperty, null);
            _mcResultText.Text = FormatTotal(value);
            switched = changed;
        }
        var symbol = CurrencySymbol(_mcResultCurrency);
        symbol.Margin = new Thickness(6, 0, 0, 0);
        symbol.VerticalAlignment = VerticalAlignment.Center;
        var totalGroup = new StackPanel { Orientation = Orientation.Horizontal, RenderTransformOrigin = new Point(0, 0.5), RenderTransform = _mcResultScale };
        if (_mcResultText.Parent is Panel oldParent) oldParent.Children.Remove(_mcResultText);
        totalGroup.Children.Add(new TextBlock
        {
            Text = "= ", FontSize = 17, FontWeight = FontWeights.SemiBold, FontFamily = ValueFont,
            Foreground = _mcResultBrush, VerticalAlignment = VerticalAlignment.Center
        });
        totalGroup.Children.Add(_mcResultText);
        totalGroup.Children.Add(symbol);
        _mcResultRow.Children.Add(totalGroup);
        if (switched)
        {
            var easeOut = new CubicEase { EasingMode = EasingMode.EaseOut };
            var slide = new TranslateTransform(-8, 0);
            symbol.RenderTransform = slide;
            slide.BeginAnimation(TranslateTransform.XProperty, Motion.Tween(-8, 0, 200, easeOut));
            symbol.BeginAnimation(OpacityProperty, Motion.Tween(0, 1, 200, easeOut));
            _mcResultText.BeginAnimation(OpacityProperty, Motion.Tween(0.4, 1, 200, easeOut));
        }
        _mcResultRow.Children.Add(_mcCopyButton);
        _mcResultRow.Children.Add(new TextBlock
        {
            Text = McPercentNote() ?? "pick a currency to convert",
            FontSize = 10, FontFamily = LabelFont,
            Foreground = new SolidColorBrush(Color.FromArgb(130, 235, 235, 240)),
            Margin = new Thickness(18, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
    }

    private static bool IsValidAmountInput(string current, string incoming)
    {
        if (incoming is "," ) incoming = ".";
        if (!incoming.All(c => char.IsDigit(c) || c == '.')) return false;
        var prospective = current + incoming;
        return !(incoming == "." && current.Contains('.')) && prospective.Length <= 15;
    }

    private const int AedIndex = 3;
    private const int TryIndex = 5;

    private FrameworkElement CurrencySymbol(int currency)
    {
        var accent = new SolidColorBrush(CalcAccent(currency));
        if (currency == AedIndex)
        {
            return new ShapePath
            {
                Data = Geometry.Parse("M3.2,1.5 L6.6,1.5 C10.4,1.5 12,3.7 12,6.5 C12,9.3 10.4,11.5 6.6,11.5 L3.2,11.5 Z M1,4.9 L12.8,4.9 M1,8.1 L12.8,8.1"),
                Stroke = accent,
                StrokeThickness = 1.4,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Stretch = Stretch.Uniform,
                Width = 12.5, Height = 11
            };
        }

        if (currency == TryIndex)
        {
            return new ShapePath
            {
                Data = Geometry.Parse("M6.5,0.5 L7.969,4.478 L12.206,4.646 L8.878,7.273 L10.027,11.354 L6.5,9 L2.973,11.354 L4.122,7.273 L0.794,4.646 L5.031,4.478 Z"),
                Fill = accent,
                Stroke = accent,
                StrokeThickness = 0.7,
                StrokeLineJoin = PenLineJoin.Round,
                Stretch = Stretch.Uniform,
                Width = 11.5, Height = 11.5
            };
        }

        return new TextBlock
        {
            Text = CalcSymbols[currency],
            FontSize = 12, FontWeight = FontWeights.Bold, FontFamily = ValueFont,
            Foreground = accent,
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    private static readonly int[] AllCurrencies = { 0, 1, 2, 3, 4 };
    private static readonly int[] SwapperCurrencies = { 0, 1, 2, 3, 5, 4 };
    private static readonly int[] ChartCurrencies = { 0, 1, 2, 3, 5 };

    private Border BuildCurrencyChip(IReadOnlyList<int> options, int initial, Action<int> onPick, out Action<int> refresh)
    {
        var symbolHost = new ContentControl { VerticalAlignment = VerticalAlignment.Center, IsTabStop = false };
        var codeText = new TextBlock { FontSize = 12, FontFamily = LabelFont, Foreground = Brushes.White, Margin = new Thickness(4, 0, 4, 0), VerticalAlignment = VerticalAlignment.Center };
        var chevronRotate = new RotateTransform(0);
        var chevron = new ShapePath
        {
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = chevronRotate,
            Data = Geometry.Parse("M0,0 L4,4 L8,0"),
            Stroke = new SolidColorBrush(Color.FromArgb(160, 235, 235, 240)),
            StrokeThickness = 1.3, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            Width = 8, Height = 4, VerticalAlignment = VerticalAlignment.Center
        };

        var content = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        content.Children.Add(symbolHost);
        content.Children.Add(codeText);
        content.Children.Add(chevron);

        var chip = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(24, 255, 255, 255)),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(8, 4, 8, 4),
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            Child = content
        };

        Action<int> refreshLocal = currency =>
        {
            symbolHost.Content = CurrencySymbol(currency);
            codeText.Text = CalcCodes[currency];
        };
        refresh = refreshLocal;
        refreshLocal(initial);

        var list = new StackPanel();
        var popupBorder = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(250, 24, 24, 27)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = list
        };
        var popup = new System.Windows.Controls.Primitives.Popup
        {
            PlacementTarget = chip,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
            StaysOpen = true,
            AllowsTransparency = true,
            PopupAnimation = System.Windows.Controls.Primitives.PopupAnimation.None,
            Child = popupBorder
        };

        var items = new List<Border>();
        var closing = false;
        void CloseAnimated()
        {
            if (!popup.IsOpen || closing) return;
            closing = true;
            chevronRotate.BeginAnimation(RotateTransform.AngleProperty, Motion.Spring(chevronRotate.Angle, 0, Motion.Snappy));
            var shrink = Motion.Tween(1, 0, 170, new CubicEase { EasingMode = EasingMode.EaseIn });
            shrink.Completed += (_, _) =>
            {
                popup.IsOpen = false;
                closing = false;
            };
            popupBorder.BeginAnimation(Reveal.FractionProperty, shrink);
            Motion.Settle(450, () =>
            {
                if (!closing) return;
                popup.IsOpen = false;
                closing = false;
            });
        }

        foreach (var idx in options)
        {
            var itemRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(10, 6, 12, 6) };
            var itemSymbol = CurrencySymbol(idx);
            itemSymbol.HorizontalAlignment = HorizontalAlignment.Center;
            itemSymbol.VerticalAlignment = VerticalAlignment.Center;
            var symbolCell = new Grid { Width = 18 };
            symbolCell.Children.Add(itemSymbol);
            itemRow.Children.Add(symbolCell);
            itemRow.Children.Add(new TextBlock
            {
                Text = CalcCodes[idx],
                FontSize = 12, FontFamily = LabelFont, Foreground = Brushes.White,
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
            var item = new Border { Background = Brushes.Transparent, Cursor = Cursors.Hand, Child = itemRow, RenderTransform = new TranslateTransform() };
            items.Add(item);
            item.MouseEnter += (_, _) => item.Background = new SolidColorBrush(Color.FromArgb(28, 255, 255, 255));
            item.MouseLeave += (_, _) => item.Background = Brushes.Transparent;
            item.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                refreshLocal(idx);
                onPick(idx);
                CloseAnimated();
            };
            list.Children.Add(item);
        }

        MouseButtonEventHandler? outsideClickHandler = null;
        popup.Opened += (_, _) =>
        {
            _openDropdown = popup;
            _openDropdownBorder = popupBorder;
            outsideClickHandler = (_, e2) =>
            {
                if (e2.OriginalSource is DependencyObject src && (IsDescendantOf(src, chip) || IsDescendantOf(src, popupBorder))) return;
                CloseAnimated();
            };
            PreviewMouseDown += outsideClickHandler;
            popupBorder.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            popupBorder.BeginAnimation(Reveal.FractionProperty, null);
            Reveal.SetFraction(popupBorder, 0);
            popupBorder.BeginAnimation(Reveal.FractionProperty, Motion.Spring(0, 1, Motion.Soft));
            chevronRotate.BeginAnimation(RotateTransform.AngleProperty, Motion.Spring(chevronRotate.Angle, 180, Motion.Bouncy));
            for (var i = 0; i < items.Count; i++)
            {
                var translate = (TranslateTransform)items[i].RenderTransform;
                items[i].BeginAnimation(OpacityProperty, null);
                translate.BeginAnimation(TranslateTransform.YProperty, null);
                items[i].Opacity = 0;
                translate.Y = -8;
                items[i].BeginAnimation(OpacityProperty, Motion.Tween(0, 1, 280, delayMs: 40 + i * 32));
                translate.BeginAnimation(TranslateTransform.YProperty, Motion.Tween(-8, 0, 280, delayMs: 40 + i * 32));
            }
            Motion.Settle(900, () =>
            {
                Motion.Clear(popupBorder, Reveal.FractionProperty, 1.0);
                foreach (var item in items)
                {
                    Motion.Clear(item, OpacityProperty, 1.0);
                    Motion.Clear((TranslateTransform)item.RenderTransform, TranslateTransform.YProperty, 0.0);
                }
            });
        };
        popup.Closed += (_, _) =>
        {
            if (ReferenceEquals(_openDropdown, popup)) { _openDropdown = null; _openDropdownBorder = null; }
            if (outsideClickHandler != null) { PreviewMouseDown -= outsideClickHandler; outsideClickHandler = null; }
            chevronRotate.BeginAnimation(RotateTransform.AngleProperty, null);
            chevronRotate.Angle = 0;
            popupBorder.BeginAnimation(Reveal.FractionProperty, null);
            Reveal.SetFraction(popupBorder, 1);
            closing = false;
        };

        chip.PreviewMouseDown += (_, e) => e.Handled = true;
        chip.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (popup.IsOpen) CloseAnimated();
            else popup.IsOpen = true;
        };

        return chip;
    }

    private static bool IsDescendantOf(DependencyObject? child, DependencyObject ancestor)
    {
        while (child != null)
        {
            if (ReferenceEquals(child, ancestor)) return true;
            child = LogicalTreeHelper.GetParent(child) ?? (child is Visual ? VisualTreeHelper.GetParent(child) : null);
        }
        return false;
    }

    private Border _calcAmountField = null!;
    private Border _calcResultField = null!;

    private Border _calcFromChip = null!;
    private Border _calcToChip = null!;

    private readonly ScaleTransform _swapScale = new(1, 1);
    private readonly RotateTransform _swapRotate = new(0);
    private readonly ScaleTransform[] _fieldPulse = { new(1, 1), new(1, 1) };
    private double _swapAngleTarget;
    private bool _swapTransformsReady;

    private async Task PulseSwapAsync(Border swapButton)
    {
        if (!Motion.Enabled)
        {
            SwapCalcCurrencies();
            return;
        }

        var fields = new[] { _calcAmountField, _calcResultField };
        if (!_swapTransformsReady)
        {
            var group = new TransformGroup();
            group.Children.Add(_swapScale);
            group.Children.Add(_swapRotate);
            swapButton.RenderTransformOrigin = new Point(0.5, 0.5);
            swapButton.RenderTransform = group;
            for (var i = 0; i < fields.Length; i++)
            {
                fields[i].RenderTransformOrigin = new Point(0.5, 0.5);
                fields[i].RenderTransform = _fieldPulse[i];
            }
            _swapTransformsReady = true;
        }

        var buttonMs = Motion.Bouncy.Seconds * 1000;
        _swapAngleTarget += 180;
        _swapRotate.BeginAnimation(RotateTransform.AngleProperty, Motion.Spring(_swapRotate.Angle, _swapAngleTarget, Motion.Bouncy));
        _swapScale.BeginAnimation(ScaleTransform.ScaleXProperty, BumpAnimation(1.15, buttonMs));
        _swapScale.BeginAnimation(ScaleTransform.ScaleYProperty, BumpAnimation(1.15, buttonMs));
        var morph = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(buttonMs) };
        morph.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromPercent(0.45), new CubicEase { EasingMode = EasingMode.EaseOut }));
        morph.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(1), new CubicEase { EasingMode = EasingMode.EaseInOut }));
        swapButton.BeginAnimation(Squircle.FractionProperty, morph);

        for (var i = 0; i < fields.Length; i++)
        {
            var animation = BumpAnimation(1.04, 460);
            animation.BeginTime = TimeSpan.FromMilliseconds(i * 50);
            _fieldPulse[i].BeginAnimation(ScaleTransform.ScaleXProperty, animation);
            _fieldPulse[i].BeginAnimation(ScaleTransform.ScaleYProperty, animation);
        }

        foreach (UIElement dimmed in new UIElement[] { _calcResultText, _calcFromChip, _calcToChip })
        {
            var dip = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(420), BeginTime = TimeSpan.FromMilliseconds(60) };
            dip.KeyFrames.Add(new EasingDoubleKeyFrame(0.2, KeyTime.FromPercent(0.35), new CubicEase { EasingMode = EasingMode.EaseOut }));
            dip.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromPercent(1), new CubicEase { EasingMode = EasingMode.EaseIn }));
            dimmed.BeginAnimation(OpacityProperty, dip);
        }

        Motion.Settle(900, () =>
        {
            Motion.Clear(_swapScale, ScaleTransform.ScaleXProperty, 1.0);
            Motion.Clear(_swapScale, ScaleTransform.ScaleYProperty, 1.0);
            Motion.Clear(_swapRotate, RotateTransform.AngleProperty, _swapAngleTarget);
            Motion.Clear(swapButton, Squircle.FractionProperty, 0.0);
            foreach (var pulse in _fieldPulse)
            {
                Motion.Clear(pulse, ScaleTransform.ScaleXProperty, 1.0);
                Motion.Clear(pulse, ScaleTransform.ScaleYProperty, 1.0);
            }
            foreach (UIElement dimmed in new UIElement[] { _calcResultText, _calcFromChip, _calcToChip })
                Motion.Clear(dimmed, OpacityProperty, 1.0);
        });

        await Task.Delay(200);
        SwapCalcCurrencies();
    }

    private static DoubleAnimationUsingKeyFrames BumpAnimation(double peak, double ms)
    {
        var bump = new DoubleAnimationUsingKeyFrames { Duration = Motion.Span(ms) };
        bump.KeyFrames.Add(new EasingDoubleKeyFrame(peak, KeyTime.FromPercent(0.4), new CubicEase { EasingMode = EasingMode.EaseOut }));
        bump.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromPercent(1), new CubicEase { EasingMode = EasingMode.EaseInOut }));
        return bump;
    }

    private void SwapCalcCurrencies()
    {
        (_calcFromCurrency, _calcToCurrency) = (_calcToCurrency, _calcFromCurrency);
        _calcFromRefresh(_calcFromCurrency);
        _calcToRefresh(_calcToCurrency);
        RecalculateCalculator();
    }

    private static readonly NumberFormatInfo CalcNumberFormat = new()
    {
        NumberGroupSeparator = " ",
        NumberDecimalSeparator = ",",
        NumberGroupSizes = new[] { 3 }
    };

    private static string FormatCalcNumber(double value) =>
        value.ToString(value >= 1000 ? "#,##0.00" : "0.####", CalcNumberFormat);

    private const string CopyIconData = "M16,1H4C2.9,1,2,1.9,2,3v14h2V3h12V1z M19,5H8C6.9,5,6,5.9,6,7v14c0,1.1,0.9,2,2,2h11c1.1,0,2-0.9,2-2V7C21,5.9,20.1,5,19,5z M19,21H8V7h11V21z";
    private ShapePath _calcCopyIcon = null!;
    private ShapePath _calcCheckIcon = null!;
    private readonly ScaleTransform _calcCopyScale = new(1, 1);
    private Border _calcWipe = null!;
    private readonly ScaleTransform _calcWipeScale = new(0, 1);
    private DispatcherTimer? _calcCopyResetTimer;
    private bool _calcCopyBusy;
    private const double CheckDash = 8.4;

    private void CopyCalcResult()
    {
        var text = _calcResultText.Text;
        if (string.IsNullOrEmpty(text)) return;
        try
        {
            Clipboard.SetText(text);
        }
        catch
        {
            return;
        }

        if (_calcCopyBusy) return;
        _calcCopyBusy = true;

        _calcWipe.BeginAnimation(OpacityProperty, null);
        _calcWipe.Opacity = 1;
        _calcWipeScale.BeginAnimation(ScaleTransform.ScaleXProperty, Motion.Tween(0, 1, 460, new CubicEase { EasingMode = EasingMode.EaseOut }));
        _calcWipe.BeginAnimation(OpacityProperty, Motion.Tween(1, 0, 320, new CubicEase { EasingMode = EasingMode.EaseIn }, delayMs: 420));

        _calcCopyScale.BeginAnimation(ScaleTransform.ScaleXProperty, Motion.Tween(1, 0, 160, new CubicEase { EasingMode = EasingMode.EaseIn }));
        _calcCopyScale.BeginAnimation(ScaleTransform.ScaleYProperty, Motion.Tween(1, 0, 160, new CubicEase { EasingMode = EasingMode.EaseIn }));
        _calcCheckIcon.BeginAnimation(OpacityProperty, null);
        _calcCheckIcon.Opacity = 1;
        _calcCheckIcon.BeginAnimation(ShapePath.StrokeDashOffsetProperty, Motion.Tween(CheckDash, 0, 320, delayMs: 120));

        _calcCopyResetTimer?.Stop();
        _calcCopyResetTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1100) };
        _calcCopyResetTimer.Tick += (_, _) =>
        {
            _calcCopyResetTimer!.Stop();
            _calcCheckIcon.BeginAnimation(OpacityProperty, Motion.Tween(1, 0, 140));
            _calcCopyScale.BeginAnimation(ScaleTransform.ScaleXProperty, Motion.Spring(0, 1, Motion.Bouncy));
            _calcCopyScale.BeginAnimation(ScaleTransform.ScaleYProperty, Motion.Spring(0, 1, Motion.Bouncy));

            var release = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(320) };
            release.Tick += (_, _) =>
            {
                release.Stop();
                _calcCheckIcon.BeginAnimation(ShapePath.StrokeDashOffsetProperty, null);
                _calcCheckIcon.StrokeDashOffset = CheckDash;
                _calcCopyBusy = false;
            };
            release.Start();
        };
        _calcCopyResetTimer.Start();

        if (Motion.Enabled) Motion.Settle(1800, () =>
        {
            Motion.Clear(_calcWipe, OpacityProperty, 0.0);
            Motion.Clear(_calcWipeScale, ScaleTransform.ScaleXProperty, 0.0);
            Motion.Clear(_calcCopyScale, ScaleTransform.ScaleXProperty, 1.0);
            Motion.Clear(_calcCopyScale, ScaleTransform.ScaleYProperty, 1.0);
            Motion.Clear(_calcCheckIcon, OpacityProperty, 0.0);
            Motion.Clear(_calcCheckIcon, ShapePath.StrokeDashOffsetProperty, CheckDash);
            _calcCopyBusy = false;
        });
    }

    private double RateToRub(int currency) => currency switch
    {
        0 => _calcLatest?.UsdRub ?? 0,
        1 => _calcLatest?.EurRub ?? 0,
        2 => _calcLatest?.CnyRub ?? 0,
        3 => _calcLatest?.AedRub ?? 0,
        5 => _calcLatest?.TryRub ?? 0,
        _ => 1
    };

    private void RecalculateCalculator()
    {
        if (_calcLatest == null) return;
        var amount = double.TryParse(_calcAmountBox.Text.Replace(',', '.'), NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
        var fromRate = RateToRub(_calcFromCurrency);
        var toRate = RateToRub(_calcToCurrency);
        var result = toRate == 0 ? 0 : amount * fromRate / toRate;
        _calcResultText.Text = FormatCalcNumber(result);

        if (fromRate > 0 && toRate > 0)
        {
            var oneUnit = fromRate / toRate;
            _calcRateText.Text = $"1 {CalcCodes[_calcFromCurrency]} = {FormatCalcNumber(oneUnit)} {CalcCodes[_calcToCurrency]}";
        }
    }

    // Called whenever fresh daily history lands (see ApplyHistoryToViews) -
    // the calculator's own amount conversion only needs the LATEST day's
    // rates (not the whole history), same cache as everything else so it
    // doesn't trigger its own separate CBR request.
    private void UpdateCalculatorRate(CbrHistoryPoint latest)
    {
        _calcLatest = latest;
        RecalculateCalculator();
    }

    private void UpdateChartVisual(List<CbrHistoryPoint> points)
    {
        var n0 = points.Count;
        for (var i = 0; i < ChartDays; i++)
        {
            var label = _chartDateLabels[i];
            if (i >= n0) { label.Text = ""; continue; }

            label.Text = points[i].Date.ToString("dd.MM");
            var x = n0 <= 1 ? 0 : i * (ChartPlotWidth / (n0 - 1));
            // Measure to center each label under its actual point instead of
            // guessing a fixed width - "19.09" and a lone "9.09"-shaped date
            // aren't the same pixel width, and centering on the true
            // measured width is what makes the spacing look even.
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var half = label.DesiredSize.Width / 2;
            Canvas.SetLeft(label, Math.Clamp(x - half, 0, ChartPlotWidth - label.DesiredSize.Width));
        }

        var endY = new double[SeriesCount];
        endY[0] = SetSeries(0, points.Select(p => p.UsdRub).ToList());
        endY[1] = SetSeries(1, points.Select(p => p.EurRub).ToList());
        endY[2] = SetSeries(2, points.Select(p => p.CnyRub).ToList());

        // All three series are normalized to their OWN range, so "today"
        // often lands near the top (or bottom) of all three at once - their
        // value labels would land on top of each other. Spread any that are
        // within a label's height of each other, ordered by their natural Y,
        // instead of leaving them to overlap into unreadable mush.
        const double minGap = 14;
        const double maxTop = ChartHeight - 14;
        var order = new[] { 0, 1, 2 };
        Array.Sort(order, (a, b) => endY[a].CompareTo(endY[b]));
        var tops = new double[SeriesCount];
        for (var s = 0; s < SeriesCount; s++) tops[s] = Math.Clamp(endY[s] - 7, 0, maxTop);
        for (var i = 1; i < order.Length; i++)
            tops[order[i]] = Math.Max(tops[order[i]], tops[order[i - 1]] + minGap);
        tops[order[^1]] = Math.Min(tops[order[^1]], maxTop);
        for (var i = order.Length - 2; i >= 0; i--)
            tops[order[i]] = Math.Min(tops[order[i]], tops[order[i + 1]] - minGap);
        for (var s = 0; s < SeriesCount; s++)
        {
            Canvas.SetLeft(_seriesValueLabel[s], ChartPlotWidth + 7);
            Canvas.SetTop(_seriesValueLabel[s], tops[s]);
        }

        var n = points.Count;
        var lastX = n <= 1 ? 0 : (n - 1) * (ChartPlotWidth / (n - 1));
        _chartTodayGuide.X1 = lastX;
        _chartTodayGuide.X2 = lastX;
    }

    // The only way to read an exact value for a specific date used to be
    // "guess from where the curve is" - hovering now snaps to the nearest
    // day's X and pops a tooltip with the real date and all three prices.
    private void OnChartCanvasMouseMove(object sender, MouseEventArgs e)
    {
        if (_latestHistory is not { Count: > 0 } history) return;

        var n = history.Count;
        var step = n <= 1 ? 0 : ChartPlotWidth / (n - 1);
        var pos = e.GetPosition(_chartCanvas);
        var idx = step <= 0 ? 0 : (int)Math.Round(pos.X / step);
        idx = Math.Clamp(idx, 0, n - 1);

        var x = idx * step;
        _chartHoverGuide.X1 = x;
        _chartHoverGuide.X2 = x;
        _chartHoverGuide.Visibility = Visibility.Visible;
        SetHoveredMarkerIndex(idx);

        var p = history[idx];

        if (_chartCrosshairHover)
        {
            _chartTooltip.Visibility = Visibility.Hidden;
            ShowHoverChips(idx, new[] { $"{p.UsdRub:0.00}", $"{p.EurRub:0.00}", $"{p.CnyRub:0.00}" });
            return;
        }

        foreach (var chip in _hoverChips) chip.Visibility = Visibility.Hidden;
        _tooltipDate.Text = p.Date.ToString("d MMMM", System.Globalization.CultureInfo.GetCultureInfo("en-US"));
        _tooltipValues[0].Text = $"{p.UsdRub:0.0000}";
        _tooltipValues[1].Text = $"{p.EurRub:0.0000}";
        _tooltipValues[2].Text = $"{p.CnyRub:0.0000}";

        _chartTooltip.Visibility = Visibility.Visible;
        _chartTooltip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var tipWidth = _chartTooltip.DesiredSize.Width;
        var left = Math.Clamp(x - tipWidth / 2, 0, ChartWidth - tipWidth);
        Canvas.SetLeft(_chartTooltip, left);
        Canvas.SetTop(_chartTooltip, 2);
    }

    // Variant E: a small pill in each series' own color sitting just above
    // its point at the hovered X - no shared box, so it reads as part of the
    // line itself rather than a separate UI panel. When two or three lines
    // sit close together at that X, docking each chip to its own raw point
    // makes them overlap into unreadable mush - so all three are laid out
    // together here: sorted top-to-bottom by their actual line position,
    // then any chip too close to the one above it gets pushed further DOWN
    // (never sideways/up), so a tight cluster fans out into a clean stack
    // instead of a pile.
    private void ShowHoverChips(int pointIndex, string[] values)
    {
        if (_seriesPoints[0] is not { Count: > 0 } pts0 || pointIndex >= pts0.Count) return;

        var x = pts0[pointIndex].X;
        var rawY = new double[SeriesCount];
        var widths = new double[SeriesCount];
        var heights = new double[SeriesCount];

        for (var s = 0; s < SeriesCount; s++)
        {
            _hoverChipTexts[s].Text = values[s];
            var chip = _hoverChips[s];
            chip.Visibility = Visibility.Visible;
            chip.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            widths[s] = chip.DesiredSize.Width;
            heights[s] = chip.DesiredSize.Height;
            rawY[s] = _seriesPoints[s] is { } pts && pointIndex < pts.Count ? pts[pointIndex].Y : 0;
        }

        var order = new[] { 0, 1, 2 };
        Array.Sort(order, (a, b) => rawY[a].CompareTo(rawY[b]));

        // Stack each chip just above its own point, pushing later ones down
        // when they'd overlap the one above. Near a shared peak the natural
        // chain can run off the bottom of the chart - shifting the WHOLE
        // chain up by the overflow (instead of clamping only the last chip
        // individually) keeps every chip's spacing intact instead of
        // dumping the last one back on top of its neighbor and hiding it
        // behind it in z-order.
        const double gap = 3;
        var tops = new double[SeriesCount];
        var prevBottom = double.NegativeInfinity;
        foreach (var s in order)
        {
            var top = Math.Max(rawY[s] - heights[s] - 8, prevBottom + gap);
            tops[s] = top;
            prevBottom = top + heights[s];
        }

        var overflow = prevBottom - ChartHeight;
        if (overflow > 0)
        {
            foreach (var s in order) tops[s] -= overflow;
        }

        var topmost = tops[order[0]];
        if (topmost < 0)
        {
            foreach (var s in order) tops[s] -= topmost;
        }

        foreach (var s in order)
        {
            Canvas.SetLeft(_hoverChips[s], Math.Clamp(x - widths[s] / 2, 0, ChartWidth - widths[s]));
            Canvas.SetTop(_hoverChips[s], tops[s]);
        }
    }

    // Confirms which day the tooltip is showing by popping the three dots at
    // that X a little bigger, with a white ring - without this the tooltip
    // number and the actual point it came from were only linked by the
    // vertical guide line, easy to lose track of on a small chart.
    private void SetHoveredMarkerIndex(int hoveredIndex)
    {
        for (var s = 0; s < SeriesCount; s++)
        {
            for (var d = 0; d < ChartDays; d++)
            {
                var marker = _seriesMarkers[s][d];
                if (marker.Visibility != Visibility.Visible) continue;
                var cx = Canvas.GetLeft(marker) + marker.Width / 2;
                var cy = Canvas.GetTop(marker) + marker.Height / 2;
                var isHovered = d == hoveredIndex;
                var size = isHovered ? 8.5 : 5.5;
                marker.Width = size;
                marker.Height = size;
                Canvas.SetLeft(marker, cx - size / 2);
                Canvas.SetTop(marker, cy - size / 2);
                marker.StrokeThickness = isHovered ? 2 : 1.2;
                marker.Stroke = new SolidColorBrush(isHovered ? Colors.White : Color.FromRgb(12, 12, 14));
            }
        }
    }

    // Draws the line/fill/markers for one series and reports back the raw
    // (pre-collision-avoidance) Y of its last point, so the caller can space
    // out value labels across all three series at once.
    private double SetSeries(int index, List<double> values)
    {
        var line = _seriesLine[index];
        var fill = _seriesFill[index];
        var markers = _seriesMarkers[index];
        var label = _seriesValueLabel[index];

        if (values.Count == 0)
        {
            line.Data = null;
            fill.Data = null;
            foreach (var m in markers) m.Visibility = Visibility.Hidden;
            label.Text = "";
            return ChartHeight / 2;
        }

        var min = values.Min();
        var max = values.Max();
        var range = max - min;
        if (range < 0.0001) range = 1; // dead-flat over the window - avoid a divide by zero

        const double topPad = 8, bottomPad = 8;
        var usableHeight = ChartHeight - topPad - bottomPad;
        var n = values.Count;
        var points = new List<Point>(n);
        for (var i = 0; i < n; i++)
        {
            var x = n == 1 ? 0 : i * (ChartPlotWidth / (n - 1));
            var normalized = (values[i] - min) / range;
            var y = topPad + (1 - normalized) * usableHeight;
            points.Add(new Point(x, y));
        }

        line.Data = BuildSmoothGeometry(points, fillToY: null);
        fill.Data = BuildSmoothGeometry(points, fillToY: ChartHeight);
        _seriesPoints[index] = points;

        for (var d = 0; d < ChartDays; d++)
        {
            if (d >= n) { markers[d].Visibility = Visibility.Hidden; continue; }
            markers[d].Visibility = Visibility.Visible;
            markers[d].Width = 5.5;
            markers[d].Height = 5.5;
            Canvas.SetLeft(markers[d], points[d].X - markers[d].Width / 2);
            Canvas.SetTop(markers[d], points[d].Y - markers[d].Height / 2);
        }

        label.Text = $"{values[^1]:0.00}";
        return points[^1].Y;
    }

    // Catmull-Rom points converted to cubic bezier control points - a smooth
    // curve through every data point (not just a rounded corner between
    // straight segments) reads as a real finished chart instead of a
    // connect-the-dots line. With fillToY set, the same curve closes down to
    // that Y to make a fillable area shape instead of an open stroke path.
    private static PathGeometry BuildSmoothGeometry(List<Point> points, double? fillToY)
    {
        var figure = new PathFigure { StartPoint = points[0], IsFilled = fillToY.HasValue };

        if (points.Count == 1)
        {
            // nothing to connect
        }
        else if (points.Count == 2)
        {
            figure.Segments.Add(new LineSegment(points[1], true));
        }
        else
        {
            for (var i = 0; i < points.Count - 1; i++)
            {
                var p0 = i == 0 ? points[i] : points[i - 1];
                var p1 = points[i];
                var p2 = points[i + 1];
                var p3 = i + 2 < points.Count ? points[i + 2] : p2;

                var c1 = new Point(p1.X + (p2.X - p0.X) / 6.0, p1.Y + (p2.Y - p0.Y) / 6.0);
                var c2 = new Point(p2.X - (p3.X - p1.X) / 6.0, p2.Y - (p3.Y - p1.Y) / 6.0);
                figure.Segments.Add(new BezierSegment(c1, c2, p2, true));
            }
        }

        if (fillToY.HasValue)
        {
            figure.Segments.Add(new LineSegment(new Point(points[^1].X, fillToY.Value), true));
            figure.Segments.Add(new LineSegment(new Point(points[0].X, fillToY.Value), true));
            figure.IsClosed = true;
        }

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }

    public Action? SettingsRequested;

    public void ResetPosition()
    {
        _pos = new PositionState();
        ApplyPosition();
        _pos.Left = Left;
        _pos.Top = Top;
        _pos.AnchorH = HorizontalContentAlignment.ToString();
        _pos.AnchorV = VerticalContentAlignment.ToString();
        SavePosition();
    }

    private void SetTab(int index)
    {
        _currentTab = ((index % TabCount) + TabCount) % TabCount;
        _tabHost.Content = _tabViews[_currentTab];
        _collapsedIcon.Content = BuildCollapsedIcon(_currentTab);

        for (var i = 0; i < TabCount; i++)
        {
            _dots[i].Fill = i == _currentTab
                ? Brushes.White
                : new SolidColorBrush(Color.FromArgb(90, 255, 255, 255));
        }

        if (_tabPersistenceReady)
        {
            var tab = _currentTab;
            AppSettings.Update(d => d.LastTab = tab);
        }

        RefreshHistoryIfStale();
        if (_currentTab == RangeTabIndex && _rangePoints is not { Count: >= 2 }) _ = LoadRangeAsync();
    }

    // Small glyphs shown on the collapsed pill for whichever tab was last
    // open, so the pill isn't just a blank capsule (like the Dynamic Island
    // showing the active app's icon). Thin vector strokes to match the
    // capsule's own hairline rim rather than a clashing emoji/bitmap icon.
    private static FrameworkElement BuildPulseIcon() => new Viewbox
    {
        Width = 15,
        Height = 15,
        Child = new ShapePath
        {
            Data = Geometry.Parse("M1,8 L4,8 L6,2.5 L9,13.5 L11,8 L15,8"),
            Stroke = Brushes.White,
            StrokeThickness = 1.6,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round
        }
    };

    private FrameworkElement? BuildCollapsedIcon(int tab) => tab switch
    {
        CurrencyTabIndex => BuildCollapsedRatesRow(),
        ChartTabIndex => BuildCollapsedRatesRow(),
        SwapperTabIndex => BuildCollapsedRatesRow(),
        RangeTabIndex => BuildCollapsedRatesRow(),
        MultiCalcTabIndex => BuildCollapsedRatesRow(),
        _ => null
    };

    private static readonly Color UsdAccent = Color.FromRgb(0x5F, 0xD0, 0x68);
    private static readonly Color EurAccent = Color.FromRgb(0x5A, 0xC8, 0xFA);
    private static readonly Color CnyAccent = Color.FromRgb(0xFF, 0xD1, 0x66);
    private static readonly Color AedAccent = Color.FromRgb(0xC3, 0x9B, 0xFF);
    private static readonly Color TryAccent = Color.FromRgb(0xFF, 0x4D, 0x5E);
    private static readonly Color TrendUpColor = Color.FromRgb(0x30, 0xD1, 0x58);
    private static readonly Color TrendDownColor = Color.FromRgb(0xFF, 0x45, 0x3A);

    // Plain currency glyphs ($/€/¥) are ordinary font characters, not
    // multi-color emoji, so they render fine as text and don't need a
    // vector icon.
    private static StackPanel BuildChip(string symbol, string value, Color accent, bool first, int trend) =>
        BuildChipCore(new TextBlock
        {
            Text = symbol,
            Foreground = new SolidColorBrush(accent),
            FontSize = 15,
            FontWeight = FontWeights.Bold,
            FontFamily = ValueFont,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        }, value, first, trend);

    // A small filled triangle for a day-over-day move, or a thin flat dash
    // when the rate didn't change - drawn instead of a text arrow character
    // so it matches the app's own thin-vector-glyph look (see BuildPulseIcon)
    // rather than pulling in a font's own arrow glyph at an inconsistent
    // weight/baseline.
    private static ShapePath BuildTrendArrow(int trend)
    {
        if (trend == 0)
        {
            return new ShapePath
            {
                Data = Geometry.Parse("M0,4 L8,4"),
                Stroke = new SolidColorBrush(Color.FromArgb(110, 235, 235, 240)),
                StrokeThickness = 1.6,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                Width = 8,
                Height = 8
            };
        }

        return new ShapePath
        {
            Data = Geometry.Parse(trend > 0 ? "M0,8 L4,0 L8,8 Z" : "M0,0 L4,8 L8,0 Z"),
            Fill = new SolidColorBrush(trend > 0 ? TrendUpColor : TrendDownColor),
            Width = 8,
            Height = 8
        };
    }

    private static StackPanel BuildChipCore(FrameworkElement icon, string value, bool first, int trend)
    {
        var chip = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(first ? 0 : 9, 0, 0, 0)
        };
        // A fixed-size slot for the icon/symbol, centered inside it, so a
        // 14px vector glyph and a 15px bold currency character both land on
        // the exact same footprint next to the value text instead of each
        // nudging the baseline by their own natural size - that mismatch is
        // what read as "icon and text aren't symmetric".
        var iconSlot = new Grid
        {
            Width = 16,
            Height = 16,
            VerticalAlignment = VerticalAlignment.Center
        };
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        icon.VerticalAlignment = VerticalAlignment.Center;
        iconSlot.Children.Add(icon);
        chip.Children.Add(iconSlot);

        // Fixed-width trend slot, always populated (flat dash included) -
        // so the chip's width never shifts between refreshes depending on
        // whether that particular currency happened to move that day.
        var trendArrow = BuildTrendArrow(trend);
        trendArrow.HorizontalAlignment = HorizontalAlignment.Center;
        trendArrow.VerticalAlignment = VerticalAlignment.Center;
        var trendSlot = new Grid
        {
            Width = 10,
            Height = 16,
            Margin = new Thickness(1, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        trendSlot.Children.Add(trendArrow);
        chip.Children.Add(trendSlot);

        chip.Children.Add(new TextBlock
        {
            Text = value,
            Foreground = Brushes.White,
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            FontFamily = ValueFont,
            Margin = new Thickness(3, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        });
        return chip;
    }

    private FrameworkElement BuildCollapsedRatesRow()
    {
        if (_latestHistory is not { Count: > 0 } history)
        {
            return new TextBlock
            {
                Text = "₽",
                Foreground = Brushes.White,
                FontSize = 16,
                FontWeight = FontWeights.SemiBold,
                FontFamily = LabelFont
            };
        }

        var r = history[^1];
        var prev = history.Count >= 2 ? history[^2] : null;
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };

        row.Children.Add(BuildChip("$", $"{r.UsdRub:0.0000}", UsdAccent, first: true, Trend(r.UsdRub, prev?.UsdRub)));
        row.Children.Add(BuildChip("€", $"{r.EurRub:0.0000}", EurAccent, first: false, Trend(r.EurRub, prev?.EurRub)));
        row.Children.Add(BuildChip("¥", $"{r.CnyRub:0.0000}", CnyAccent, first: false, Trend(r.CnyRub, prev?.CnyRub)));

        return row;
    }

    // 1 = up, -1 = down, 0 = flat or no prior day to compare against.
    private static int Trend(double current, double? previous)
    {
        if (previous is not { } p || Math.Abs(current - p) < 0.00005) return 0;
        return current > p ? 1 : -1;
    }

    private const double CollapsedRatesWidth = 326;

    private void OnShellMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // Ignored while a switch is already animating rather than queued or
        // stacked - overlapping fade animations on the same property could
        // drop a Completed callback, leaving _tabHost stuck at opacity 0
        // (a "black" pill) with _currentTab desynced so scrolling appeared to
        // do nothing afterward.
        if (!_isExpanded || _tabAnimating) return;
        AnimateTabSwitch(_currentTab + (e.Delta > 0 ? -1 : 1));
        e.Handled = true;
    }

    private void AnimateTabSwitch(int index)
    {
        var newIndex = ((index % TabCount) + TabCount) % TabCount;
        if (newIndex == _currentTab) return;

        _tabAnimating = true;
        var fadeOut = new DoubleAnimation(_tabHost.Opacity, 0, TimeSpan.FromMilliseconds(90))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        fadeOut.Completed += (_, _) =>
        {
            SetTab(newIndex);
            ResizeForTab(newIndex);
            var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(140))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            fadeIn.Completed += (_, _) => _tabAnimating = false;
            _tabHost.BeginAnimation(OpacityProperty, fadeIn);
        };
        _tabHost.BeginAnimation(OpacityProperty, fadeOut);
    }

    // PointToScreen/PointFromScreen return physical pixels while Window.Left/
    // Top are device-independent units, and mixing the two made the pill drift
    // away from the cursor proportionally to drag distance on a scaled display.
    // GetCursorPos + the window's own DPI gives a DIP-consistent screen point.
    private Point GetCursorScreenDip()
    {
        GetCursorPos(out var p);
        var dpi = VisualTreeHelper.GetDpi(this);
        return new Point(p.X / dpi.DpiScaleX, p.Y / dpi.DpiScaleY);
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    // Reads the work area directly via Win32 and converts it with this
    // window's own DPI, so it's guaranteed consistent with Left/Top/Width
    // (also DIPs) regardless of how SystemParameters.WorkArea behaves.
    private Rect GetWorkAreaDip()
    {
        const uint SPI_GETWORKAREA = 0x0030;
        var rect = new NativeRect();
        SystemParametersInfo(SPI_GETWORKAREA, 0, ref rect, 0);
        var dpi = VisualTreeHelper.GetDpi(this);
        return new Rect(
            rect.Left / dpi.DpiScaleX,
            rect.Top / dpi.DpiScaleY,
            (rect.Right - rect.Left) / dpi.DpiScaleX,
            (rect.Bottom - rect.Top) / dpi.DpiScaleY);
    }

    [DllImport("user32.dll")]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref NativeRect pvParam, uint fWinIni);

    // Removing our own DropShadowEffect didn't kill the soft white haze
    // hugging the pill's top edge because it was never ours to begin with -
    // Windows itself tags AllowsTransparency+WindowStyle=None windows with
    // the CS_DROPSHADOW window-class style and DWM paints that glow outside
    // WPF's own rendering pipeline entirely, on top of whatever we draw.
    // Clearing the class style bit is the standard fix.
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        const int GCL_STYLE = -26;
        const long CS_DROPSHADOW = 0x00020000;
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        var style = GetClassLongPtr(hwnd, GCL_STYLE).ToInt64();
        SetClassLongPtr(hwnd, GCL_STYLE, new IntPtr(style & ~CS_DROPSHADOW));

        // ShowInTaskbar=false alone doesn't reliably keep this out of the
        // Alt-Tab switcher - that's driven by WS_EX_APPWINDOW/TOOLWINDOW on
        // the window's extended style, not the taskbar visibility WPF
        // property. Forcing TOOLWINDOW on and APPWINDOW off directly is the
        // actual mechanism Alt-Tab checks.
        const int GWL_EXSTYLE = -20;
        const long WS_EX_TOOLWINDOW = 0x00000080;
        const long WS_EX_APPWINDOW = 0x00040000;
        var exStyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        exStyle = (exStyle | WS_EX_TOOLWINDOW) & ~WS_EX_APPWINDOW;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(exStyle));

        // Topmost=true alone can still get knocked down by another window
        // that also asks for topmost (some games/capture tools, or a UAC
        // prompt), and it doesn't recover on its own. Re-asserting HWND_
        // TOPMOST on a slow timer keeps it pinned above everything without
        // ever touching size/position/activation, so there's nothing for
        // it to visibly flicker - a no-op SetWindowPos when it's already
        // on top, silent recovery on the rare frame it isn't.
        _topmostTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _topmostTimer.Tick += (_, _) => SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        _topmostTimer.Start();
    }

    private DispatcherTimer? _topmostTimer;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "GetClassLongPtr")]
    private static extern IntPtr GetClassLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetClassLongPtr")]
    private static extern IntPtr SetClassLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    // A plain click is a MouseDown+MouseUp with barely any movement - it used
    // to unconditionally start a drag (and instantly collapse the pill) on
    // MouseDown alone, so clicking an expanded pill collapsed it for no
    // reason. Now MouseDown only arms a drag *candidate*; it only becomes a
    // real drag (and only then collapses) once the cursor actually moves past
    // DragStartThreshold in OnShellMouseMove.
    private const double DragStartThreshold = 4;

    private void OnShellMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        _dragCandidate = true;
        _dragging = false;
        _shell.CaptureMouse();
        _dragMouseStart = GetCursorScreenDip();
        _dragWindowStart = new Point(Left, Top);
        _dragBaseH = HorizontalContentAlignment;
        _dragBaseV = VerticalContentAlignment;
        e.Handled = true;
    }

    private void OnShellMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragCandidate || _positionUpdateInProgress) return;

        var current = GetCursorScreenDip();
        var delta = current - _dragMouseStart;

        if (!_dragging)
        {
            if (Math.Abs(delta.X) < DragStartThreshold && Math.Abs(delta.Y) < DragStartThreshold) return;
            _dragging = true;
            _snapped = false;
            _snapArmed = false;
            DebugTrace($"Drag START curLeft={Left:F1} curTop={Top:F1} curH={Height:F1} anchorH={HorizontalContentAlignment} anchorV={VerticalContentAlignment}");
            // Quick (90ms), not the full 220ms - running the collapse's five
            // concurrent animations at full length at the exact moment the
            // drag loop starts hammering Left/Top every mouse move is what
            // read as jerky/laggy right at the start of a drag. Going fully
            // instant instead fixed that but looked like a harsh snap/jump-
            // cut - still animated, just shorter, so it reads as a quick
            // shrink instead of either a stutter or a cut.
            if (!_pinned) CollapseFast();
        }

        _positionUpdateInProgress = true;

        try
        {
            // Computed fully in locals and assigned to Left/Top exactly once
            // each - Left and Top are separate dependency properties, each
            // triggering its own SetWindowPos, so writing them more than once
            // per move (e.g. a raw write followed by a separate clamp write)
            // let an external observer - and occasionally the compositor -
            // catch a torn intermediate frame, which read as teleporting.
            var rawLeft = _dragWindowStart.X + delta.X;
            var rawTop = _dragWindowStart.Y + delta.Y;

            // Live magnetic pull toward a snap point, same alignment only (a
            // point built for a different corner would read its coordinates
            // under the wrong alignment and jump). Alignment itself never
            // changes mid-drag - only on release - so this is a pure position
            // nudge, and hysteresis (wider radius once already snapped) stops
            // the pill flickering in and out right at the capture boundary.
            //
            // The magnet only actually pulls once the drag has left every
            // capture radius at least once (_snapArmed). A drag that STARTS
            // already resting on a point begins disarmed, so nudging away
            // from it isn't fought by its own gravity the instant you move -
            // it only re-engages once you've genuinely left and can approach
            // a point again.
            var stillSnapped = false;
            var withinAnyRadius = false;
            foreach (var (point, radius, h, v) in GetSnapPoints())
            {
                if (_pinned) break;
                if (h != _dragBaseH || v != _dragBaseV) continue;
                var effectiveRadius = _snapped ? radius * 1.2 : radius;
                if (Math.Abs(rawLeft - point.X) < effectiveRadius && Math.Abs(rawTop - point.Y) < effectiveRadius)
                {
                    withinAnyRadius = true;
                    if (_snapArmed)
                    {
                        rawLeft = point.X;
                        rawTop = point.Y;
                        stillSnapped = true;
                    }
                    break;
                }
            }
            _snapped = stillSnapped;
            if (!withinAnyRadius) _snapArmed = true;

            var (clampedLeft, clampedTop) = ClampWindowPosition(rawLeft, rawTop);
            if (Math.Abs(clampedTop - Top) > 1) DebugTrace($"Drag MOVE Top jump {Top:F1} -> {clampedTop:F1} (rawTop={rawTop:F1} curH={Height:F1})");
            Left = clampedLeft;
            Top = clampedTop;
        }
        finally
        {
            _positionUpdateInProgress = false;
        }
    }

    // The earlier "flicker at the edge" turned out to be the clamp math bug
    // (wrong work-area units) fighting itself, not a real DWM edge quirk, so
    // this stays at 0 - fully flush against the monitor edge is reachable.
    private const double DragEdgeMargin = 0;

    private (double Left, double Top) ClampWindowPosition(double left, double top)
    {
        // Used live during drag, which is always mid-collapse-animation (see
        // OnShellMouseUp for the full story) - the target collapsed size is
        // what actually matters, not wherever _shell.Width/Height happens to
        // be that frame. When called at rest (ClampToScreen), the two are
        // already equal, so this is safe either way.
        var area = GetWorkAreaDip();
        var pillWidth = GetCollapsedWidth();
        var pillHeight = CollapsedHeight;

        var pillLeft = PillLeftFromWindow(left, pillWidth);
        var pillTop = PillTopFromWindow(top, pillHeight, pillHeight);

        var clampedPillLeft = Math.Clamp(pillLeft, area.Left + DragEdgeMargin, area.Right - DragEdgeMargin - pillWidth);
        var clampedPillTop = Math.Clamp(pillTop, area.Top + DragEdgeMargin, area.Bottom - DragEdgeMargin - pillHeight);

        var newLeft = WindowLeftFromPill(clampedPillLeft, pillWidth, HorizontalContentAlignment);
        var newTop = WindowTopFromPill(clampedPillTop, pillHeight, VerticalContentAlignment, pillHeight);
        return (newLeft, newTop);
    }

    private void ClampToScreen()
    {
        var (clampedLeft, clampedTop) = ClampWindowPosition(Left, Top);
        Left = clampedLeft;
        Top = clampedTop;
    }

    private void OnShellMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragCandidate) return;
        _dragCandidate = false;
        _shell.ReleaseMouseCapture();

        // Never crossed the drag threshold - it was just a click. The pill
        // never collapsed for this gesture, so there's nothing to finalize;
        // leave it exactly as it was (expanded, hovering).
        if (!_dragging) return;
        _dragging = false;

        if (_pinned)
        {
            _pos.Left = Left;
            _pos.Top = Top;
            _pos.AnchorH = HorizontalContentAlignment.ToString();
            _pos.AnchorV = VerticalContentAlignment.ToString();
            SavePosition();
            return;
        }

        // Snapping only happens here, once, at release - not on every move
        // event during the drag. Alignment never changes mid-drag, so if this
        // drag started at a DIFFERENT corner (e.g. dragged in from bottom-
        // left), Left/Top are still being tracked in that corner's alignment
        // terms - comparing them directly against a point defined for a
        // different alignment (home is Right/Top) silently never matched,
        // so dragging in from elsewhere could never dock at home. Re-express
        // the pill's actual current position in each candidate point's own
        // alignment terms before comparing - this is a no-op when the drag's
        // alignment already matches the candidate (the common case), and
        // only matters when it doesn't.
        // The collapse triggered by this drag animates over ~220ms - a quick
        // release (exactly what happens when someone decisively drops the
        // pill in a corner) lands while _shell.Width/Height are still mid-
        // shrink, not yet at their final collapsed size. Using the live,
        // still-animating value here made the corner math intermittently
        // wrong depending on how fast the release happened. Use the actual
        // target collapsed size instead - deterministic regardless of where
        // the animation currently is.
        var pillWidth = GetCollapsedWidth();
        var pillHeight = CollapsedHeight;
        var pillLeft = PillLeftFromWindow(Left, pillWidth);
        var pillTop = PillTopFromWindow(Top, pillHeight, pillHeight);

        var docked = false;
        foreach (var (point, radius, h, v) in GetSnapPoints())
        {
            var equivLeft = WindowLeftFromPill(pillLeft, pillWidth, h);
            var equivTop = WindowTopFromPill(pillTop, pillHeight, v, pillHeight);

            if (Math.Abs(equivLeft - point.X) < radius && Math.Abs(equivTop - point.Y) < radius)
            {
                SetAnchor(h, v);
                Left = point.X;
                Top = point.Y;
                docked = true;
                break;
            }
        }
        // Didn't land on a snap point - still make sure alignment matches
        // whatever quadrant it was actually dropped in (e.g. dragged in from
        // one corner and released somewhere in open space), done once here
        // rather than on every later hover.
        if (!docked) UpdateGrowthAlignment();

        _pos.Left = Left;
        _pos.Top = Top;
        _pos.AnchorH = HorizontalContentAlignment.ToString();
        _pos.AnchorV = VerticalContentAlignment.ToString();
        SavePosition();

        if (!_shell.IsMouseOver) Collapse();
    }

    private (Point Point, double Radius, HorizontalAlignment H, VerticalAlignment V)[] GetSnapPoints()
    {
        var area = GetWorkAreaDip();
        var topRight = new Point(area.Right - EdgeMargin - Width, area.Top + TopEdgeMargin);

        // Only the default home corner snaps for now - more points get added
        // here once their exact coordinates are picked with the coord-watcher
        // tool (tools/coord-watcher.ps1).
        return new[]
        {
            (topRight, SnapThreshold * 1.3, HorizontalAlignment.Right, VerticalAlignment.Top)
        };
    }

    private void ApplyPinMorph()
    {
        if (_pinButton == null) return;
        var m = PinMorph;
        var t = Math.Clamp(m, 0, 1);
        var accent = Wpf.Ui.Appearance.ApplicationAccentColorManager.SystemAccent;

        _pinButton.CornerRadius = new CornerRadius(Math.Max(0, 13 - 6 * m));
        _pinScale.ScaleX = _pinScale.ScaleY = 1 + 0.08 * m;
        _pinRotate.Angle = 45 * (1 - m);

        var idleAlpha = _pinHover ? 75 : 40;
        _pinBgBrush.Color = Color.FromArgb(
            (byte)(idleAlpha + (255 - idleAlpha) * t),
            (byte)(255 + (accent.R - 255) * t),
            (byte)(255 + (accent.G - 255) * t),
            (byte)(255 + (accent.B - 255) * t));
        _pinIconBrush.Color = Color.FromArgb((byte)(190 + 65 * t), 255, 255, 255);
        if (!ReferenceEquals(_pinButton.Background, _pinBgBrush)) _pinButton.Background = _pinBgBrush;
        if (!ReferenceEquals(_pinIcon.Fill, _pinIconBrush)) _pinIcon.Fill = _pinIconBrush;
    }

    private void SetPinned(bool pinned)
    {
        _pinned = pinned;
        BeginAnimation(PinMorphProperty, Motion.Spring(PinMorph, pinned ? 1 : 0, Motion.Pin));
        Motion.Settle(500, () => Motion.Clear(this, PinMorphProperty, pinned ? 1.0 : 0.0));
        AppSettings.Update(d => d.Pinned = pinned);
        if (pinned) Expand();
        else if (!IsCursorOverHoverZone()) Collapse();
    }

    public void RestorePinned()
    {
        if (_pinned) Dispatcher.BeginInvoke(new Action(Expand), DispatcherPriority.Loaded);
    }

    public void ToggleForcedVisibility()
    {
        _forcedHidden = !_forcedHidden;
        if (_forcedHidden)
        {
            AnimateOpacity(0, () => Visibility = Visibility.Hidden);
        }
        else
        {
            Visibility = Visibility.Visible;
            AnimateOpacity(1, null);
        }
    }

    private void Expand()
    {
        if (_forcedHidden || _isExpanded) return;
        _isExpanded = true;
        // No UpdateGrowthAlignment() here - it used to run on every single
        // hover. Collapsed width varies by tab (currency's compact rate row
        // is much wider than the plain icon), so recomputing the quadrant on
        // every hover with whatever width happened to be active could flip
        // alignment even though the window never actually moved, jerking the
        // pill sideways. It only needs to run once, right after a drag
        // actually changes the position (see OnShellMouseUp).
        _content.Visibility = Visibility.Visible;
        _settingsButton.Visibility = Visibility.Visible;
        _pinButton.Visibility = Visibility.Visible;

        // The real window only ever needs to be as tall as whichever tab
        // needs the most room (ExpandedHeight) while the pill is expanded
        // at ALL - which tab happens to be showing is purely a _shell-level
        // concern once the window is already that size. Growing the window
        // to the max here, once, means every later tab switch (see
        // ResizeForTab) never touches the HWND again for as long as the
        // pill stays expanded - a native resize on every single scroll tick
        // was what actually made tab-switching choppy (chasing the
        // animation with a synchronous Win32 call every few milliseconds is
        // real OS-level work, not just a compositor transform), not the
        // shell's own WPF animation.
        //
        // Snapped, not animated (see SnapWindowHeight) - and happens right
        // here, before ResizeForTab even starts _shell moving, so nothing
        // else is animating anywhere in the window at the moment this
        // native resize happens.
        _hoverZoneWidth = ExpandedWidth;
        _hoverZoneHeight = ExpandedHeight;
        _resizingTab = true;
        SnapWindowHeight(ExpandedHeight);

        ResizeForTab(_currentTab);
        AnimateContentOpacity(1);
        AnimateSettingsButtonOpacity(1);
        AnimateCollapsedIconOpacity(0);
        RefreshHistoryIfStale();
    }

    // Purely a _shell-level resize now - the window is already sitting at
    // ExpandedHeight for as long as the pill is expanded (see Expand()), so
    // switching tabs never needs the HWND at all, just _shell animating
    // within an already-big-enough container. Also used by Expand() itself
    // to animate _shell to the current tab's size once the window has
    // already grown to make room.
    private void ResizeForTab(int tab)
    {
        var targetHeight = TabExpandedHeight[tab];
        DebugTrace($"ResizeForTab targetH={targetHeight:F1} shellW={_shell.Width:F1} shellH={_shell.Height:F1}");
        _resizingTab = true;
        AnimateShell(ExpandedWidth, targetHeight, CollapsedHeight * 0.34, AnimDuration, onHeightCompleted: () =>
        {
            _resizingTab = false;

            // Reconcile against whatever the cursor is ACTUALLY doing right
            // now - a Enter/Leave that arrived while this resize was still
            // guarded only updated _isMouseOverShell, it never got to act.
            // Re-derived fresh (not just trusting the last Enter/Leave) for
            // the same reason _shell.MouseEnter does - it's the truth this
            // whole guard/reconcile dance exists to protect.
            if (_dragging) return;
            _isMouseOverShell = IsCursorOverHoverZone();
            if (_isMouseOverShell && !_isExpanded) Expand();
            else if (!_isMouseOverShell && _isExpanded) Collapse();
        });
    }

    // Shared by Collapse/CollapseFast. _shell shrinks first, purely in WPF -
    // the window is still sitting at its full ExpandedHeight the whole time
    // (see Expand()), so there's nothing for the still-larger window to
    // clip and nothing for the still-smaller-target window to mismatch
    // against either. Only once _shell has genuinely finished shrinking
    // does the real window shrink to match, by which point shell and
    // target already agree - there's nothing left to get wrong.
    //
    // The deferred window-shrink used to be scheduled on its own
    // DispatcherTimer with a hardcoded duration guess. A quick hover in/out
    // (or fast tab flick) fired a SECOND resize before the first timer ever
    // ticked, so two independent timer chains raced each other - each
    // closing over its own stale target height - and the window would snap
    // to whichever one happened to fire last, sometimes stepping through an
    // intermediate size on the way ("blinks and changes size"). Hanging the
    // deferred resize off the shell's OWN height animation Completed event
    // instead removes the race entirely: WPF cancels an animation's pending
    // Completed callback the instant a NEW animation starts on that same
    // property (BeginAnimation always supersedes), so only the most recent
    // resize's callback can ever actually fire - no manual bookkeeping, no
    // timing guesswork.
    private void CollapseShellAndWindow(TimeSpan duration)
    {
        var targetWidth = GetCollapsedWidth();
        DebugTrace($"CollapseShellAndWindow targetW={targetWidth:F1} curTop={Top:F1} curH={Height:F1} shellW={_shell.Width:F1} shellH={_shell.Height:F1}");

        // Snapped instantly, not animated - see the comment by
        // _shell.MouseEnter for why the hover zone must never be a moving
        // target while _shell's own visual size animates underneath it.
        _hoverZoneWidth = targetWidth;
        _hoverZoneHeight = CollapsedHeight;

        _resizingTab = true;

        AnimateShell(targetWidth, CollapsedHeight, CollapsedHeight / 2, duration, onHeightCompleted: () =>
        {
            // SnapWindowHeight now animates (see its own comment) - the
            // reconcile below waits for its onCompleted instead of running
            // right after the call, same idea as ResizeForTab's own
            // onHeightCompleted.
            SnapWindowHeight(CollapsedHeight, duration, onCompleted: () =>
            {
                _resizingTab = false;

                // Reconcile against whatever the cursor is ACTUALLY doing
                // right now - a Enter/Leave that arrived while this resize
                // was still guarded only updated _isMouseOverShell, it never
                // got to act. Re-derived fresh (not just trusting the last
                // Enter/Leave) for the same reason _shell.MouseEnter does -
                // it's the truth this whole guard/reconcile dance exists to
                // protect.
                if (_dragging) return;
                _isMouseOverShell = IsCursorOverHoverZone();
                if (_isMouseOverShell && !_isExpanded) Expand();
                else if (!_isMouseOverShell && _isExpanded) Collapse();
            });
        });
    }

    // The pill's default growth is symmetric - it pops open centered on
    // wherever it's currently sitting, expanding equally in every direction
    // (Center/Center). Only when that would actually run the expanded
    // footprint off the work area on some side does that axis fall back to
    // an edge anchor, growing away from just that edge. Recomputed fresh
    // from scratch (not "keep whatever it was before") every time, so a
    // pill sitting anywhere with room around it always lands on Center -
    // it doesn't get stuck growing lopsided just because it was once
    // dragged in from a corner.
    private void UpdateGrowthAlignment()
    {
        // Only ever called right after a drag (see OnShellMouseUp), which is
        // always in the middle of collapsing - use the target collapsed size,
        // not the live mid-animation _shell.Width/Height (see the comment in
        // OnShellMouseUp for why that was wrong).
        var area = GetWorkAreaDip();
        var pillWidth = GetCollapsedWidth();
        var pillHeight = CollapsedHeight;

        var pillLeft = PillLeftFromWindow(Left, pillWidth);
        var pillTop = PillTopFromWindow(Top, pillHeight, pillHeight);
        var pillCenterX = pillLeft + pillWidth / 2;
        var pillCenterY = pillTop + pillHeight / 2;

        var newH = HorizontalAlignment.Center;
        if (pillCenterX - ExpandedWidth / 2 < area.Left) newH = HorizontalAlignment.Left;
        else if (pillCenterX + ExpandedWidth / 2 > area.Right) newH = HorizontalAlignment.Right;

        var newV = VerticalAlignment.Center;
        if (pillCenterY - ExpandedHeight / 2 < area.Top) newV = VerticalAlignment.Top;
        else if (pillCenterY + ExpandedHeight / 2 > area.Bottom) newV = VerticalAlignment.Bottom;

        if (newH == HorizontalContentAlignment && newV == VerticalContentAlignment) return;

        var newLeft = WindowLeftFromPill(pillLeft, pillWidth, newH);
        var newTop = WindowTopFromPill(pillTop, pillHeight, newV, pillHeight);

        SetAnchor(newH, newV);
        Left = newLeft;
        Top = newTop;
    }

    // Re-expresses the pill's visible left/top edge in terms of the WINDOW's
    // Left/Top for a given alignment - Center means the pill sits in the
    // middle of the fixed-size window (so it grows equally on both sides),
    // Right/Bottom means it's pinned to that far edge (grows only away from
    // it), and Left/Top (the switch default) means it's pinned to the near
    // edge (grows only forward from it).
    private double PillLeftFromWindow(double windowLeft, double pillWidth) => HorizontalContentAlignment switch
    {
        HorizontalAlignment.Right => windowLeft + Width - pillWidth,
        HorizontalAlignment.Center => windowLeft + (Width - pillWidth) / 2,
        _ => windowLeft
    };

    // windowHeight is explicit, not read off Height, because every caller
    // here is reasoning about a TARGET pill size while the real Window.Height
    // may still be mid a DEFERRED shrink (CollapseFast defers the actual
    // ResizeWindowHeight call to the shell animation's Completed - see
    // ResizeForTab/CollapseShellAndWindow). Reading the live Height during that window used
    // the still-expanded value for Bottom/Center math, then the deferred
    // resize applied its OWN correction on top a moment later - two
    // conflicting corrections compounding into the pill visibly jumping
    // right as a drag starts (or a corner-snap released quickly enough to
    // land before the deferred shrink caught up). Passing the same target
    // height the caller already resolved (pillHeight/CollapsedHeight, per
    // the "use target collapsed size, not live mid-animation size" pattern
    // already established at every one of these call sites) makes this
    // conversion consistent with where the window is ACTUALLY headed,
    // not where it happens to still be this frame.
    private double PillTopFromWindow(double windowTop, double pillHeight, double windowHeight) => VerticalContentAlignment switch
    {
        VerticalAlignment.Bottom => windowTop + windowHeight - pillHeight,
        VerticalAlignment.Center => windowTop + (windowHeight - pillHeight) / 2,
        _ => windowTop
    };

    // The inverse of the above: given where the pill should visibly sit,
    // solves for the WINDOW's Left/Top under a given (possibly different)
    // target alignment, so switching alignment never moves the visible pill.
    private double WindowLeftFromPill(double pillLeft, double pillWidth, HorizontalAlignment h) => h switch
    {
        HorizontalAlignment.Right => pillLeft - Width + pillWidth,
        HorizontalAlignment.Center => pillLeft - (Width - pillWidth) / 2,
        _ => pillLeft
    };

    // See PillTopFromWindow for why windowHeight is explicit instead of Height.
    private double WindowTopFromPill(double pillTop, double pillHeight, VerticalAlignment v, double windowHeight) => v switch
    {
        VerticalAlignment.Bottom => pillTop - windowHeight + pillHeight,
        VerticalAlignment.Center => pillTop - (windowHeight - pillHeight) / 2,
        _ => pillTop
    };

    // Single source of truth for the anchor: keeps the Window's own
    // HorizontalContentAlignment/VerticalContentAlignment as plain state
    // storage (read all over this file, and persisted via _pos.AnchorH/V),
    // AND drives the actual on-screen position by setting _shell's own
    // HorizontalAlignment/VerticalAlignment within the stretching contentHost
    // Grid - the only alignment mechanism WPF reliably honors here.
    private void SetAnchor(HorizontalAlignment h, VerticalAlignment v)
    {
        HorizontalContentAlignment = h;
        VerticalContentAlignment = v;
        _shell.HorizontalAlignment = h;
        _shell.VerticalAlignment = v;
    }

    // Ground truth for "is the cursor over the pill" - deliberately NOT
    // _shell's own (animating) bounds. See the comment by _shell.MouseEnter
    // for why trusting the live hit-test result causes a self-sustaining
    // resize oscillation.
    private System.Windows.Controls.Primitives.Popup? _openDropdown;
    private Border? _openDropdownBorder;

    private void CloseDropdown()
    {
        if (_openDropdown is { IsOpen: true } popup) popup.IsOpen = false;
    }

    private bool IsCursorOverHoverZone()
    {
        if (_openDropdownBorder is { IsMouseOver: true }) return true;
        var cursor = GetCursorScreenDip();
        var pillLeft = PillLeftFromWindow(Left, _hoverZoneWidth);
        var pillTop = PillTopFromWindow(Top, _hoverZoneHeight, _hoverZoneHeight);
        return cursor.X >= pillLeft && cursor.X <= pillLeft + _hoverZoneWidth
            && cursor.Y >= pillTop && cursor.Y <= pillTop + _hoverZoneHeight;
    }

    private static void DebugTrace(string msg)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CurrencyIsland", "resize-trace.log"),
                $"{DateTime.Now:HH:mm:ss.fff} {msg}{Environment.NewLine}");
        }
        catch { }
    }

    // Changes the WINDOW's own Height (the actual HWND, not just _shell) to
    // exactly targetHeight, in one synchronous step - no animation, no
    // timer, nothing else touching the window while this runs. Every
    // attempt at spreading this over time (a DispatcherTimer driving
    // repeated native resizes, then BeginAnimation on Top/Height directly)
    // read as jerky regardless of mechanism or how it was clocked - tab
    // switching (ResizeForTab), which never touches the window at ALL, is
    // the one thing that's stayed smooth through all of this. So the
    // window doesn't animate either, same as it doesn't during a tab
    // switch - it just isn't touched while anything else is moving,
    // period. Keeps whichever edge VerticalContentAlignment currently
    // anchors to fixed in place, so the visible capsule doesn't jump
    // sideways-of-center when its height changes.
    // Animating this (BeginAnimation on Top/Height, same composition clock
    // as _shell's own animation) was tried twice now, on the theory that a
    // ~140 DIP instant jump inherently reads as a teleport no matter how
    // cleanly it's presented. Still came back jerky both times. Confirmed
    // (2026-09-28) this whole class of resize glitch - jerky when
    // animated, a one-frame teleport when snapped, immune to every
    // presentation-timing fix tried (DwmFlush, staged hide/reveal,
    // software rendering) - is specific to THIS machine: an older build
    // with none of that day's fixes ran clean on a different laptop. Back
    // to the plain instant snap, which is at least the simplest, most
    // "just like tab switching" version of this - once Windhawk (or
    // whatever this machine's actual culprit is) is out of the way, this
    // is the version that should read as clean as tab-switching already
    // does.
    private void SnapWindowHeight(double targetHeight, TimeSpan? duration = null, Action? onCompleted = null)
    {
        if (Math.Abs(Height - targetHeight) < 0.5)
        {
            onCompleted?.Invoke();
            return;
        }

        var oldHeight = Height;
        var newTop = VerticalContentAlignment switch
        {
            VerticalAlignment.Bottom => Top + oldHeight - targetHeight,
            VerticalAlignment.Center => Top + (oldHeight - targetHeight) / 2,
            _ => Top
        };

        DebugTrace($"SnapWindowHeight fromH={oldHeight:F1} toH={targetHeight:F1} fromTop={Top:F1} toTop={newTop:F1}");

        Top = newTop;
        Height = targetHeight;
        onCompleted?.Invoke();
    }

    private void Collapse()
    {
        if (!_isExpanded || _pinned) return;
        _isExpanded = false;
        CloseDropdown();
        _collapsedIcon.Content = BuildCollapsedIcon(_currentTab);
        CollapseShellAndWindow(AnimDuration);
        // Matched to AnimDuration (not the shorter default) so these are
        // still actively fading at the exact moment the shell's shrink
        // completes and SnapWindowHeight's brief invisible window-resize
        // fires - with the default (shorter) duration, every other fade
        // had already finished well before that point, leaving the
        // window's own hide/resize/show as the only thing moving in an
        // otherwise fully static frame, which is exactly what made it
        // read as a stray blink instead of part of the same motion.
        AnimateContentOpacity(0, onCompleted: () => _content.Visibility = Visibility.Hidden, AnimDuration);
        AnimateSettingsButtonOpacity(0, onCompleted: HideCornerButtons, AnimDuration);
        AnimateCollapsedIconOpacity(1, AnimDuration);
    }

    // Same end state as Collapse(), but quick instead of the full-length
    // animation - used only when a drag starts, so the size/opacity
    // transition doesn't fight the drag loop's per-move Left/Top writes for
    // as long. Fully instant (no animation at all) turned out to look like
    // a harsh snap/jump-cut instead of a shrink - still animated, just a lot
    // shorter, so it reads as a quick shrink rather than either a stutter
    // or a cut.
    private static readonly TimeSpan FastAnimDuration = TimeSpan.FromMilliseconds(90);

    private void CollapseFast()
    {
        if (!_isExpanded || _pinned) return;
        _isExpanded = false;
        CloseDropdown();
        _collapsedIcon.Content = BuildCollapsedIcon(_currentTab);
        CollapseShellAndWindow(FastAnimDuration);
        AnimateContentOpacity(0, onCompleted: () => _content.Visibility = Visibility.Hidden, FastAnimDuration);
        AnimateSettingsButtonOpacity(0, onCompleted: HideCornerButtons, FastAnimDuration);
        AnimateCollapsedIconOpacity(1, FastAnimDuration);
    }

    private double GetCollapsedWidth()
    {
        if (_latestHistory is { Count: > 0 }) return CollapsedRatesWidth;
        return CollapsedWidth;
    }

    // Purely a _shell-level animation - it never touches the real window.
    // Callers that DO need the window involved (Expand's initial grow to
    // ExpandedHeight, CollapseShellAndWindow's final shrink) do that
    // themselves, once, outside of this function - see the comments there
    // for why a resize animation is never a good time to also be resizing
    // the HWND it lives inside.
    private void AnimateShell(double targetWidth, double targetHeight, double targetRadius, TimeSpan? duration = null, Action? onHeightCompleted = null)
    {
        var animDuration = duration ?? AnimDuration;
        var plainEase = new CubicEase { EasingMode = EasingMode.EaseOut };

        DebugTrace($"AnimateShell START fromW={_shell.Width:F1} fromH={_shell.Height:F1} toW={targetWidth:F1} toH={targetHeight:F1} dur={animDuration.TotalMilliseconds}");
        var widthAnim = new DoubleAnimation(_shell.Width, targetWidth, animDuration) { EasingFunction = plainEase };
        var heightAnim = new DoubleAnimation(_shell.Height, targetHeight, animDuration) { EasingFunction = plainEase };
        var radiusAnim = new CornerRadiusAnimation(_shell.CornerRadius, new CornerRadius(targetRadius), animDuration) { EasingFunction = plainEase };

        heightAnim.Completed += (_, _) =>
        {
            DebugTrace($"AnimateShell shell height anim COMPLETED actualH={_shell.Height:F1} winTop={Top:F1} winH={Height:F1}");
            onHeightCompleted?.Invoke();
        };

        _shell.BeginAnimation(WidthProperty, widthAnim);
        _shell.BeginAnimation(HeightProperty, heightAnim);
        _shell.BeginAnimation(Border.CornerRadiusProperty, radiusAnim);
    }

    private void AnimateContentOpacity(double target, Action? onCompleted = null, TimeSpan? duration = null)
    {
        var anim = new DoubleAnimation(_content.Opacity, target, duration ?? TimeSpan.FromMilliseconds(150));
        if (onCompleted != null) anim.Completed += (_, _) => onCompleted();
        _content.BeginAnimation(OpacityProperty, anim);
    }

    private void AnimateCollapsedIconOpacity(double target, TimeSpan? duration = null)
    {
        var anim = new DoubleAnimation(_collapsedIcon.Opacity, target, duration ?? TimeSpan.FromMilliseconds(150));
        _collapsedIcon.BeginAnimation(OpacityProperty, anim);
    }

    // The settings gear now lives outside _content (see BuildInnerContent) so
    // its corner margin isn't at the mercy of _content's own asymmetric
    // padding - which means it also needs its own explicit show/hide fade in
    // Expand()/Collapse()/CollapseFast() instead of inheriting _content's.
    private void HideCornerButtons()
    {
        _settingsButton.Visibility = Visibility.Hidden;
        _pinButton.Visibility = Visibility.Hidden;
    }

    private void AnimateSettingsButtonOpacity(double target, Action? onCompleted = null, TimeSpan? duration = null)
    {
        var length = duration ?? TimeSpan.FromMilliseconds(150);
        var anim = new DoubleAnimation(_settingsButton.Opacity, target, length);
        if (onCompleted != null) anim.Completed += (_, _) => onCompleted();
        _settingsButton.BeginAnimation(OpacityProperty, anim);
        _pinButton.BeginAnimation(OpacityProperty, new DoubleAnimation(_pinButton.Opacity, target, length));
    }

    private void AnimateOpacity(double target, Action? onDone)
    {
        var anim = new DoubleAnimation(Opacity, target, TimeSpan.FromMilliseconds(200));
        if (onDone != null) anim.Completed += (_, _) => onDone();
        BeginAnimation(OpacityProperty, anim);
    }

    private void ApplyPosition()
    {
        if (double.IsNaN(_pos.Left) || double.IsNaN(_pos.Top))
        {
            var area = GetWorkAreaDip();
            SetAnchor(HorizontalAlignment.Right, VerticalAlignment.Top);
            Left = area.Right - EdgeMargin - Width;
            Top = area.Top + TopEdgeMargin;
            return;
        }

        SetAnchor(
            Enum.TryParse<HorizontalAlignment>(_pos.AnchorH, out var h) ? h : HorizontalAlignment.Right,
            Enum.TryParse<VerticalAlignment>(_pos.AnchorV, out var v) ? v : VerticalAlignment.Top);
        Left = _pos.Left;
        Top = _pos.Top;
        ClampToScreen();
    }

    private void LoadPosition()
    {
        try
        {
            if (File.Exists(_stateFile))
            {
                var json = File.ReadAllText(_stateFile);
                var loaded = JsonSerializer.Deserialize<PositionState>(json);
                if (loaded != null) _pos = loaded;
            }
        }
        catch
        {
            // corrupt or unreadable state file - fall back to default position
        }
    }

    private void SavePosition()
    {
        try
        {
            File.WriteAllText(_stateFile, JsonSerializer.Serialize(_pos));
        }
        catch
        {
            // best-effort persistence only
        }
    }

}

internal sealed class CornerRadiusAnimation : AnimationTimeline
{
    public CornerRadius From { get; }
    public CornerRadius To { get; }

    public CornerRadiusAnimation(CornerRadius from, CornerRadius to, TimeSpan duration)
    {
        From = from;
        To = to;
        Duration = new Duration(duration);
    }

    public EasingFunctionBase? EasingFunction { get; set; }

    public override Type TargetPropertyType => typeof(CornerRadius);

    protected override Freezable CreateInstanceCore() => new CornerRadiusAnimation(From, To, Duration.TimeSpan);

    public override object GetCurrentValue(object defaultOriginValue, object defaultDestinationValue, AnimationClock clock)
    {
        var progress = clock.CurrentProgress ?? 0;
        if (EasingFunction != null) progress = EasingFunction.Ease(progress);

        double Lerp(double a, double b) => a + (b - a) * progress;

        return new CornerRadius(
            Lerp(From.TopLeft, To.TopLeft),
            Lerp(From.TopRight, To.TopRight),
            Lerp(From.BottomRight, To.BottomRight),
            Lerp(From.BottomLeft, To.BottomLeft));
    }
}

public sealed class App : Application
{
    private Forms.NotifyIcon? _trayIcon;
    private IslandWindow? _window;
    private SettingsWindow? _settingsWindow;
    private UpdatePromptWindow? _updatePromptWindow;
    private string? _updateNotifiedVersion;
    private string? _pendingUpdateAssetUrl;
    private string _pendingUpdateNotes = "";
    private long _pendingUpdateSize;
    private DateTime? _pendingUpdatePublished;
    private string _pendingUpdateReleaseUrl = "";
    private System.Threading.CancellationTokenSource? _updateCts;
    private bool _updateApplying;
    private System.Threading.Mutex? _instanceMutex;

    [STAThread]
    public static void Main()
    {
        var app = new App();
        app.Run();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        if (!AcquireSingleInstance(e.Args.Contains("--updated")))
        {
            SelfUpdater.Log("another instance is already running, exiting");
            Shutdown();
            return;
        }

        // The actual root cause of the black settings window: this app has no
        // App.xaml, so the Wpf.Ui theme/control resource dictionaries that a
        // normal project merges declaratively (see SircleToSearch's App.xaml:
        // <ui:ThemesDictionary Theme="Light"/> + <ui:ControlsDictionary/>)
        // never got merged here. ApplicationThemeManager.Apply alone doesn't
        // substitute for that. Without those dictionaries, CardControl/
        // ToggleSwitch/FluentWindow's chrome all fall back to unstyled/blank
        // rendering (which is what "black window" and "empty card rows"
        // both were). Forcing software rendering was a wrong turn chasing a
        // remote-desktop theory - it never was that, and it left a stray
        // opaque-white artifact behind the transparent island pill. Fixed
        // properly by merging the same dictionaries SircleToSearch uses.
        Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ThemesDictionary { Theme = Wpf.Ui.Appearance.ApplicationTheme.Light });
        Resources.MergedDictionaries.Add(new Wpf.Ui.Markup.ControlsDictionary());

        // If autostart is on at all, make sure the registered path still
        // points at wherever this build is actually running from - see
        // AppSettings.RepairAutostart for why that drifts.
        AppSettings.RepairAutostart();
        SelfUpdater.CleanupLeftovers();
        SelfUpdater.Log($"app start v{AppVersion.Current} path={Environment.ProcessPath}");
        CheckUrgentUpdateFlag();

        _window = new IslandWindow();
        _window.SettingsRequested = OpenSettings;
        _window.Show();
        _window.RestorePinned();

        _trayIcon = new Forms.NotifyIcon
        {
            Icon = BuildTrayIcon(),
            Visible = true,
            Text = "Currency Island"
        };

        var menu = new Forms.ContextMenuStrip();
        var toggleItem = menu.Items.Add("Show/hide");
        toggleItem.Click += (_, _) => _window?.ToggleForcedVisibility();
        var settingsItem = menu.Items.Add("Settings");
        settingsItem.Click += (_, _) => OpenSettings();
        var checkUpdatesItem = menu.Items.Add("Check for updates");
        checkUpdatesItem.Click += (_, _) => CheckForUpdatesInBackground(manual: true);
        menu.Items.Add(new Forms.ToolStripSeparator());
        var exitItem = menu.Items.Add("Exit");
        exitItem.Click += (_, _) => Shutdown();

        _trayIcon.ContextMenuStrip = menu;
        _trayIcon.DoubleClick += (_, _) => _window?.ToggleForcedVisibility();
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

        CheckForUpdatesInBackground();

        // A single check at startup meant an already-running instance
        // never found out about a release published after it launched -
        // this app can sit in the tray for days. Silent (manual: false) -
        // no balloon for "still up to date", same as the startup check;
        // the prompt window is still the only thing that ever surfaces an
        // available update, and it still only ever applies on a click.
        _updateCheckTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(15) };
        _updateCheckTimer.Tick += (_, _) =>
        {
            SelfUpdater.Log("periodic recheck tick");
            CheckForUpdatesInBackground();
        };
        _updateCheckTimer.Start();
    }

    private DispatcherTimer? _updateCheckTimer;

    private void CheckUrgentUpdateFlag()
    {
        try
        {
            if (!File.Exists(SelfUpdater.UrgentUpdateFlagPath)) return;
            File.Delete(SelfUpdater.UrgentUpdateFlagPath);
            _trayIcon?.ShowBalloonTip(8000, "Currency Island",
                "Emergency update installed.", Forms.ToolTipIcon.Info);
        }
        catch
        {
            // best-effort notification only
        }
    }

    private async void CheckForUpdatesInBackground(bool manual = false)
    {
        try
        {
            SelfUpdater.Log($"check start manual={manual} current={AppVersion.Current}");
            var result = await UpdateChecker.CheckAsync();
            SelfUpdater.Log($"check result latest={result.LatestVersion} available={result.UpdateAvailable} asset={result.AssetDownloadUrl is not null} urgent={result.IsUrgent}");
            if (!result.UpdateAvailable || result.AssetDownloadUrl is null)
            {
                if (manual)
                    _trayIcon?.ShowBalloonTip(5000, "Currency Island", "You are up to date.", Forms.ToolTipIcon.Info);
                return;
            }

            if (!manual && result.LatestVersion == _updateNotifiedVersion)
            {
                SelfUpdater.Log($"prompt for {result.LatestVersion} already shown this session, skipping");
                return;
            }
            _updateNotifiedVersion = result.LatestVersion;
            _pendingUpdateAssetUrl = result.AssetDownloadUrl;
            _pendingUpdateNotes = result.Notes;
            _pendingUpdateSize = result.AssetSize;
            _pendingUpdatePublished = result.Published;
            _pendingUpdateReleaseUrl = result.ReleaseUrl;

            SelfUpdater.Log($"showing update prompt for {result.LatestVersion}");
            ShowUpdatePrompt(result.IsUrgent);
        }
        catch (Exception ex)
        {
            SelfUpdater.Log($"check failed: {ex.Message}");
            if (manual)
                _trayIcon?.ShowBalloonTip(5000, "Currency Island", "Could not check for updates.", Forms.ToolTipIcon.Warning);
        }
    }

    public void OfferUpdate(string version, string assetUrl, string notes = "", long size = 0, DateTime? published = null, string releaseUrl = "")
    {
        _updateNotifiedVersion = version;
        _pendingUpdateAssetUrl = assetUrl;
        _pendingUpdateNotes = notes;
        _pendingUpdateSize = size;
        _pendingUpdatePublished = published;
        _pendingUpdateReleaseUrl = releaseUrl;
        ShowUpdatePrompt(urgent: false);
    }

    private void ShowUpdatePrompt(bool urgent)
    {
        if (_pendingUpdateAssetUrl is not { } assetUrl) return;

        if (_updatePromptWindow is not null)
        {
            _updatePromptWindow.Activate();
            return;
        }

        _updatePromptWindow = new UpdatePromptWindow(_updateNotifiedVersion ?? "", urgent, _pendingUpdateNotes, _pendingUpdateSize, _pendingUpdatePublished, _pendingUpdateReleaseUrl);
        _updatePromptWindow.UpdateAccepted += () => _ = ApplyUpdateAsync(assetUrl, urgent);
        _updatePromptWindow.CancelRequested += () => _updateCts?.Cancel();
        _updatePromptWindow.Closed += (_, _) => _updatePromptWindow = null;
        _updatePromptWindow.Show();
        _updatePromptWindow.Activate();
    }

    private async Task ApplyUpdateAsync(string assetUrl, bool urgent = false)
    {
        if (_updateApplying) return;
        _updateApplying = true;

        _updateCts = new System.Threading.CancellationTokenSource();
        try
        {
            SelfUpdater.Log("user accepted the update");
            var window = _updatePromptWindow;
            var progress = new Progress<(long Read, long Total)>(p => window?.ReportProgress(p.Read, p.Total));
            await SelfUpdater.DownloadAndRestartAsync(assetUrl, progress, urgent, stage => window?.SetStage(stage), _updateCts.Token);
        }
        catch (OperationCanceledException)
        {
            _updatePromptWindow?.ShowAvailable();
        }
        catch (Exception ex)
        {
            SelfUpdater.Log($"apply failed: {ex}");
            _updatePromptWindow?.ShowFailure(ex);
            _trayIcon?.ShowBalloonTip(8000, "Currency Island", "Could not install the update. Download the latest version manually from the release page.", Forms.ToolTipIcon.Warning);
        }
        finally
        {
            _updateApplying = false;
            _updateCts?.Dispose();
            _updateCts = null;
        }
    }

    private void OpenSettings()
    {
        if (_settingsWindow != null)
        {
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow(_window!);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
    }

    private bool AcquireSingleInstance(bool afterUpdate)
    {
        _instanceMutex = new System.Threading.Mutex(false, @"Local\CurrencyIsland.SingleInstance");
        try
        {
            return _instanceMutex.WaitOne(afterUpdate ? TimeSpan.FromSeconds(20) : TimeSpan.Zero);
        }
        catch (System.Threading.AbandonedMutexException)
        {
            return true;
        }
    }

    private void OnUserPreferenceChanged(object? sender, Microsoft.Win32.UserPreferenceChangedEventArgs e)
    {
        if (e.Category != Microsoft.Win32.UserPreferenceCategory.General) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_trayIcon != null) _trayIcon.Icon = BuildTrayIcon();
        }));
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        if (_trayIcon != null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }
        try { _instanceMutex?.ReleaseMutex(); } catch { }
        base.OnExit(e);
    }

    private static bool IsTaskbarLight()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is int value && value == 1;
        }
        catch
        {
            return false;
        }
    }

    private static Drawing.Icon BuildTrayIcon()
    {
        var size = Math.Max(16, Forms.SystemInformation.SmallIconSize.Width);
        var light = IsTaskbarLight();
        var red = light ? 20 / 255f : 1f;
        var green = light ? 24 / 255f : 1f;
        var blue = light ? 26 / 255f : 1f;

        using var stream = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("logo.png")!;
        using var memory = new System.IO.MemoryStream();
        stream.CopyTo(memory);
        memory.Position = 0;
        using var source = new Drawing.Bitmap(memory);

        const int scale = 4;
        var big = size * scale;
        var bold = (size <= 16 ? 0.30f : size <= 20 ? 0.25f : size <= 24 ? 0.20f : 0.15f) * scale;
        using var tinted = new Drawing.Bitmap(big, big, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Drawing.Graphics.FromImage(tinted))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            var matrix = new System.Drawing.Imaging.ColorMatrix(new[]
            {
                new float[] { 0, 0, 0, 0, 0 },
                new float[] { 0, 0, 0, 0, 0 },
                new float[] { 0, 0, 0, 0, 0 },
                new float[] { 0, 0, 0, 1, 0 },
                new[] { red, green, blue, 0, 1 }
            });
            using var attributes = new System.Drawing.Imaging.ImageAttributes();
            attributes.SetColorMatrix(matrix);
            var pad = big / 64f;
            var box = big - pad * 2;
            for (var i = 0; i < 8; i++)
            {
                var angle = i / 8.0 * Math.PI * 2;
                var rect = new Drawing.RectangleF(pad + (float)Math.Cos(angle) * bold, pad + (float)Math.Sin(angle) * bold, box, box);
                g.DrawImage(source, Drawing.Rectangle.Round(rect), 0, 0, source.Width, source.Height, Drawing.GraphicsUnit.Pixel, attributes);
            }
            g.DrawImage(source, Drawing.Rectangle.Round(new Drawing.RectangleF(pad, pad, box, box)), 0, 0, source.Width, source.Height, Drawing.GraphicsUnit.Pixel, attributes);
        }

        using var small = new Drawing.Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var g = Drawing.Graphics.FromImage(small))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            g.DrawImage(tinted, 0, 0, size, size);
        }

        var hIcon = small.GetHicon();
        return Drawing.Icon.FromHandle(hIcon);
    }
}
