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
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LockTune.Core;
using LockTune.Platform;

namespace LockTune.UI;

/// Settings: Status / General / Look / Layout / Lyrics, on a dark glass
/// window (Acrylic on Windows 11) with a sliding "droplet" tab bar.
public sealed class SettingsWindow : Window
{
    private static readonly string[] Tabs = { "Status", "General", "Look", "Layout", "Lyrics" };

    private readonly AppCoordinator app;
    private readonly AppSettings settings;
    private readonly List<Action> refreshers = new();
    private readonly ContentControl page = new();
    private readonly Grid tabBar = new() { Height = 38 };
    private readonly Border droplet = new() { CornerRadius = new CornerRadius(15), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(4) };
    private readonly TranslateTransform dropletOffset = new();
    private readonly ScaleTransform dropletScale = new(1, 1);
    private readonly List<TextBlock> tabLabels = new();
    private readonly DispatcherTimer statusTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Dictionary<int, FrameworkElement> pages = new();
    private int selected;
    private bool draggingTabs;
    private double dragStartX;
    private bool backdrop;

    public SettingsWindow(AppCoordinator app)
    {
        this.app = app;
        settings = app.Settings;
        Title = "LockTune Settings";
        Width = 760;
        Height = 700;
        MinWidth = 620;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        FontSize = 13.5;
        Foreground = (Brush)FindResource("Text");
        Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/LockTune.ico"));
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

        // Windows 11 22H2+: real acrylic behind a transparent window. Older: dark gradient.
        backdrop = Environment.OSVersion.Version.Build >= 22621 && !settings.LiteMode;
        Background = backdrop ? Brushes.Transparent : new LinearGradientBrush(Color.FromRgb(32, 33, 44), Color.FromRgb(18, 18, 24), 90);
        SourceInitialized += (_, _) => ApplyBackdrop();

        var root = new DockPanel { LastChildFill = true };
        var header = BuildHeader();
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = page,
            Padding = new Thickness(22, 6, 22, 22),
        };
        root.Children.Add(scroll);
        Content = root;

