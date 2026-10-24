using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using LockTune.Core;
using LockTune.Platform;

namespace LockTune.UI;

/// "LockTune Settings" (SettingsView.swift): 580 × 640, see-through blurred
/// window tinted by the artwork, the glass tab bar with the liquid droplet,
/// and grouped forms — Status / General / Look / Layout / Lyrics.
public sealed class SettingsWindow : Window
{
    private enum Tab { Status, General, Look, Layout, Lyrics }
    private static readonly (Tab Tab, string Title, string Icon)[] Tabs =
    {
        (Tab.Status, "Status", "checkmark.circle"),
        (Tab.General, "General", "gearshape"),
        (Tab.Look, "Look", "paintpalette"),
        (Tab.Layout, "Layout", "rectangle.3.offgrid"),
        (Tab.Lyrics, "Lyrics", "quote.bubble"),
    };
    private const double TabWidth = 96, BarHeight = 32;

    private readonly AppCoordinator app;
    private readonly AppSettings settings;
    private readonly List<Action> refreshers = new();
    private readonly ContentControl page = new();
    private readonly ScrollViewer scroll = new();
    private readonly Rectangle tint = new() { IsHitTestVisible = false };
    private readonly DispatcherTimer statusTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly Dictionary<Tab, FrameworkElement> pages = new();
    private Tab selected = Tab.Status;
    private bool backdrop;

    // Tab bar
    private readonly Grid tabBar = new() { Width = TabWidth * 5, Height = BarHeight };
    private readonly Border selection = new() { Width = TabWidth, Height = BarHeight, CornerRadius = new CornerRadius(BarHeight / 2), HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TranslateTransform selectionOffset = new();
    private readonly StackPanel labels = new() { Orientation = Orientation.Horizontal, IsHitTestVisible = false };
    private readonly Canvas dropletLayer = new() { IsHitTestVisible = false, ClipToBounds = false };
    private readonly Border droplet = new();
    private readonly ScaleTransform dropletStretch = new(1, 1);
    private readonly Rectangle magnified = new();
    private double? dragX;
    private double stretch;
    private (double X, DateTime Time)? lastSample;

    public SettingsWindow(AppCoordinator app)
    {
        this.app = app;
        settings = app.Settings;
        Title = "LockTune Settings";
        SizeToContent = SizeToContent.WidthAndHeight;
        ResizeMode = ResizeMode.CanMinimize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = Glass.SystemText;
        FontSize = 13;
        Foreground = (Brush)FindResource("Text");
        Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/LockTune.ico"));
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

        // See-through, blurred window (acrylic on Windows 11) + a glow tinted by the artwork.
        backdrop = Environment.OSVersion.Version.Build >= 22621 && !settings.LiteMode;
        Background = backdrop ? Brushes.Transparent : new SolidColorBrush(Color.FromRgb(31, 31, 31));
        SourceInitialized += (_, _) => ApplyBackdrop();

        var root = new Grid { Width = 580, Height = 640 };
        root.Children.Add(tint);
        var column = new DockPanel();
        var header = new Grid { Margin = new Thickness(0, 12, 0, 6) };
        header.Children.Add(BuildTabBar());
        var close = new PressableButton { Width = 26, Height = 26, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 14, 0), ToolTip = "Close (Ctrl+W)" };
        var closeGlass = new Border { CornerRadius = new CornerRadius(13), Child = new Symbol("xmark", 11, 1.4) };
        Glass.Apply(closeGlass, GlassLevel.Normal, settings.LiteMode);
        close.Child = closeGlass;
        close.Click += Close;
        header.Children.Add(close);
        DockPanel.SetDock(header, Dock.Top);
        column.Children.Add(header);
        scroll.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        scroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        scroll.Content = page;
        scroll.Padding = new Thickness(20, 4, 20, 20);
        column.Children.Add(scroll);
        root.Children.Add(column);
        Content = root;

        settings.PropertyChanged += OnSettingsChanged;
        app.StatusChanged += OnStatusChanged;
        app.Media.ArtworkChanged += UpdateTint;
        statusTimer.Tick += (_, _) => { if (selected == Tab.Status) Refresh(); };
        statusTimer.Start();
        Closed += (_, _) =>
        {
            settings.PropertyChanged -= OnSettingsChanged;
            app.StatusChanged -= OnStatusChanged;
            app.Media.ArtworkChanged -= UpdateTint;
            statusTimer.Stop();
        };
        PreviewKeyDown += OnKey;
        UpdateTint();
        Select(Tab.Status, animated: false);
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e) => Dispatcher.BeginInvoke(Refresh);
    private void OnStatusChanged() => Dispatcher.BeginInvoke(Refresh);

    private void Refresh()
    {
        foreach (var r in refreshers) r();
    }

    private void UpdateTint()
    {
        var p = app.Media.HasTrack ? app.Media.Palette : Media.ArtworkPalette.Fallback;
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
        brush.GradientStops.Add(new GradientStop(Media.ArtworkPalette.WithAlpha(p.Primary, 0.45), 0));
        brush.GradientStops.Add(new GradientStop(Media.ArtworkPalette.WithAlpha(p.Secondary, 0.25), 0.5));
        brush.GradientStops.Add(new GradientStop(Media.ArtworkPalette.WithAlpha(p.Tertiary, 0.35), 1));
        tint.Fill = brush;
    }