        settings.PropertyChanged += OnSettingsChanged;
        app.StatusChanged += Refresh;
        statusTimer.Tick += (_, _) => { if (selected == 0) Refresh(); };
        statusTimer.Start();
        Closed += (_, _) =>
        {
            settings.PropertyChanged -= OnSettingsChanged;
            app.StatusChanged -= Refresh;
            statusTimer.Stop();
        };
        PreviewKeyDown += OnKey;
        Select(0, animated: false);
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e) => Dispatcher.BeginInvoke(Refresh);

    private void Refresh()
    {
        foreach (var r in refreshers) r();
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
        Native.SetAttribute(hwnd, Native.DWMWA_SYSTEMBACKDROP_TYPE, 3); // acrylic
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        if (e.Key == Key.Escape || ctrl && e.Key == Key.W) { Close(); e.Handled = true; }
        else if (ctrl && e.Key >= Key.D1 && e.Key <= Key.D5) { Select(e.Key - Key.D1, true); e.Handled = true; }
        else if (ctrl && e.Key == Key.Tab) { Select((selected + 1) % Tabs.Length, true); e.Handled = true; }
    }

    // MARK: Header + tab bar

    private FrameworkElement BuildHeader()
    {
        var stack = new StackPanel { Margin = new Thickness(22, 16, 22, 10) };
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(2, 0, 0, 14) };
        titleRow.Children.Add(new Image
        {
            Source = new BitmapImage(new Uri("pack://application:,,,/Assets/LockTune.png")), Width = 34, Height = 34,
            Margin = new Thickness(0, 0, 12, 0),
        });
        var titles = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titles.Children.Add(new TextBlock { Text = "LockTune", FontSize = 20, FontWeight = FontWeights.Bold });
        titles.Children.Add(new TextBlock
        {
            Text = "Your music on a beautiful lock screen", FontSize = 12, Foreground = (Brush)FindResource("SubText"),
        });
        titleRow.Children.Add(titles);
        stack.Children.Add(titleRow);

        var bar = new Border
        {
            CornerRadius = new CornerRadius(19), Background = new SolidColorBrush(Color.FromArgb(28, 255, 255, 255)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), BorderThickness = new Thickness(1),
            Child = tabBar,
        };
        for (int i = 0; i < Tabs.Length; i++) tabBar.ColumnDefinitions.Add(new ColumnDefinition());

        var rim = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
        rim.GradientStops.Add(new GradientStop(Color.FromArgb(200, 255, 255, 255), 0));
        rim.GradientStops.Add(new GradientStop(Color.FromArgb(30, 255, 255, 255), 0.5));
        rim.GradientStops.Add(new GradientStop(Color.FromArgb(120, 255, 255, 255), 1));
        droplet.Background = new LinearGradientBrush(Color.FromArgb(90, 255, 255, 255), Color.FromArgb(40, 255, 255, 255), 90);
        droplet.BorderBrush = rim;
        droplet.BorderThickness = new Thickness(1);
        var group = new TransformGroup();
        group.Children.Add(dropletScale);
        group.Children.Add(dropletOffset);
        droplet.RenderTransform = group;
        droplet.RenderTransformOrigin = new Point(0.5, 0.5);
        Grid.SetColumnSpan(droplet, Tabs.Length);
        tabBar.Children.Add(droplet);

        for (int i = 0; i < Tabs.Length; i++)
        {
            var label = new TextBlock
            {
                Text = Tabs[i], HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                FontWeight = FontWeights.SemiBold, IsHitTestVisible = false,
            };
            Grid.SetColumn(label, i);
            tabBar.Children.Add(label);
            tabLabels.Add(label);
        }
        tabBar.Background = Brushes.Transparent;
        tabBar.Cursor = Cursors.Hand;
        tabBar.SizeChanged += (_, _) => PositionDroplet(false);
        tabBar.MouseLeftButtonDown += (_, e) =>
        {
            draggingTabs = false;
            dragStartX = e.GetPosition(tabBar).X;
            tabBar.CaptureMouse();
        };
        tabBar.MouseMove += (_, e) =>
        {
            if (!tabBar.IsMouseCaptured) return;
            double x = e.GetPosition(tabBar).X;
            if (!draggingTabs && Math.Abs(x - dragStartX) > 4)
            {
                draggingTabs = true;
                // The droplet swells while you drag it, like a drop of water.
                Glass.Animate(dropletScale, ScaleTransform.ScaleXProperty, 1.18, 0.2);
                Glass.Animate(dropletScale, ScaleTransform.ScaleYProperty, 1.12, 0.2);
            }
            if (draggingTabs)
            {
                double w = TabWidth;
                dropletOffset.BeginAnimation(TranslateTransform.XProperty, null);
                dropletOffset.X = Math.Clamp(x - w / 2, 0, tabBar.ActualWidth - w);
                Highlight((int)Math.Clamp(x / w, 0, Tabs.Length - 1));
            }
        };
        tabBar.MouseLeftButtonUp += (_, e) =>
        {
            if (!tabBar.IsMouseCaptured) return;
            tabBar.ReleaseMouseCapture();
            int index = (int)Math.Clamp(e.GetPosition(tabBar).X / TabWidth, 0, Tabs.Length - 1);
            draggingTabs = false;
            Select(index, true);
        };
        stack.Children.Add(bar);
        return stack;
    }

    private double TabWidth => Math.Max(1, tabBar.ActualWidth / Tabs.Length);

    private void Highlight(int index)
    {
        for (int i = 0; i < tabLabels.Count; i++) tabLabels[i].Opacity = i == index ? 1 : 0.62;
    }

    private void PositionDroplet(bool animated)
    {
        double w = TabWidth;
        droplet.Width = Math.Max(1, w - 8);
        droplet.Height = 30;
        double x = selected * w;
        if (animated && !settings.LiteMode)
        {
            dropletOffset.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(x, TimeSpan.FromSeconds(0.42)) { EasingFunction = new BackEase { Amplitude = 0.35, EasingMode = EasingMode.EaseOut } });
            // Stretch on the way, then settle back into a round pill.
            var stretch = new DoubleAnimationUsingKeyFrames();
            stretch.KeyFrames.Add(new EasingDoubleKeyFrame(1.22, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.15))));
            stretch.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.45)),
                                                           new ElasticEase { Oscillations = 1, Springiness = 6, EasingMode = EasingMode.EaseOut }));
            dropletScale.BeginAnimation(ScaleTransform.ScaleXProperty, stretch);
            var squash = new DoubleAnimationUsingKeyFrames();
            squash.KeyFrames.Add(new EasingDoubleKeyFrame(0.9, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.15))));
            squash.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.45))));
            dropletScale.BeginAnimation(ScaleTransform.ScaleYProperty, squash);
        }
        else
        {
            dropletOffset.BeginAnimation(TranslateTransform.XProperty, null);
            dropletOffset.X = x;
            dropletScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            dropletScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            dropletScale.ScaleX = dropletScale.ScaleY = 1;
        }
    }

    public void Select(int index, bool animated)
    {
        selected = Math.Clamp(index, 0, Tabs.Length - 1);
        Highlight(selected);
        PositionDroplet(animated);
        if (!pages.TryGetValue(selected, out var content))
        {
            content = selected switch
            {
                0 => StatusPage(),
                1 => GeneralPage(),
                2 => LookPage(),
                3 => LayoutPage(),
                _ => LyricsPage(),
            };
            pages[selected] = content;
        }
        page.Content = content;
        Refresh();
        if (animated && !settings.LiteMode)
        {
            content.Opacity = 0;
            Glass.Animate(content, OpacityProperty, 1, 0.25);
        }
    }

    // MARK: Pages

    private FrameworkElement StatusPage()
    {
        var p = new StackPanel();
        p.Children.Add(Section("Now playing",
            Info("Media", () => app.Media.Track is { } t ? $"{t.Title} — {t.Artist}" : "Nothing playing"),
            Info("Source", () => app.Media.SourceName is { } s ? $"{s}  ({app.Media.Status})" : app.Media.Status),
            Info("Lyrics", () => app.Lyrics.State switch
            {
                Lyrics.LyricsState.Loaded => app.Lyrics.SyncedLines != null ? $"Synced · {app.Lyrics.SyncedLines.Count} lines" : "Found (not synced)",
                Lyrics.LyricsState.Loading => "Loading…",
                Lyrics.LyricsState.NotFound => "Not found for this song",
                Lyrics.LyricsState.Failed => "Couldn't load (offline?)",
                _ => settings.LyricsEnabled ? "Waiting for a song" : "Turned off",
            })));
        p.Children.Add(Section("Lock screen",
            Info("Shortcut", () => !settings.HotkeyEnabled ? "Off"
                                   : app.HotkeyAvailable ? settings.Hotkey + "  ✓" : settings.Hotkey + "  — used by another app, pick another one"),
            Info("Leaving it", () => settings.RequirePassword ? "Locks Windows (PIN / password) ✓" : "Just closes (no password)"),
            Info("Windows lock screen", () => settings.WindowsLockImage ? (app.LockImageStatus ?? "Album cover will be shown") : "Not changed"),
            Buttons(("Show preview", () => app.ShowPreview(), false), ("Lock now", () => app.LockNow(), true))));
        p.Children.Add(Section("About",
            Info("Version", () => $"{AppCoordinator.VersionString}"),
            Info("Updates", () => app.Updater.StatusText),
            Buttons(("Check for updates", () => app.Updater.CheckNow(userInitiated: true), false),
                    ("Open on GitHub", () => AppCoordinator.OpenUrl("https://github.com/KubaCh7/LockTune"), false))));
        p.Children.Add(Note("Windows doesn't let any app draw on its own lock screen, so LockTune shows its own lock screen first. " +
                            "With \"Require Windows password\" on, leaving it (or anything that tries to get around it) locks Windows for real."));
        return p;
    }

    private FrameworkElement GeneralPage()
    {
        var p = new StackPanel();
        p.Children.Add(Section("Opening the lock screen",
            Toggle("Keyboard shortcut", "Shows the LockTune lock screen", () => settings.HotkeyEnabled, v => settings.HotkeyEnabled = v),
            HotkeyRow(),
            Toggle("Lock when idle while music plays", null, () => settings.IdleEnabled, v => settings.IdleEnabled = v),
            SliderRow("Idle time", 1, 30, () => settings.IdleMinutes, v => settings.IdleMinutes = (int)Math.Round(v),
                      v => $"{Math.Round(v)} min", step: 1, enabled: () => settings.IdleEnabled)));
        p.Children.Add(Section("Security & display",
            Toggle("Require Windows password", "Leaving LockTune locks Windows (recommended)", () => settings.RequirePassword, v => settings.RequirePassword = v),
            Choice("Keep the display on", AppSettings.AwakeChoices.Select(m => (m, AppSettings.AwakeTitle(m))).ToArray(),
                   () => settings.AwakeMinutes, v => settings.AwakeMinutes = v),
            Toggle("Album cover on the Windows lock screen", "Experimental · replaces your Windows lock screen picture",
                   () => settings.WindowsLockImage, v => settings.WindowsLockImage = v)));
        p.Children.Add(Section("Music",
            Choice("Music source", new[] { (PreferredSource.Auto, "Automatic"), (PreferredSource.Spotify, "Spotify"), (PreferredSource.AppleMusic, "Apple Music") },
                   () => settings.PreferredSource, v => settings.PreferredSource = v)));
        p.Children.Add(Section("App",
            Toggle("Launch at login", null, () => LaunchAtLogin.IsEnabled, v => LaunchAtLogin.IsEnabled = v),
            Toggle("Check for updates automatically", null, () => settings.AutoUpdate, v => settings.AutoUpdate = v)));
        p.Children.Add(Section("Desktop turntable",
            Toggle("Show turntable on the desktop", "Sits on your wallpaper, below all apps · drag to move", () => settings.TurntableEnabled, v => settings.TurntableEnabled = v),
            SliderRow("Size", 0.6, 1.8, () => settings.TurntableScale, v => settings.TurntableScale = v, v => $"{v * 100:0}%",
                      enabled: () => settings.TurntableEnabled),
            Buttons(("Reset position", () => app.Turntable?.ResetPosition(), false))));
        return p;
    }

    private FrameworkElement LookPage()
    {
        var p = new StackPanel();
        p.Children.Add(Section("Background",
            Choice("Style", new[] { (BackgroundStyle.Blurred, "Blurred artwork"), (BackgroundStyle.Artwork, "Artwork"), (BackgroundStyle.Wallpaper, "Windows wallpaper") },
                   () => settings.BackgroundStyle, v => settings.BackgroundStyle = v),
            Choice("Liquid Glass", new[] { (GlassLevel.Off, "Off"), (GlassLevel.Normal, "Normal"), (GlassLevel.Mega, "Mega (experimental)") },
                   () => settings.GlassLevel, v => settings.GlassLevel = v)));
        p.Children.Add(Section("Clock",
            Stepper("Clock font", FontChoices.All, () => settings.ClockFont, v => settings.ClockFont = v, preview: true),
            Stepper("Clock weight", FontChoices.Weights, () => settings.ClockWeight, v => settings.ClockWeight = v)));
        p.Children.Add(Section("Lyrics text",
            Stepper("Lyrics font", FontChoices.All, () => settings.LyricsFont, v => settings.LyricsFont = v, preview: true),
            Stepper("Lyrics weight", FontChoices.Weights, () => settings.LyricsWeight, v => settings.LyricsWeight = v)));
        p.Children.Add(Section("Performance",
            Toggle("Lite mode", "For older or slower PCs: no animated background, blur or shadows", () => settings.LiteMode, v => settings.LiteMode = v)));
        return p;
    }

    private FrameworkElement LayoutPage()
    {
        var p = new StackPanel();
        p.Children.Add(Section("Player",
            SliderRow("Artwork size", 0.5, 2.5, () => settings.ArtworkScale, v => settings.ArtworkScale = v, v => $"{v * 100:0}%"),
            SliderRow("Player height", 0, 1, () => settings.PlayerHeight, v => settings.PlayerHeight = v, v => $"{v * 100:0}%")));
        p.Children.Add(Section("Lyrics",
            Choice("Lyrics position", new[] { (LyricsPosition.Left, "Left"), (LyricsPosition.Center, "Center"), (LyricsPosition.Right, "Right") },
                   () => settings.LyricsPosition, v => settings.LyricsPosition = v),
            SliderRow("Lyrics height", 0, 1, () => 1 - settings.LyricsOffset, v => settings.LyricsOffset = 1 - v, v => $"{v * 100:0}%"),
            SliderRow("Lyrics size", 0.6, 1.6, () => settings.LyricsScale, v => settings.LyricsScale = v, v => $"{v * 100:0}%")));
        p.Children.Add(Section("Presets",
            Buttons(("Preview", () => app.ShowPreview(), false), ("Reset layout", settings.ResetLayout, false)),
            Buttons(("Export preset…", ExportPreset, false), ("Import preset…", ImportPreset, false))));
        p.Children.Add(Note("Sliders: left = smaller / lower, right = bigger / higher. A very big artwork can reach the clock."));
        return p;
    }

    private FrameworkElement LyricsPage()
    {
        var p = new StackPanel();
        p.Children.Add(Section("Lyrics",
            Toggle("Show lyrics", "Synced lyrics from LRCLIB (free, no account)", () => settings.LyricsEnabled, v => settings.LyricsEnabled = v),
            Toggle("Open with lyrics", "Show lyrics right away when a song has them", () => settings.OpenWithLyrics, v => settings.OpenWithLyrics = v),
            Toggle("Word-by-word highlight", "Like Apple Music (off in Lite mode)", () => settings.WordByWord, v => settings.WordByWord = v)));
        p.Children.Add(Section("Timing",
            SliderRow("Lyrics timing", 0, 3, () => settings.LyricsLead, v => settings.LyricsLead = v,
                      v => Math.Abs(v - 1.5) < 0.05 ? "default" : v < 1.5 ? $"{1.5 - v:0.0} s later" : $"{v - 1.5:0.0} s earlier", step: 0.1),
            Note("Out of sync for one song? Use − / + on the lock screen (or ↑ / ↓): it's remembered for that song."),
            Buttons(("Reset per-song timing", app.Lyrics.ResetAllSongOffsets, false), ("Clear lyrics cache", app.Lyrics.ClearCache, false))));
        return p;
    }

    // MARK: Building blocks

    private FrameworkElement Section(string title, params FrameworkElement[] rows)
    {
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = title.ToUpperInvariant(), FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("SubText"),
            Margin = new Thickness(6, 16, 0, 8),
        });
        var card = new Border
        {
            CornerRadius = new CornerRadius(16), Background = (Brush)FindResource("Card"), BorderBrush = (Brush)FindResource("CardBorder"),
            BorderThickness = new Thickness(1), Padding = new Thickness(16, 6, 16, 6),
        };
        var inner = new StackPanel();
        for (int i = 0; i < rows.Length; i++)
        {
            if (i > 0) inner.Children.Add(new Border { Height = 1, Background = new SolidColorBrush(Color.FromArgb(18, 255, 255, 255)), Margin = new Thickness(0, 2, 0, 2) });
            inner.Children.Add(rows[i]);
        }
        card.Child = inner;
        stack.Children.Add(card);
        return stack;
    }

    private static Grid Row(string title, string? subtitle, FrameworkElement control)
    {
        var grid = new Grid { Margin = new Thickness(0, 8, 0, 8), MinHeight = 30 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var labels = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) };
        labels.Children.Add(new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap });
        if (subtitle != null)
            labels.Children.Add(new TextBlock { Text = subtitle, FontSize = 11.5, Opacity = 0.6, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 0) });
        grid.Children.Add(labels);
        control.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        return grid;
    }

    private FrameworkElement Toggle(string title, string? subtitle, Func<bool> get, Action<bool> set)
    {
        var box = new CheckBox { Style = (Style)FindResource("Switch") };
        bool updating = false;
        box.Checked += (_, _) => { if (!updating) set(true); };
        box.Unchecked += (_, _) => { if (!updating) set(false); };
        refreshers.Add(() => { updating = true; box.IsChecked = get(); updating = false; });
        var row = Row(title, subtitle, box);
        row.Cursor = Cursors.Hand;
        row.Background = Brushes.Transparent;
        row.MouseLeftButtonUp += (_, e) => { if (!box.IsMouseOver) set(!get()); Refresh(); };
        return row;
    }

    private FrameworkElement SliderRow(string title, double min, double max, Func<double> get, Action<double> set,
                                       Func<double, string> format, double step = 0, Func<bool>? enabled = null)
    {
        var value = new TextBlock { MinWidth = 90, TextAlignment = TextAlignment.Right, Opacity = 0.75, FontWeight = FontWeights.SemiBold };
        var slider = new Slider { Minimum = min, Maximum = max, Style = (Style)FindResource("GlassSlider"), Margin = new Thickness(0, 4, 0, 0) };
        if (step > 0) { slider.TickFrequency = step; slider.IsSnapToTickEnabled = true; }
        bool updating = false;
        slider.ValueChanged += (_, e) =>
        {
            value.Text = format(e.NewValue);
            if (!updating) set(e.NewValue);
        };
        refreshers.Add(() =>
        {
            updating = true;
            slider.Value = get();
            value.Text = format(slider.Value);
            updating = false;
            bool on = enabled?.Invoke() ?? true;
            slider.IsEnabled = on;
            slider.Opacity = on ? 1 : 0.4;
        });
        var stack = new StackPanel { Margin = new Thickness(0, 8, 0, 8) };
        var head = new Grid();
        head.Children.Add(new TextBlock { Text = title });
        value.HorizontalAlignment = HorizontalAlignment.Right;
        head.Children.Add(value);
        stack.Children.Add(head);
        stack.Children.Add(slider);
        return stack;
    }

    private FrameworkElement Choice<T>(string title, (T Value, string Label)[] options, Func<T> get, Action<T> set) where T : notnull
    {
        var group = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        var border = new Border
        {
            CornerRadius = new CornerRadius(13), Background = new SolidColorBrush(Color.FromArgb(22, 255, 255, 255)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)), BorderThickness = new Thickness(1), Child = group,
        };
        string groupName = "g" + Guid.NewGuid().ToString("N");
        var buttons = new List<(RadioButton Button, T Value)>();
        bool updating = false;
        foreach (var (v, label) in options)
        {
            var rb = new RadioButton { Content = label, GroupName = groupName, Style = (Style)FindResource("Segment") };
            rb.Checked += (_, _) => { if (!updating) set(v); };
            buttons.Add((rb, v));
            group.Children.Add(rb);
        }
        refreshers.Add(() =>
        {
            updating = true;
            var current = get();
            foreach (var (b, v) in buttons) b.IsChecked = EqualityComparer<T>.Default.Equals(v, current);
            updating = false;
        });
        if (options.Length > 3)
        {
            var stack = new StackPanel { Margin = new Thickness(0, 8, 0, 8) };
            stack.Children.Add(new TextBlock { Text = title, Margin = new Thickness(0, 0, 0, 6) });
            group.HorizontalAlignment = HorizontalAlignment.Left;
            border.HorizontalAlignment = HorizontalAlignment.Left;
            stack.Children.Add(border);
            return stack;
        }
        return Row(title, null, border);
    }

    private FrameworkElement Stepper(string title, string[] options, Func<string> get, Action<string> set, bool preview = false)
    {
        var label = new TextBlock { MinWidth = 190, TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center, FontSize = 14 };
        void Step(int delta)
        {
            int i = Array.IndexOf(options, get());
            set(options[((i < 0 ? 0 : i) + delta + options.Length) % options.Length]);
        }
        var back = new Button { Content = "‹", Style = (Style)FindResource("PillButton"), Padding = new Thickness(12, 2, 12, 4), FontSize = 16 };
        var forward = new Button { Content = "›", Style = (Style)FindResource("PillButton"), Padding = new Thickness(12, 2, 12, 4), FontSize = 16 };
        back.Click += (_, _) => Step(-1);
        forward.Click += (_, _) => Step(1);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(back);
        row.Children.Add(label);
        row.Children.Add(forward);
        refreshers.Add(() =>
        {
            var v = get();
            label.Text = v;
            label.FontFamily = preview ? Glass.Family(v) : FontFamily;
        });
        return Row(title, null, row);
    }

    private FrameworkElement Buttons(params (string Title, Action Action, bool Accent)[] buttons)
    {
        var panel = new WrapPanel { Margin = new Thickness(0, 8, 0, 8) };
        foreach (var (title, action, accent) in buttons)
        {
            var b = new Button { Content = title, Style = (Style)FindResource(accent ? "AccentButton" : "PillButton"), Margin = new Thickness(0, 0, 8, 0) };
            b.Click += (_, _) => { action(); Refresh(); };
            panel.Children.Add(b);
        }
        return panel;
    }

    private FrameworkElement Info(string title, Func<string> value)
    {
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Right, Opacity = 0.8, MaxWidth = 420 };
        refreshers.Add(() => text.Text = value());
        return Row(title, null, text);
    }

    private FrameworkElement Note(string text) => new TextBlock
    {
        Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = (Brush)FindResource("SubText"),
        Margin = new Thickness(6, 10, 6, 4),
    };

    private FrameworkElement HotkeyRow()
    {
        var button = new Button { Style = (Style)FindResource("PillButton"), MinWidth = 140 };
        bool recording = false;
        refreshers.Add(() => { if (!recording) button.Content = settings.Hotkey.ToString(); button.IsEnabled = settings.HotkeyEnabled; });
        button.Click += (_, _) =>
        {
            recording = true;
            button.Content = "Press a shortcut…";
            app.SuspendHotkey();
        };
        PreviewKeyDown += (_, e) =>
        {
            if (!recording) return;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
                return;
            e.Handled = true;
            recording = false;
            if (key != Key.Escape)
            {
                uint mods = 0;
                if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) mods |= Native.MOD_CONTROL;
                if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0) mods |= Native.MOD_ALT;
                if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) mods |= Native.MOD_SHIFT;
                if ((Keyboard.Modifiers & ModifierKeys.Windows) != 0) mods |= Native.MOD_WIN;
                if (mods != 0) settings.Hotkey = new Hotkey(mods, (uint)KeyInterop.VirtualKeyFromKey(key));
            }
            app.ResumeHotkey();
            Refresh();
        };
        return Row("Shortcut", "Click, then press e.g. Ctrl+Alt+L", button);
    }

    // MARK: Presets

    private void ExportPreset()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { FileName = "LockTune preset.locktune", Filter = "LockTune preset|*.locktune;*.json" };
        if (dialog.ShowDialog(this) != true) return;
        File.WriteAllText(dialog.FileName, settings.ExportPreset());
    }

    private void ImportPreset()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "LockTune preset|*.locktune;*.json|All files|*.*" };
        if (dialog.ShowDialog(this) != true) return;
        if (!settings.ImportPreset(File.ReadAllText(dialog.FileName)))
            MessageBox.Show(this, "That file isn't a LockTune preset.", "LockTune", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