    private void ApplyBackdrop()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        Native.SetAttribute(hwnd, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, 1);
        Native.SetAttribute(hwnd, Native.DWMWA_WINDOW_CORNER_PREFERENCE, 2);
        if (!backdrop) return;
        if (HwndSource.FromHwnd(hwnd) is { CompositionTarget: { } target }) target.BackgroundColor = Colors.Transparent;
        var margins = new Native.MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        Native.DwmExtendFrameIntoClientArea(hwnd, ref margins);
        Native.SetAttribute(hwnd, Native.DWMWA_SYSTEMBACKDROP_TYPE, 3); // acrylic, like the Mac's HUD blur
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (recordingHotkey) return;
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        if (e.Key == Key.Escape || ctrl && e.Key == Key.W) { Close(); e.Handled = true; }
        else if (ctrl && e.Key >= Key.D1 && e.Key <= Key.D5) { Select((Tab)(e.Key - Key.D1), true); e.Handled = true; }
    }

    // MARK: Tab bar — tap a tab, or press and drag: a liquid-glass droplet
    // pops out, magnifies the tabs under it, stretches with your speed and
    // drops into the tab you release on.

    private FrameworkElement BuildTabBar()
    {
        selection.Background = new SolidColorBrush(Color.FromArgb(46, 255, 255, 255));
        selection.BorderBrush = new SolidColorBrush(Color.FromArgb(89, 255, 255, 255));
        selection.BorderThickness = new Thickness(0.8);
        selection.RenderTransform = selectionOffset;
        tabBar.Children.Add(selection);

        foreach (var (_, title, icon) in Tabs)
        {
            var item = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            item.Children.Add(new Symbol(icon, 12, 1.2) { Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
            item.Children.Add(new TextBlock { Text = title, FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
            labels.Children.Add(new Grid { Width = TabWidth, Height = BarHeight, Children = { item } });
        }
        tabBar.Children.Add(labels);

        // The droplet lens.
        double lensWidth = TabWidth * 1.12, lensHeight = BarHeight * 1.45;
        var lens = new Grid { Width = lensWidth, Height = lensHeight, ClipToBounds = true };
        lens.Clip = new RectangleGeometry(new Rect(0, 0, lensWidth, lensHeight), lensHeight / 2, lensHeight / 2);
        lens.Children.Add(new Rectangle { Fill = new SolidColorBrush(Color.FromArgb(26, 255, 255, 255)) });
        magnified.Width = TabWidth * 5;
        magnified.Height = BarHeight;
        magnified.Fill = new VisualBrush(labels) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top };
        magnified.HorizontalAlignment = HorizontalAlignment.Left;
        magnified.VerticalAlignment = VerticalAlignment.Top;
        lens.Children.Add(magnified);
        var highlight = new Border
        {
            Width = lensWidth * 0.62, Height = lensHeight * 0.28, CornerRadius = new CornerRadius(lensHeight * 0.14),
            Background = new LinearGradientBrush(Color.FromArgb(115, 255, 255, 255), Colors.Transparent, 90),
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, lensHeight * 0.06, 0, 0), Effect = new BlurEffect { Radius = 3 },
        };
        lens.Children.Add(highlight);
        var rim = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
        rim.GradientStops.Add(new GradientStop(Color.FromArgb(217, 255, 255, 255), 0));
        rim.GradientStops.Add(new GradientStop(Color.FromArgb(38, 255, 255, 255), 0.5));
        rim.GradientStops.Add(new GradientStop(Color.FromArgb(128, 255, 255, 255), 1));
        droplet.Width = lensWidth;
        droplet.Height = lensHeight;
        droplet.CornerRadius = new CornerRadius(lensHeight / 2);
        droplet.BorderBrush = rim;
        droplet.BorderThickness = new Thickness(1.2);
        droplet.Background = new SolidColorBrush(Color.FromArgb(settings.LiteMode ? (byte)40 : (byte)20, 255, 255, 255));
        droplet.Effect = Glass.Shadow(0.3, 10, 5);
        droplet.Child = lens;
        droplet.RenderTransformOrigin = new Point(0.5, 0.5);
        droplet.RenderTransform = dropletStretch;
        droplet.Visibility = Visibility.Collapsed;
        dropletLayer.Children.Add(droplet);
        tabBar.Children.Add(dropletLayer);

        var bar = new Border
        {
            CornerRadius = new CornerRadius((BarHeight + 8) / 2), Padding = new Thickness(4), Child = tabBar,
            HorizontalAlignment = HorizontalAlignment.Center, Cursor = Cursors.Hand,
        };
        Glass.Apply(bar, GlassLevel.Normal, settings.LiteMode);
        tabBar.Background = Brushes.Transparent;
        tabBar.MouseLeftButtonDown += (_, e) => { tabBar.CaptureMouse(); dragStart = e.GetPosition(tabBar).X; };
        tabBar.MouseMove += (_, e) => { if (tabBar.IsMouseCaptured) Drag(e.GetPosition(tabBar).X); };
        tabBar.MouseLeftButtonUp += (_, e) =>
        {
            if (!tabBar.IsMouseCaptured) return;
            tabBar.ReleaseMouseCapture();
            int index = (int)Math.Clamp(e.GetPosition(tabBar).X / TabWidth, 0, Tabs.Length - 1);
            EndDrag();
            Select(Tabs[index].Tab, true);
        };
        return bar;
    }

    private double dragStart;

    private void Drag(double x)
    {
        // Taps don't pop the droplet; real drags do.
        if (dragX == null && Math.Abs(x - dragStart) <= 3) return;
        var now = DateTime.UtcNow;
        if (lastSample is { } last)
        {
            double dt = Math.Max((now - last.Time).TotalSeconds, 1.0 / 240);
            double speed = Math.Abs(x - last.X) / dt;
            stretch = stretch * 0.6 + Math.Min(speed / 1400, 1) * 0.4;
        }
        lastSample = (x, now);
        if (dragX == null)
        {
            droplet.Visibility = Visibility.Visible;
            droplet.Opacity = 0;
            Glass.Animate(droplet, OpacityProperty, 1, 0.18);
            Glass.Animate(selection, OpacityProperty, 0, 0.15);
        }
        dragX = x;
        double total = TabWidth * Tabs.Length;
        double center = Math.Clamp(x, TabWidth / 2, total - TabWidth / 2);
        double lensWidth = droplet.Width, lensHeight = droplet.Height;
        Canvas.SetLeft(droplet, center - lensWidth / 2);
        Canvas.SetTop(droplet, (BarHeight - lensHeight) / 2);
        // Magnify the labels around the droplet centre.
        const double m = 1.28;
        magnified.RenderTransform = new MatrixTransform(m, 0, 0, m, lensWidth / 2 - center * m, lensHeight / 2 - BarHeight / 2 * m);
        dropletStretch.ScaleX = 1 + stretch * 0.28;
        dropletStretch.ScaleY = 1 - stretch * 0.14;
        Highlight((int)Math.Clamp(x / TabWidth, 0, Tabs.Length - 1));
    }

    private void EndDrag()
    {
        lastSample = null;
        stretch = 0;
        if (dragX == null) return;
        dragX = null;
        Glass.Animate(dropletStretch, ScaleTransform.ScaleXProperty, 1, 0.3);
        Glass.Animate(dropletStretch, ScaleTransform.ScaleYProperty, 1, 0.3);
        var fade = new DoubleAnimation(0, TimeSpan.FromSeconds(0.2));
        fade.Completed += (_, _) => { if (dragX == null) droplet.Visibility = Visibility.Collapsed; };
        droplet.BeginAnimation(OpacityProperty, fade);
        Glass.Animate(selection, OpacityProperty, 1, 0.15);
    }

    private void Highlight(int lit)
    {
        for (int i = 0; i < labels.Children.Count; i++)
            ((UIElement)labels.Children[i]).Opacity = i == lit ? 1 : 0.65;
    }

    /// For the self-test screenshots.
    internal void SelectTab(int index) => Select((Tab)index, animated: false);

    private void Select(Tab tab, bool animated)
    {
        selected = tab;
        int index = (int)tab;
        Highlight(index);
        double x = TabWidth * index;
        if (animated && !settings.LiteMode)
            selectionOffset.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(x, TimeSpan.FromSeconds(0.42)) { EasingFunction = new BackEase { Amplitude = 0.3, EasingMode = EasingMode.EaseOut } });
        else
        {
            selectionOffset.BeginAnimation(TranslateTransform.XProperty, null);
            selectionOffset.X = x;
        }
        if (!pages.TryGetValue(tab, out var content))
        {
            content = tab switch
            {
                Tab.Status => StatusPage(),
                Tab.General => GeneralPage(),
                Tab.Look => LookPage(),
                Tab.Layout => LayoutPage(),
                _ => LyricsPage(),
            };
            pages[tab] = content;
        }
        page.Content = content;
        scroll.ScrollToTop();
        Refresh();
        if (animated && !settings.LiteMode)
        {
            content.Opacity = 0;
            Glass.Animate(content, OpacityProperty, 1, 0.25);
        }
    }

    // MARK: Status

    private FrameworkElement StatusPage()
    {
        var p = new StackPanel();
        var intro = new Grid();
        intro.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        intro.ColumnDefinitions.Add(new ColumnDefinition());
        intro.Children.Add(new Image { Source = new BitmapImage(new Uri("pack://application:,,,/Assets/LockTune.png")), Width = 56, Height = 56, Margin = new Thickness(0, 4, 14, 4), VerticalAlignment = VerticalAlignment.Center });
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(new TextBlock { Text = "LockTune is running", FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        texts.Children.Add(Secondary("Play music in Spotify or Apple Music, then lock your PC (Ctrl+Alt+L). The player widget appears on your lock screen, with lyrics on the left toolbar.", 12.5));
        Grid.SetColumn(texts, 1);
        intro.Children.Add(texts);
        p.Children.Add(Section(null, intro));

        p.Children.Add(Section("Look", GlassPicker(), LiteModeToggle()));

        p.Children.Add(Section("Checklist",
            StatusRow(() => app.Media.HasTrack, () => "Music detected",
                      () => app.Media.Track is { } t ? $"{t.Title} — {t.Artist} · {app.Media.SourceName}" : "Start a song in Spotify or Apple Music."),
            StatusRow(() => app.Media.SessionsAvailable, () => $"Access to Windows media sessions: {(app.Media.SessionsAvailable ? "Allowed" : "Unavailable")}",
                      () => app.Media.SessionsAvailable ? "OK. Spotify, Apple Music and browsers need no extra permission." : app.Media.Status),
            StatusRow(() => true, () => "Showing on the lock screen",
                      () => "Ready. Lock with the shortcut while music plays. Windows doesn't let apps draw on its own lock screen, so LockTune shows its lock screen first; leaving it locks Windows (PIN / password)."),
            StatusRow(() => !app.HotkeyUnavailable || !settings.HotkeyEnabled, () => "Lock Screen Now",
                      () => app.HotkeyUnavailable && settings.HotkeyEnabled ? $"{settings.Hotkey} is used by another app — pick a different shortcut in General." : $"{(settings.HotkeyEnabled ? settings.Hotkey + " or " : "")}Lock Screen Now in the menu. Win+L always locks Windows directly.")));

        var diagnostics = new StackPanel();
        refreshers.Add(() =>
        {
            diagnostics.Children.Clear();
            foreach (var (name, text) in app.Media.Diagnostics())
                diagnostics.Children.Add(LabeledRow(name, new TextBlock
                {
                    Text = text, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 11, TextAlignment = TextAlignment.Right,
                    TextWrapping = TextWrapping.Wrap, MaxWidth = 380, Foreground = (Brush)FindResource("SubText"),
                }));
        });
        p.Children.Add(Section("Diagnostics", diagnostics,
            Buttons(("Refresh", () => app.Media.Refresh(), false), ("Copy Diagnostics", CopyDiagnostics, false))));

        p.Children.Add(Section("Try it",
            Buttons(("Preview Widget", () => app.TogglePreview(), true), ("Lock Screen Now", () => app.LockNow(), false)),
            Caption("The LockTune icon (♪ with a house) can be hidden under the ^ arrow in the taskbar corner. Open LockTune again from the Start menu to get back to this window.")));
        return p;
    }

    private void CopyDiagnostics()
    {
        var lines = new List<string>
        {
            $"LockTune {AppCoordinator.VersionString}",
            $"Windows {Environment.OSVersion.VersionString}",
            $"lock screen up: {app.LockScreen.IsVisible}",
            $"track: {(app.Media.Track is { } t ? $"{t.Title} — {t.Artist}" : "none")}",
        };
        lines.AddRange(app.Media.Diagnostics().Select(d => $"{d.Name}: {d.Text.Replace("\n", " · ")}"));
        try { Clipboard.SetText(string.Join("\n", lines)); } catch { }
    }

    // MARK: General

    private FrameworkElement GeneralPage()
    {
        var p = new StackPanel();
        p.Children.Add(Section(null, Toggle("Launch at login", () => LaunchAtLogin.IsEnabled, v => LaunchAtLogin.IsEnabled = v)));
        p.Children.Add(Section("Look", GlassPicker(), LiteModeToggle()));
        p.Children.Add(Section("Lyrics", LyricsHighlightPicker()));

        var updateButtons = new StackPanel { Orientation = Orientation.Horizontal };
        var check = MacButton("Check for Updates", false);
        check.Click += (_, _) => app.Updater.CheckNow(userInitiated: true);
        var install = MacButton("Install Update", true);
        install.Click += (_, _) => app.InstallUpdate();
        updateButtons.Children.Add(check);
        updateButtons.Children.Add(install);
        var updateStatus = Secondary("", 11);
        updateStatus.VerticalAlignment = VerticalAlignment.Center;
        var updateRow = new DockPanel { Margin = RowPadding };
        DockPanel.SetDock(updateButtons, Dock.Left);
        updateRow.Children.Add(updateButtons);
        updateStatus.TextAlignment = TextAlignment.Right;
        updateRow.Children.Add(updateStatus);
        refreshers.Add(() =>
        {
            bool available = app.Updater.UpdateAvailable;
            check.Visibility = available ? Visibility.Collapsed : Visibility.Visible;
            install.Visibility = available ? Visibility.Visible : Visibility.Collapsed;
            install.Content = $"Install Update (build {app.Updater.AvailableBuild})";
            updateStatus.Text = app.Updater.StatusText;
        });
        p.Children.Add(Section("Updates",
            LabeledRow("Installed version", Secondary(AppCoordinator.VersionString, 13)),
            Toggle("Check for updates automatically", () => settings.AutoCheckUpdates, v => settings.AutoCheckUpdates = v),
            updateRow));

        p.Children.Add(Section("Lock screen",
            Toggle("Show the music widget when you lock the PC", () => settings.LockWidgetEnabled, v => settings.LockWidgetEnabled = v),
            SliderBlock(() => $"Screen stays on after locking: {AppSettings.AwakeTitle(settings.AwakeDuration)}",
                        0, AppSettings.AwakeDurations.Length - 1, 1, () => settings.AwakeDurationIndex, v => settings.AwakeDurationIndex = (int)Math.Round(v),
                        TextLabel("Default"), TextLabel("Always"),
                        "Only while music is playing. With “Windows default” the display turns off as usual."),
            Toggle("Lock after being idle while music plays", () => settings.IdleEnabled, v => settings.IdleEnabled = v),
            Stepper(() => $"Idle time: {settings.IdleMinutes} min", () => settings.IdleMinutes, v => settings.IdleMinutes = v, 1, 120, () => settings.IdleEnabled),
            Toggle("Show on all displays", () => settings.AllDisplays, v => settings.AllDisplays = v)));

        var unavailable = new TextBlock { Text = "This shortcut is already used by another app. Pick a different one.", FontSize = 11, Foreground = Brushes.Orange, TextWrapping = TextWrapping.Wrap, Margin = RowPadding };
        refreshers.Add(() => unavailable.Visibility = app.HotkeyUnavailable && settings.HotkeyEnabled ? Visibility.Visible : Visibility.Collapsed);
        p.Children.Add(Section("Keyboard shortcut",
            Toggle("Global shortcut", () => settings.HotkeyEnabled, v => settings.HotkeyEnabled = v),
            LabeledRow("Lock screen now", HotkeyRecorder()),
            unavailable,
            Caption("The shortcut shows the LockTune lock screen; leaving it locks Windows (PIN / password). Win+L always locks Windows directly.")));

        p.Children.Add(Section("Music source",
            Popup("Player", Enum.GetValues<PreferredSource>(), Titles.Of, () => settings.PreferredSource, v => settings.PreferredSource = v)));
        return p;
    }

    // MARK: Look

    private FrameworkElement LookPage()
    {
        var p = new StackPanel();
        p.Children.Add(Section("Background",
            Toggle("Artwork wallpaper behind the widget", () => settings.LockWallpaper, v => settings.LockWallpaper = v),
            Popup("Wallpaper style", Enum.GetValues<BackgroundStyle>(), Titles.Of, () => settings.BackgroundStyle, v => settings.BackgroundStyle = v, () => settings.LockWallpaper),
            Caption("On: the screen is filled with the artwork. Off: only the album art, controls and lyrics float over your normal wallpaper.")));
        p.Children.Add(Section("Liquid Glass", GlassPicker()));

        var preview = new ClockView { Margin = new Thickness(0, 6, 0, 6) };
        refreshers.Add(() => { preview.Apply(56, Glass.Family(settings.ClockFont), Glass.Weight(settings.ClockWeight)); preview.Refresh(); });
        p.Children.Add(Section("Clock (wallpaper mode & preview)",
            Popup("Font", Enum.GetValues<FontChoice>(), Titles.Of, () => settings.ClockFont, v => settings.ClockFont = v),
            Popup("Weight", Enum.GetValues<ClockWeight>(), Titles.Of, () => settings.ClockWeight, v => settings.ClockWeight = v),
            preview));

        var reset = MacButton("Reset Position", false);
        reset.Click += (_, _) => app.Turntable?.ResetPosition();
        refreshers.Add(() => reset.IsEnabled = settings.TurntableEnabled);
        p.Children.Add(Section("Desktop turntable",
            Toggle("Show a turntable on the desktop", () => settings.TurntableEnabled, v => settings.TurntableEnabled = v),
            SliderBlock(() => $"Size: {Math.Round(settings.TurntableScale * 100)} %", 0.6, 1.5, 0.05, () => settings.TurntableScale, v => settings.TurntableScale = v,
                        new Symbol("opticaldisc", 10), new Symbol("opticaldisc", 16), null, () => settings.TurntableEnabled),
            Wrap(reset),
            Caption("Sits on your wallpaper, under all app windows. Drag it anywhere; click the record to play/pause.")));

        p.Children.Add(Section("Animation",
            Segmented("Smoothness", AppSettings.FpsOptions, f => $"{f} fps", () => settings.AnimationFPS, v => settings.AnimationFPS = v),
            Caption("Frame rate of the moving background, lyrics and progress bar. 120 fps needs a 120 Hz display.")));

        p.Children.Add(Section("Presets",
            Buttons(("Export Preset…", ExportPreset, false), ("Import Preset…", ImportPreset, false)),
            Caption("Save your whole look (layout, sizes, fonts, lyrics, glass, clock) to a file you can share, or load someone else's. Presets work on the Mac and on Windows.")));
        return p;
    }

    // MARK: Layout

    /// Knob position along the slider, 0–100 % (50 % = default).
    private static int Percent(double value, (double Min, double Max) r) => (int)Math.Round((value - r.Min) / (r.Max - r.Min) * 100);

    private FrameworkElement HeightSlider(Func<double> inset, Action<double> setInset)
    {
        var r = AppSettings.PlayerBottomRange;
        // Mirrored so that left = lower, right = higher.
        double Knob() => r.Min + r.Max - (1 - inset());
        void SetKnob(double k) => setInset(1 - (r.Min + r.Max - k));
        return SliderBlock(() => $"Player height (artwork + controls): {Percent(Knob(), r)} %", r.Min, r.Max, 0, Knob, SetKnob,
                           TextLabel("Lower"), TextLabel("Higher"));
    }

    private FrameworkElement SizeSlider(Func<double> get, Action<double> set) =>
        SliderBlock(() => $"Artwork size: {Math.Round(get() * 100)} %", 0.6, AppSettings.MaxArtworkScale, 0.05, get, set,
                    new Symbol("photo", 10), new Symbol("photo", 16));

    private FrameworkElement LyricsOffsetSlider(Func<double> get, Action<double> set, (double Min, double Max) r) =>
        SliderBlock(() => $"Lyrics position: {Percent(r.Min + r.Max - get(), r)} %", r.Min, r.Max, 0,
                    () => r.Min + r.Max - get(), k => set(r.Min + r.Max - k), TextLabel("Lower"), TextLabel("Higher"),
                    "50 % is the default. Too high can touch the clock, too low your name.");

    private FrameworkElement ModeHeader(string title, Func<bool> inUse)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new TextBlock { Text = title, FontSize = 13, FontWeight = FontWeights.Bold, VerticalAlignment = VerticalAlignment.Center });
        var badge = new Border
        {
            CornerRadius = new CornerRadius(8), Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(Color.FromArgb(64, 10, 132, 255)),
            Child = new TextBlock { Text = "IN USE", FontSize = 9, FontWeight = FontWeights.Bold },
        };
        refreshers.Add(() => badge.Visibility = inUse() ? Visibility.Visible : Visibility.Collapsed);
        row.Children.Add(badge);
        return row;
    }

    private FrameworkElement LayoutPage()
    {
        var p = new StackPanel();
        p.Children.Add(Section(null, Caption("Each mode has its own sliders. Left = less / lower, right = more / higher; 50 % is the default.")));
        p.Children.Add(Section(ModeHeader("With artwork wallpaper", () => settings.LockWallpaper),
            HeightSlider(() => settings.WidgetBottomInset, v => settings.WidgetBottomInset = v),
            SizeSlider(() => settings.ArtworkScale, v => settings.ArtworkScale = v),
            LyricsOffsetSlider(() => settings.LyricsOffset, v => settings.LyricsOffset = v, AppSettings.WallpaperLyricsRange)));

        var warning = new StackPanel { Orientation = Orientation.Horizontal, Margin = RowPadding };
        warning.Children.Add(new Symbol("exclamationmark.triangle", 11) { Foreground = Brushes.Orange, Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center });
        warning.Children.Add(new TextBlock { Text = "A big change here can make the album cover reach up into the clock.", FontSize = 11, Foreground = Brushes.Orange, TextWrapping = TextWrapping.Wrap });
        p.Children.Add(Section(ModeHeader("Without wallpaper (normal lock screen)", () => !settings.LockWallpaper),
            HeightSlider(() => settings.PlainBottomInset, v => settings.PlainBottomInset = v),
            SizeSlider(() => settings.PlainArtworkScale, v => settings.PlainArtworkScale = v),
            warning,
            LyricsOffsetSlider(() => settings.PlainLyricsOffset, v => settings.PlainLyricsOffset = v, AppSettings.PlainLyricsRange),
            Caption("Below 50 % the player can reach your name and “Press Enter to unlock”.")));
        p.Children.Add(Section(null, Buttons(("Reset Layout to Defaults", settings.ResetLayout, false))));
        return p;
    }

    // MARK: Lyrics

    private FrameworkElement LyricsPage()
    {
        var p = new StackPanel();
        p.Children.Add(Section(null,
            Toggle("Load lyrics from LRCLIB", () => settings.LyricsEnabled, v => settings.LyricsEnabled = v),
            Popup("Where", Enum.GetValues<LyricsPosition>(), Titles.Of, () => settings.LyricsPosition, v => settings.LyricsPosition = v),
            LyricsHighlightPicker()));

        var sample = new StackPanel { Margin = RowPadding };
        var big = new TextBlock { Text = "Here is the place", FontSize = 26 };
        var small = new TextBlock { Text = "Come together in the dream", FontSize = 20, Opacity = 0.45, Margin = new Thickness(0, 6, 0, 0) };
        sample.Children.Add(big);
        sample.Children.Add(small);
        refreshers.Add(() =>
        {
            big.FontFamily = small.FontFamily = Glass.Family(settings.LyricsFont);
            big.FontWeight = small.FontWeight = Glass.Weight(settings.LyricsWeight);
        });
        p.Children.Add(Section("Font",
            Popup("Font", Enum.GetValues<FontChoice>(), Titles.Of, () => settings.LyricsFont, v => settings.LyricsFont = v),
            Popup("Weight", Enum.GetValues<ClockWeight>(), Titles.Of, () => settings.LyricsWeight, v => settings.LyricsWeight = v),
            sample,
            SliderBlock(() => $"Text size: {Math.Round(settings.LyricsScale * 100)} %", 0.6, 1.4, 0.05, () => settings.LyricsScale, v => settings.LyricsScale = v,
                        new Symbol("textformat.size.smaller", 11), new Symbol("textformat.size.larger", 11), "Smaller text shows more lines at once.")));

        p.Children.Add(Section("Timing",
            SliderBlock(() => $"Lyrics timing: {settings.LyricsLead:0.0} s ahead", AppSettings.LyricsLeadRange.Min, AppSettings.LyricsLeadRange.Max, 0.1,
                        () => settings.LyricsLead, v => settings.LyricsLead = v, TextLabel("On time"), TextLabel("Earlier"),
                        "Middle (1.5 s) is the default. If lyrics feel late, move right towards Earlier; if they run ahead, move left."),
            Buttons(("Clear Lyrics Cache", app.Lyrics.ClearCache, false), ("Reset Per-Song Sync", app.Lyrics.ResetAllSongOffsets, false)),
            Caption("On the lock screen, with lyrics on, the − / + buttons on the left shift the current song's lyrics by 0.25 s; that correction is remembered for that song.")));
        return p;
    }

    // MARK: Shared pieces (GlassPicker, LiteModeToggle, LyricsHighlightPicker)

    private FrameworkElement GlassPicker()
    {
        var detail = Caption("");
        refreshers.Add(() => detail.Text = settings.GlassLevel switch
        {
            GlassLevel.Off => "Classic frosted blur.",
            GlassLevel.Normal => "Glass player card and toolbar.",
            _ => "⚠️ Experimental — doesn't work very well yet and can look off, especially without the artwork wallpaper. Glass everywhere: player card, every button, a frame around the artwork and a panel behind the lyrics.",
        });
        var stack = new StackPanel();
        stack.Children.Add(Segmented("Liquid Glass", Enum.GetValues<GlassLevel>(), Titles.Of, () => settings.GlassLevel, v => settings.GlassLevel = v, () => !settings.LiteMode));
        stack.Children.Add(detail);
        return stack;
    }

    private FrameworkElement LiteModeToggle()
    {
        var stack = new StackPanel();
        stack.Children.Add(Toggle("Lite mode (for older / slower PCs)", () => settings.LiteMode, v => settings.LiteMode = v));
        stack.Children.Add(Caption("Everything keeps working, but heavy effects are switched off: no moving background, no blur or glass, no word-by-word highlight, 30 fps and slower polling. Made for older laptops."));
        return stack;
    }

    private FrameworkElement LyricsHighlightPicker()
    {
        var detail = Caption("");
        refreshers.Add(() => detail.Text = settings.WordByWord
            ? "Words light up one after another, like Apple Music."
            : "The whole current line lights up at once.");
        var stack = new StackPanel();
        stack.Children.Add(Segmented("Lyrics highlight", new[] { true, false }, v => v ? "Word by word" : "Whole line", () => settings.WordByWord, v => settings.WordByWord = v));
        stack.Children.Add(detail);
        return stack;
    }

    // MARK: Form building blocks (macOS grouped Form)

    private static readonly Thickness RowPadding = new(10, 7, 10, 7);

    private FrameworkElement Section(object? header, params FrameworkElement[] rows)
    {
        var stack = new StackPanel { Margin = new Thickness(0, 10, 0, 6) };
        if (header is string title)
            stack.Children.Add(new TextBlock { Text = title, FontSize = 13, FontWeight = FontWeights.Bold, Margin = new Thickness(10, 0, 0, 6) });
        else if (header is FrameworkElement element)
        {
            element.Margin = new Thickness(10, 0, 0, 6);
            stack.Children.Add(element);
        }
        var card = new Border
        {
            CornerRadius = new CornerRadius(10), Background = (Brush)FindResource("Card"),
            BorderBrush = (Brush)FindResource("CardBorder"), BorderThickness = new Thickness(0.5), Padding = new Thickness(0, 3, 0, 3),
        };
        var inner = new StackPanel();
        for (int i = 0; i < rows.Length; i++)
        {
            if (i > 0)
            {
                var divider = new Border { Height = 0.5, Background = new SolidColorBrush(Color.FromArgb(28, 255, 255, 255)), Margin = new Thickness(10, 0, 10, 0) };
                var row = rows[i];
                refreshers.Add(() => divider.Visibility = row.Visibility);
                inner.Children.Add(divider);
            }
            inner.Children.Add(rows[i]);
        }
        card.Child = inner;
        stack.Children.Add(card);
        return stack;
    }

    private static Grid Row(FrameworkElement label, FrameworkElement control)
    {
        var grid = new Grid { Margin = RowPadding, MinHeight = 24 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        label.VerticalAlignment = VerticalAlignment.Center;
        label.Margin = new Thickness(0, 0, 12, 0);
        grid.Children.Add(label);
        control.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        return grid;
    }

    private static TextBlock Label(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap };

    private TextBlock Secondary(string text, double size) => new()
    {
        Text = text, FontSize = size, Foreground = (Brush)FindResource("SubText"), TextWrapping = TextWrapping.Wrap,
    };

    private TextBlock Caption(string text)
    {
        var t = Secondary(text, 11);
        t.Margin = new Thickness(10, 2, 10, 7);
        return t;
    }

    private static TextBlock TextLabel(string text) => new() { Text = text, FontSize = 11, Opacity = 0.8 };

    private FrameworkElement Wrap(FrameworkElement element)
    {
        element.HorizontalAlignment = HorizontalAlignment.Left;
        return new Border { Padding = RowPadding, Child = element };
    }

    private FrameworkElement LabeledRow(string title, FrameworkElement content) => Row(Label(title), content);

    private FrameworkElement Toggle(string title, Func<bool> get, Action<bool> set)
    {
        var box = new CheckBox { Style = (Style)FindResource("MacSwitch") };
        bool updating = false;
        box.Checked += (_, _) => { if (!updating) set(true); };
        box.Unchecked += (_, _) => { if (!updating) set(false); };
        refreshers.Add(() => { updating = true; box.IsChecked = get(); updating = false; });
        return Row(Label(title), box);
    }

    private Button MacButton(string title, bool isDefault)
    {
        var b = new Button { Content = title, Style = (Style)FindResource(isDefault ? "MacDefaultButton" : "MacButton"), Margin = new Thickness(0, 0, 8, 0) };
        return b;
    }

    private FrameworkElement Buttons(params (string Title, Action Action, bool IsDefault)[] buttons)
    {
        var panel = new WrapPanel { Margin = RowPadding };
        foreach (var (title, action, isDefault) in buttons)
        {
            var b = MacButton(title, isDefault);
            b.Click += (_, _) => { action(); Refresh(); };
            panel.Children.Add(b);
        }
        return panel;
    }

    /// Menu-style picker: the current value + ⌃⌄, a menu of choices.
    private FrameworkElement Popup<T>(string title, IEnumerable<T> options, Func<T, string> name, Func<T> get, Action<T> set, Func<bool>? enabled = null) where T : notnull
    {
        var text = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(text);
        content.Children.Add(new Symbol("chevron.up.chevron.down", 9, 1.3) { Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
        var button = new Button { Style = (Style)FindResource("MacPopup"), Content = content };
        var list = options.ToList();
        button.Click += (_, _) =>
        {
            var menu = new ContextMenu { Style = (Style)FindResource("MacMenu"), PlacementTarget = button, Placement = PlacementMode.Bottom };
            var current = get();
            foreach (var option in list)
            {
                var item = new MenuItem { Header = name(option), IsCheckable = true, IsChecked = EqualityComparer<T>.Default.Equals(option, current), Style = (Style)FindResource("MacMenuItem") };
                var value = option;
                item.Click += (_, _) => { set(value); Refresh(); };
                menu.Items.Add(item);
            }
            menu.IsOpen = true;
        };
        var row = Row(Label(title), button);
        refreshers.Add(() =>
        {
            text.Text = name(get());
            bool on = enabled?.Invoke() ?? true;
            button.IsEnabled = on;
            row.Opacity = on ? 1 : 0.5;
        });
        return row;
    }

    private FrameworkElement Segmented<T>(string title, IEnumerable<T> options, Func<T, string> name, Func<T> get, Action<T> set, Func<bool>? enabled = null) where T : notnull
    {
        var group = new StackPanel { Orientation = Orientation.Horizontal };
        var border = new Border
        {
            CornerRadius = new CornerRadius(7), Background = new SolidColorBrush(Color.FromArgb(28, 255, 255, 255)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(20, 255, 255, 255)), BorderThickness = new Thickness(0.5), Child = group, Padding = new Thickness(1),
        };
        string groupName = "g" + Guid.NewGuid().ToString("N");
        var buttons = new List<(RadioButton Button, T Value)>();
        bool updating = false;
        foreach (var option in options)
        {
            var value = option;
            var rb = new RadioButton { Content = name(option), GroupName = groupName, Style = (Style)FindResource("MacSegment") };
            rb.Checked += (_, _) => { if (!updating) set(value); };
            buttons.Add((rb, option));
            group.Children.Add(rb);
        }
        var row = Row(Label(title), border);
        refreshers.Add(() =>
        {
            updating = true;
            var current = get();
            foreach (var (b, v) in buttons) b.IsChecked = EqualityComparer<T>.Default.Equals(v, current);
            updating = false;
            bool on = enabled?.Invoke() ?? true;
            border.IsEnabled = on;
            border.Opacity = on ? 1 : 0.5;
        });
        return row;
    }

    /// Title (with the value) above a slider with minimum / maximum labels, then an optional caption.
    private FrameworkElement SliderBlock(Func<string> title, double min, double max, double step, Func<double> get, Action<double> set,
                                         FrameworkElement minLabel, FrameworkElement maxLabel, string? caption = null, Func<bool>? enabled = null)
    {
        var header = Label("");
        var slider = new Slider { Minimum = min, Maximum = max, Style = (Style)FindResource("MacSlider") };
        if (step > 0) { slider.TickFrequency = step; slider.IsSnapToTickEnabled = true; }
        bool updating = false;
        slider.ValueChanged += (_, e) =>
        {
            if (updating) return;
            set(e.NewValue);
            header.Text = title();
        };
        var line = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.ColumnDefinitions.Add(new ColumnDefinition());
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        minLabel.VerticalAlignment = maxLabel.VerticalAlignment = VerticalAlignment.Center;
        minLabel.Margin = new Thickness(0, 0, 8, 0);
        maxLabel.Margin = new Thickness(8, 0, 0, 0);
        line.Children.Add(minLabel);
        Grid.SetColumn(slider, 1);
        line.Children.Add(slider);
        Grid.SetColumn(maxLabel, 2);
        line.Children.Add(maxLabel);
        var stack = new StackPanel { Margin = RowPadding };
        stack.Children.Add(header);
        stack.Children.Add(line);
        if (caption != null)
        {
            var c = Secondary(caption, 11);
            c.Margin = new Thickness(0, 4, 0, 0);
            stack.Children.Add(c);
        }
        refreshers.Add(() =>
        {
            updating = true;
            slider.Value = get();
            updating = false;
            header.Text = title();
            bool on = enabled?.Invoke() ?? true;
            stack.IsEnabled = on;
            stack.Opacity = on ? 1 : 0.5;
        });
        return stack;
    }

    /// "Idle time: 5 min" with a −/+ stepper.
    private FrameworkElement Stepper(Func<string> title, Func<int> get, Action<int> set, int min, int max, Func<bool>? enabled = null)
    {
        var label = Label("");
        var stepper = new Border
        {
            CornerRadius = new CornerRadius(6), Background = new SolidColorBrush(Color.FromArgb(36, 255, 255, 255)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(20, 255, 255, 255)), BorderThickness = new Thickness(0.5),
        };
        var pair = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (symbol, delta) in new[] { ("minus", -1), ("plus", 1) })
        {
            var b = new PressableButton { Width = 24, Height = 22, Child = new Symbol(symbol, 9, 1.4) };
            b.Click += () => { set(Math.Clamp(get() + delta, min, max)); Refresh(); };
            pair.Children.Add(b);
        }
        stepper.Child = pair;
        var row = Row(label, stepper);
        refreshers.Add(() =>
        {
            label.Text = title();
            bool on = enabled?.Invoke() ?? true;
            row.IsEnabled = on;
            row.Opacity = on ? 1 : 0.5;
        });
        return row;
    }

    private FrameworkElement StatusRow(Func<bool> ok, Func<string> title, Func<string> detail)
    {
        var icon = new Symbol("checkmark.circle.fill", 16) { VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 10, 0) };
        var titleText = new TextBlock { FontWeight = FontWeights.Medium, TextWrapping = TextWrapping.Wrap };
        var detailText = Secondary("", 11);
        var texts = new StackPanel();
        texts.Children.Add(titleText);
        texts.Children.Add(detailText);
        var grid = new Grid { Margin = RowPadding };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.Children.Add(icon);
        Grid.SetColumn(texts, 1);
        grid.Children.Add(texts);
        refreshers.Add(() =>
        {
            bool good = ok();
            icon.SymbolName = good ? "checkmark.circle.fill" : "exclamationmark.circle.fill";
            icon.Foreground = good ? new SolidColorBrush(Color.FromRgb(50, 215, 75)) : Brushes.Orange;
            titleText.Text = title();
            detailText.Text = detail();
        });
        return grid;
    }

    // MARK: Shortcut recorder

    private bool recordingHotkey;

    /// Click, then press a shortcut (must include Ctrl, Alt or Win). Esc cancels.
    private FrameworkElement HotkeyRecorder()
    {
        var button = new Button { Style = (Style)FindResource("MacButton"), MinWidth = 110, FontFamily = Glass.Rounded, FontWeight = FontWeights.Medium };
        refreshers.Add(() =>
        {
            if (!recordingHotkey) button.Content = settings.Hotkey.ToString();
            button.IsEnabled = settings.HotkeyEnabled;
        });
        button.Click += (_, _) =>
        {
            if (recordingHotkey) { StopRecording(); return; }
            recordingHotkey = true;
            button.Content = "Press shortcut…";
            app.SuspendHotkey();
        };
        void StopRecording()
        {
            recordingHotkey = false;
            app.ResumeHotkey();
            Refresh();
        }
        PreviewKeyDown += (_, e) =>
        {
            if (!recordingHotkey) return;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
                return;
            e.Handled = true;
            if (key == Key.Escape) { StopRecording(); return; }
            uint mods = 0;
            var m = Keyboard.Modifiers;
            if ((m & ModifierKeys.Control) != 0) mods |= Native.MOD_CONTROL;
            if ((m & ModifierKeys.Alt) != 0) mods |= Native.MOD_ALT;
            if ((m & ModifierKeys.Shift) != 0) mods |= Native.MOD_SHIFT;
            if ((m & ModifierKeys.Windows) != 0) mods |= Native.MOD_WIN;
            if ((mods & (Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_WIN)) == 0)
            {
                System.Media.SystemSounds.Beep.Play();
                return;
            }
            settings.Hotkey = new Hotkey(mods, (uint)KeyInterop.VirtualKeyFromKey(key));
            StopRecording();
        };
        return button;
    }

    // MARK: Presets

    private void ExportPreset()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Title = "Export LockTune Preset", FileName = "LockTune Preset.json", Filter = "LockTune preset (*.json)|*.json" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, settings.ExportPreset(System.IO.Path.GetFileNameWithoutExtension(dialog.FileName)));
        }
        catch (Exception e)
        {
            MessageBox.Show(this, e.Message, "Couldn't export the preset", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ImportPreset()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "Import LockTune Preset", Filter = "LockTune preset (*.json)|*.json|All files|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        string? name = null;
        bool ok;
        try { ok = settings.ImportPreset(File.ReadAllText(dialog.FileName), out name); }
        catch (Exception) { ok = false; }
        if (ok)
            MessageBox.Show(this, $"“{name ?? System.IO.Path.GetFileNameWithoutExtension(dialog.FileName)}” is now active.", "Preset imported", MessageBoxButton.OK, MessageBoxImage.Information);
        else
            MessageBox.Show(this, "This file isn't a LockTune preset.", "Couldn't import the preset", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
