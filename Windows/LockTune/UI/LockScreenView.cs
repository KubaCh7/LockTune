using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using LockTune.Core;
using LockTune.Lyrics;
using LockTune.Media;

namespace LockTune.UI;

public enum Presentation { Lock, Preview }

/// Date + big clock, iPhone lock-screen style (ClockView.swift).
public sealed class ClockView : Border
{
    private readonly TextBlock date = new() { HorizontalAlignment = HorizontalAlignment.Center, Opacity = 0.92 };
    private readonly TextBlock time = new() { HorizontalAlignment = HorizontalAlignment.Center };

    public ClockView()
    {
        HorizontalAlignment = HorizontalAlignment.Center;
        IsHitTestVisible = false;
        var stack = new StackPanel();
        stack.Children.Add(date);
        stack.Children.Add(time);
        Child = stack;
        Refresh();
    }

    public void Apply(double size, FontFamily family, FontWeight weight)
    {
        date.FontFamily = time.FontFamily = family;
        date.FontWeight = FontWeights.SemiBold;
        date.FontSize = size * 0.15;
        time.FontSize = size;
        time.FontWeight = weight;
        time.Margin = new Thickness(0, -size * 0.04 - size * 0.12, 0, 0);
    }

    public void Refresh()
    {
        var now = DateTime.Now;
        var culture = CultureInfo.CurrentCulture;
        // Like iOS: no AM/PM marker, no leading zero in 12-hour mode.
        bool twelveHour = culture.DateTimeFormat.ShortTimePattern.Contains('h');
        var t = now.ToString(twelveHour ? "h:mm" : "H:mm", culture);
        if (time.Text != t) time.Text = t;
        // "Mon Oct 19" (or "pon. 19 paź" — day before month where that's the custom).
        bool dayFirst = culture.DateTimeFormat.MonthDayPattern.TrimStart().StartsWith("d");
        var d = now.ToString(dayFirst ? "ddd d MMM" : "ddd MMM d", culture).Replace(",", "");
        if (date.Text != d) date.Text = d;
    }
}

/// The lock-screen widget (LockWidgetView.swift): artwork + glass controls,
/// lyrics on the right/left/centre when enabled, and a small toolbar on the
/// left. With "artwork wallpaper" on, the blurred artwork and a clock fill
/// the screen; otherwise it floats over your normal wallpaper, above your
/// name, like on the Mac lock screen.
public sealed class LockScreenView : Grid
{
    private readonly MediaController media;
    private readonly LyricsController lyrics;
    private readonly AppSettings settings;

    // Plain mode: your wallpaper + clock + name, like the normal lock screen.
    private readonly Grid plainBackdrop = new();
    private readonly Image wallpaperImage = new() { Stretch = Stretch.UniformToFill };
    private readonly ClockView systemClock = new();
    private readonly StackPanel account = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Cursor = Cursors.Hand };
    // Wallpaper mode.
    private readonly Grid artworkBackdrop = new();
    private readonly BackgroundView background = new();
    private readonly ScaleTransform backgroundScale = new(1, 1);
    private readonly ClockView clock = new();
    private readonly TextBlock hint = new() { FontSize = 12, FontWeight = FontWeights.Medium, Opacity = 0.6, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 18), Cursor = Cursors.Hand };

    // The widget.
    private readonly Canvas content = new();
    private readonly TranslateTransform contentOffset = new();
    private readonly ScaleTransform contentScale = new(1, 1);
    private readonly Border artworkFrame = new();
    private readonly Border artworkInner = new();
    private readonly ScalingShadow artworkShadow = new();
    /// Target artwork size of the current layout (Width is mid-animation).
    private double artSide = 300;
    private readonly Grid artworkClip = new();
    private readonly Border artworkStroke = new() { BorderThickness = new Thickness(0.5), IsHitTestVisible = false };
    private readonly Grid artworkImages = new();
    private readonly Border artworkPlaceholder = new();
    private readonly ScaleTransform artworkScale = new(1, 1);
    private readonly Symbol placeholderSymbol = new("music.note", 30) { Opacity = 0.55, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private CornerRadius artworkCorner = new(6);
    private readonly Grid controls = new();
    private readonly Border controlsGlass = new() { CornerRadius = new CornerRadius(24) };
    private readonly Image controlsShadow = new();
    private Size controlsShadowSize;
    private readonly DispatcherTimer controlsShadowTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    // Frame counter (press F on the lock screen).
    private readonly TextBlock fpsText = new() { FontSize = 12, FontFamily = new FontFamily("Cascadia Mono, Consolas"), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 12, 16, 0), Visibility = Visibility.Collapsed, IsHitTestVisible = false };
    private int frames;
    private DateTime fpsSince = DateTime.UtcNow;
    private readonly TextBlock titleText = new() { FontSize = 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock artistText = new() { FontSize = 13, Opacity = 0.65, TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly Grid progress = new() { Height = 14, Background = Brushes.Transparent, Cursor = Cursors.Hand };
    private readonly Border progressTrack = new() { Height = 5, CornerRadius = new CornerRadius(2.5), VerticalAlignment = VerticalAlignment.Center };
    private readonly Border progressFill = new() { Height = 5, CornerRadius = new CornerRadius(2.5), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock elapsedText = new() { FontSize = 11, FontWeight = FontWeights.Medium, Opacity = 0.6 };
    private readonly TextBlock remainingText = new() { FontSize = 11, FontWeight = FontWeights.Medium, Opacity = 0.6, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly Image sourceIcon = new() { Width = 18, Height = 18, Opacity = 0.85, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TransportButton previousButton = new("backward.fill", 20);
    private readonly TransportButton playButton = new("pause.fill", 28);
    private readonly TransportButton nextButton = new("forward.fill", 20);
    private readonly PressableButton lyricsCardButton = new() { Width = 28, Height = 28, ToolTip = "Lyrics (L)" };
    private readonly Symbol lyricsCardSymbol = new("quote.bubble", 15, 1.1);
    private readonly Border lyricsHost = new() { CornerRadius = new CornerRadius(30) };
    private readonly LyricsPanel lyricsPanel = new();

    // Left toolbar.
    private readonly StackPanel toolbar = new() { Margin = new Thickness(26, 34, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
    private readonly ToolbarButton lyricsButton = new("quote.bubble", "Lyrics");
    private readonly ToolbarButton laterButton = new("minus", "Lyrics later (this song)");
    private readonly Border offsetLabel = new() { CornerRadius = new CornerRadius(10), Padding = new Thickness(6, 3, 6, 3), HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock offsetText = new() { FontSize = 10, FontWeight = FontWeights.Bold };
    private readonly ToolbarButton earlierButton = new("plus", "Lyrics earlier (this song)");
    private readonly ToolbarButton positionButton = new("rectangle.righthalf.inset.filled", "Lyrics position");
    private readonly ToolbarButton wallpaperButton = new("photo", "Artwork wallpaper");
    private readonly ToolbarButton hideButton = new("eye", "Hide widget");
    private readonly ToolbarButton closeButton = new("xmark", "Close preview (Esc)");

    private readonly DispatcherTimer clockTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    /// Kept between appearances, so lyrics stay on if you turned them on.
    private bool showsLyrics;
    private bool collapsed;
    private bool scrubbing;
    private bool hoveringProgress;
    private double scrubFraction;
    private int shownSecond = -1;
    private double lastClock;
    private Presentation presentation = Presentation.Preview;
    private bool widgetHidden;

    public event Action? UnlockRequested;
    public event Action? CloseRequested;

    public Presentation Presentation
    {
        get => presentation;
        set { presentation = value; ApplyTheme(); Refresh(); }
    }

    /// Extra monitors without "Show on all displays": just the backdrop.
    public bool WidgetHidden
    {
        get => widgetHidden;
        set { widgetHidden = value; Refresh(); }
    }

    private bool DrawsBackground => settings.LockWallpaper || presentation == Presentation.Preview;
    private bool HasLyricsMode => showsLyrics && media.HasTrack;
    private ArtworkPalette Palette => media.HasTrack ? media.Palette : ArtworkPalette.Fallback;
    private bool IsLight => DrawsBackground && Palette.IsLight;
    private Color TextColor => DrawsBackground ? Palette.TextColor : Colors.White;

    public LockScreenView(MediaController media, LyricsController lyrics, AppSettings settings)
    {
        this.media = media;
        this.lyrics = lyrics;
        this.settings = settings;
        Background = Brushes.Transparent;
        ClipToBounds = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        TextElement.SetFontFamily(this, Glass.SystemText);

        // Plain backdrop.
        plainBackdrop.Children.Add(new Rectangle { Fill = Brushes.Black });
        RenderOptions.SetBitmapScalingMode(wallpaperImage, BitmapScalingMode.HighQuality);
        plainBackdrop.Children.Add(wallpaperImage);
        plainBackdrop.Children.Add(new Rectangle { Fill = new SolidColorBrush(Color.FromArgb(40, 0, 0, 0)) });
        systemClock.VerticalAlignment = VerticalAlignment.Top;
        TextElement.SetForeground(plainBackdrop, Brushes.White);
        plainBackdrop.Children.Add(systemClock);
        BuildAccount();
        plainBackdrop.Children.Add(account);
        Children.Add(plainBackdrop);

        // Artwork wallpaper.
        background.RenderTransformOrigin = new Point(0.5, 0.5);
        background.RenderTransform = backgroundScale;
        artworkBackdrop.Children.Add(background);
        clock.VerticalAlignment = VerticalAlignment.Top;
        artworkBackdrop.Children.Add(clock);
        artworkBackdrop.Background = Brushes.Transparent;
        artworkBackdrop.MouseLeftButtonUp += (_, _) => { if (presentation == Presentation.Preview) CloseRequested?.Invoke(); };
        Children.Add(artworkBackdrop);

        // Widget.
        BuildArtwork();
        BuildControls();
        lyricsHost.Child = lyricsPanel;
        lyricsHost.Opacity = 0;
        lyricsHost.IsHitTestVisible = false;
        lyricsPanel.SeekRequested += t => media.Seek(t);
        content.Children.Add(lyricsHost);
        content.Children.Add(artworkFrame);
        content.Children.Add(controls);
        var group = new TransformGroup();
        group.Children.Add(contentScale);
        group.Children.Add(contentOffset);
        content.RenderTransform = group;
        Children.Add(content);

        BuildToolbar();
        Children.Add(toolbar);
        hint.MouseLeftButtonUp += (_, e) => { Dismiss(); e.Handled = true; };
        Children.Add(hint);
        Children.Add(fpsText);

        clockTimer.Tick += (_, _) => { clock.Refresh(); systemClock.Refresh(); };
        SizeChanged += (_, _) => { Relayout(false); ApplyClockSizes(); };

        media.TrackChanged += () => { UpdateTrack(); Relayout(true); };
        media.ArtworkChanged += () => { ApplyTheme(); UpdateArtwork(true); };
        media.PlaybackChanged += OnPlaybackChanged;
        lyrics.PropertyChanged += (_, _) => { lyricsPanel.SetContent(lyrics.State, lyrics.Content, lyrics.Revision); Refresh(); };
        settings.PropertyChanged += OnSettingsChanged;

        lyricsPanel.SetContent(lyrics.State, lyrics.Content, lyrics.Revision);
        ApplyTheme();
        UpdateTrack();
    }

    // MARK: Building

    private void BuildAccount()
    {
        string name = Environment.UserName;
        var avatar = new Grid { Width = 72, Height = 72, HorizontalAlignment = HorizontalAlignment.Center };
        var picture = AccountPicture();
        if (picture != null)
            avatar.Children.Add(new Ellipse { Fill = new ImageBrush(picture) { Stretch = Stretch.UniformToFill } });
        else
        {
            avatar.Children.Add(new Ellipse { Fill = new LinearGradientBrush(Color.FromRgb(150, 150, 158), Color.FromRgb(100, 100, 108), 90) });
            avatar.Children.Add(new TextBlock
            {
                Text = string.Concat(name.Split(' ', '.', '_').Where(p => p.Length > 0).Take(2).Select(p => char.ToUpperInvariant(p[0]))),
                FontSize = 28, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, FontFamily = Glass.Rounded,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            });
        }
        account.Children.Add(avatar);
        account.Children.Add(new TextBlock
        {
            Text = name, FontSize = 15, FontWeight = FontWeights.SemiBold, Foreground = Brushes.White, FontFamily = Glass.SystemText,
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 6),
        });
        account.Children.Add(new TextBlock
        {
            Text = "Press Enter to unlock", FontSize = 13, Foreground = new SolidColorBrush(Color.FromArgb(170, 255, 255, 255)),
            FontFamily = Glass.SystemText, HorizontalAlignment = HorizontalAlignment.Center,
        });
        account.MouseLeftButtonUp += (_, e) => { Dismiss(); e.Handled = true; };
    }

    /// The Windows account picture, if there is one.
    private static BitmapSource? AccountPicture()
    {
        try
        {
            var sid = WindowsIdentity.GetCurrent().User?.Value;
            if (sid == null) return null;
            var folder = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDocuments), "..", "AccountPictures", sid);
            if (!Directory.Exists(folder)) return null;
            var file = Directory.GetFiles(folder).Where(f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                                                 .OrderByDescending(f => new FileInfo(f).Length).FirstOrDefault();
            if (file == null) return null;
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 144;
            image.UriSource = new Uri(file);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception) { return null; }
    }

    private void BuildArtwork()
    {
        artworkPlaceholder.Background = new LinearGradientBrush(Color.FromArgb(64, 255, 255, 255), Color.FromArgb(20, 255, 255, 255), 45);
        artworkPlaceholder.Child = placeholderSymbol;
        artworkClip.Children.Add(artworkPlaceholder);
        artworkClip.Children.Add(artworkImages);
        var stack = new Grid();
        stack.Children.Add(artworkClip);
        stack.Children.Add(artworkStroke);
        artworkStroke.BorderBrush = new SolidColorBrush(Color.FromArgb(26, 255, 255, 255));
        artworkInner.Child = stack;
        var frame = new Grid();
        frame.Children.Add(artworkShadow);
        frame.Children.Add(artworkInner);
        artworkFrame.Child = frame;
        artworkFrame.RenderTransformOrigin = new Point(0.5, 0.5);
        artworkFrame.RenderTransform = artworkScale;
        artworkClip.SizeChanged += (_, e) =>
        {
            artworkCorner = new CornerRadius(Math.Max(6, Math.Min(e.NewSize.Width, e.NewSize.Height) * 0.045));
            artworkStroke.CornerRadius = artworkPlaceholder.CornerRadius = artworkCorner;
            foreach (var child in artworkImages.Children)
                if (child is Border b) b.CornerRadius = artworkCorner;
        };
        artworkFrame.SizeChanged += (_, e) => artworkInner.CornerRadius = new CornerRadius(e.NewSize.Width * 0.09);
        artworkFrame.Cursor = Cursors.Hand;
        artworkFrame.MouseLeftButtonUp += (_, e) => { ToggleLyrics(); e.Handled = true; };
    }

    private void BuildControls()
    {
        var stack = new StackPanel();
        var titles = new StackPanel { HorizontalAlignment = HorizontalAlignment.Stretch };
        titleText.Margin = new Thickness(0, 0, 0, 2);
        titles.Children.Add(titleText);
        titles.Children.Add(artistText);
        stack.Children.Add(titles);

        progress.Margin = new Thickness(0, 12, 0, 0);
        progress.Children.Add(progressTrack);
        progress.Children.Add(progressFill);
        progress.MouseLeftButtonDown += (_, e) => { scrubbing = true; progress.CaptureMouse(); Scrub(e); SetProgressActive(); e.Handled = true; };
        progress.MouseMove += (_, e) => { if (scrubbing) Scrub(e); };
        progress.MouseLeftButtonUp += (_, e) =>
        {
            if (!scrubbing) return;
            scrubbing = false;
            progress.ReleaseMouseCapture();
            if (media.Duration > 0) media.Seek(scrubFraction * media.Duration);
            SetProgressActive();
            e.Handled = true;
        };
        progress.MouseEnter += (_, _) => { hoveringProgress = true; SetProgressActive(); };
        progress.MouseLeave += (_, _) => { hoveringProgress = false; SetProgressActive(); };
        stack.Children.Add(progress);
        var times = new Grid { Margin = new Thickness(0, 5, 0, 0) };
        elapsedText.FontFamily = remainingText.FontFamily = Glass.Rounded;
        Typography.SetNumeralAlignment(elapsedText, FontNumeralAlignment.Tabular);
        Typography.SetNumeralAlignment(remainingText, FontNumeralAlignment.Tabular);
        times.Children.Add(elapsedText);
        times.Children.Add(remainingText);
        stack.Children.Add(times);

        var buttons = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        RenderOptions.SetBitmapScalingMode(sourceIcon, BitmapScalingMode.HighQuality);
        sourceIcon.VerticalAlignment = VerticalAlignment.Center;
        buttons.Children.Add(sourceIcon);
        var transport = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        playButton.Margin = new Thickness(30, 0, 30, 0);
        transport.Children.Add(previousButton);
        transport.Children.Add(playButton);
        transport.Children.Add(nextButton);
        Grid.SetColumn(transport, 1);
        buttons.Children.Add(transport);
        lyricsCardButton.Child = lyricsCardSymbol;
        lyricsCardButton.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(lyricsCardButton, 2);
        buttons.Children.Add(lyricsCardButton);
        stack.Children.Add(buttons);
        // The glass card (with its shadow) is drawn once and kept on the GPU.
        // The (bigger) shadow image must not size the card.
        controls.Children.Add(new Backdrop { Child = controlsShadow });
        controls.Children.Add(controlsGlass);
        // Redraw the shadow once the size has settled (it stretches meanwhile).
        controlsShadowTimer.Tick += (_, _) => { controlsShadowTimer.Stop(); UpdateControlsShadow(); };
        controls.SizeChanged += (_, _) => { controlsShadowTimer.Stop(); controlsShadowTimer.Start(); };
        controls.Children.Add(new Border { Padding = new Thickness(18, 14, 18, 14), Child = stack });

        previousButton.Click += () => media.Previous();
        playButton.Click += () => media.PlayPause();
        nextButton.Click += () => media.Next();
        lyricsCardButton.Click += ToggleLyrics;
    }

    private void BuildToolbar()
    {
        offsetLabel.Child = offsetText;
        offsetText.FontFamily = Glass.Rounded;
        foreach (var element in new FrameworkElement[] { lyricsButton, laterButton, offsetLabel, earlierButton, positionButton, wallpaperButton, hideButton, closeButton })
        {
            element.Margin = new Thickness(0, 0, 0, 10);
            toolbar.Children.Add(element);
        }
        lyricsButton.Click += ToggleLyrics;
        laterButton.Click += () => lyrics.Nudge(-LyricsController.OffsetStep);
        earlierButton.Click += () => lyrics.Nudge(LyricsController.OffsetStep);
        positionButton.Click += () =>
        {
            settings.LyricsPosition = settings.LyricsPosition switch
            {
                LyricsPosition.Right => LyricsPosition.Left,
                LyricsPosition.Left => LyricsPosition.Center,
                _ => LyricsPosition.Right,
            };
        };
        wallpaperButton.Click += () => settings.LockWallpaper = !settings.LockWallpaper;
        hideButton.Click += () => { collapsed = !collapsed; ApplyCollapsed(true); Refresh(); };
        closeButton.Click += () => CloseRequested?.Invoke();
    }

    // MARK: Public API

    /// Entrance: the artwork wallpaper zooms in out of a blur, the widget rises into place.
    public void PlayEntrance()
    {
        lyrics.ReloadIfNeeded();
        collapsed = false;
        ApplyCollapsed(false);
        LoadWallpaper();
        ApplyTheme();
        Relayout(false);
        if (showsLyrics) lyricsPanel.Reveal();
        clockTimer.Start();
        clock.Refresh();
        systemClock.Refresh();
        Refresh();

        bool lite = settings.LiteMode;
        artworkBackdrop.Opacity = 0;
        Glass.Animate(artworkBackdrop, OpacityProperty, DrawsBackground ? 1 : 0, 0.55);
        if (!lite)
        {
            backgroundScale.ScaleX = backgroundScale.ScaleY = 1.12;
            Glass.Animate(backgroundScale, ScaleTransform.ScaleXProperty, 1, 0.55);
            Glass.Animate(backgroundScale, ScaleTransform.ScaleYProperty, 1, 0.55);
        }
        plainBackdrop.Opacity = 0;
        Glass.Animate(plainBackdrop, OpacityProperty, 1, 0.4);
        // The widget rises into place with a spring.
        content.Opacity = 0;
        Glass.Animate(content, OpacityProperty, 1, 0.4, delay: 0.08);
        contentOffset.Y = 70;
        contentScale.ScaleX = contentScale.ScaleY = 0.94;
        Glass.Animate(contentOffset, TranslateTransform.YProperty, 0, 0.62, Glass.Spring, 0.08);
        Glass.Animate(contentScale, ScaleTransform.ScaleXProperty, 1, 0.62, Glass.Spring, 0.08);
        Glass.Animate(contentScale, ScaleTransform.ScaleYProperty, 1, 0.62, Glass.Spring, 0.08);
        toolbar.Opacity = 0;
        Glass.Animate(toolbar, OpacityProperty, 1, 0.4, delay: 0.2);
        hint.Opacity = 0;
        Glass.Animate(hint, OpacityProperty, 0.6, 0.4, delay: 0.3);
    }

    /// Exit: the widget drops away, everything fades. Calls `done` when finished.
    public void PlayExit(Action done)
    {
        clockTimer.Stop();
        Glass.Animate(contentOffset, TranslateTransform.YProperty, 70, 0.22, new QuadraticEase { EasingMode = EasingMode.EaseIn });
        var fade = new DoubleAnimation(0, TimeSpan.FromSeconds(0.3));
        fade.Completed += (_, _) => done();
        BeginAnimation(OpacityProperty, fade);
    }

    public void ResetOpacity()
    {
        BeginAnimation(OpacityProperty, null);
        Opacity = 1;
    }

    public void Stop()
    {
        clockTimer.Stop();
    }

    /// Keyboard shortcuts while the lock screen is up. Returns true if handled.
    public bool HandleKey(Key key)
    {
        switch (key)
        {
            // (Media keys are left to Windows / the player itself.)
            case Key.Space:
                media.PlayPause();
                return true;
            case Key.Right:
                media.Next();
                return true;
            case Key.Left:
                media.Previous();
                return true;
            case Key.L:
                ToggleLyrics();
                return true;
            case Key.F:
                fpsText.Visibility = fpsText.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
                return true;
            case Key.Enter:
            case Key.Escape:
                Dismiss();
                return true;
            default:
                return false;
        }
    }

    /// Final state without animations (self-test snapshots).
    internal void ShowStatic(bool withLyrics)
    {
        showsLyrics = withLyrics;
        LoadWallpaper();
        lyricsHost.BeginAnimation(OpacityProperty, null);
        lyricsHost.Opacity = HasLyricsMode ? 1 : 0;
        lyricsHost.IsHitTestVisible = HasLyricsMode;
        foreach (var element in new UIElement[] { plainBackdrop, artworkBackdrop, content, toolbar, hint })
            element.BeginAnimation(OpacityProperty, null);
        plainBackdrop.Opacity = 1;
        content.Opacity = toolbar.Opacity = 1;
        hint.Opacity = 0.6;
        artworkBackdrop.Opacity = DrawsBackground ? 1 : 0;
        foreach (var p in new[] { ScaleTransform.ScaleXProperty, ScaleTransform.ScaleYProperty })
        {
            backgroundScale.BeginAnimation(p, null);
            contentScale.BeginAnimation(p, null);
            artworkScale.BeginAnimation(p, null);
        }
        backgroundScale.ScaleX = backgroundScale.ScaleY = 1;
        contentScale.ScaleX = contentScale.ScaleY = 1;
        artworkScale.ScaleX = artworkScale.ScaleY = media.IsPlaying ? 1 : 0.84;
        contentOffset.BeginAnimation(TranslateTransform.YProperty, null);
        contentOffset.Y = 0;
        ApplyTheme();
        Refresh();
        Relayout(false);
    }

    /// The Mac reads the position over AppleScript, ~1.5 s behind the music,
    /// and its default "1.5 s ahead" makes up for that. Windows' media
    /// sessions are on time, so the same slider value would run 1.5 s early.
    public const double TimingCorrection = -1.5;

    /// Per-frame update: progress bar and lyrics. `frameTime` is WPF's frame
    /// clock (steady, unlike reading the system time); `step` is for the self-test.
    public void Tick(double? step = null, TimeSpan? frameTime = null)
    {
        var now = DateTime.UtcNow;
        double clock = frameTime?.TotalSeconds ?? Environment.TickCount64 / 1000.0;
        frames++;
        if ((now - fpsSince).TotalSeconds >= 1)
        {
            if (fpsText.Visibility == Visibility.Visible)
                fpsText.Text = $"{frames / (now - fpsSince).TotalSeconds:0} fps · {(RenderCapability.Tier >> 16 >= 2 ? "GPU" : "software")}";
            frames = 0;
            fpsSince = now;
        }
        // Only the 30 fps setting (and Lite mode) skips frames; 60/120 run at the display's rate.
        if (step == null && settings.FrameInterval > 1.0 / 50 && clock - lastClock < settings.FrameInterval - 0.004) return;
        double dt = step ?? Math.Clamp(clock - lastClock, 0, 0.1);
        lastClock = clock;
        double position = media.Position(now);
        double duration = media.Duration;

        double fraction = scrubbing ? scrubFraction : duration > 0 ? Math.Clamp(position / duration, 0, 1) : 0;
        progressFill.Width = Math.Max(0, progress.ActualWidth * fraction);
        double shownPosition = scrubbing ? scrubFraction * duration : position;
        int second = (int)shownPosition;
        if (second != shownSecond)
        {
            shownSecond = second;
            elapsedText.Text = Format(shownPosition);
            remainingText.Text = duration > 0 ? "-" + Format(Math.Max(0, duration - shownPosition)) : "LIVE";
        }

        if (HasLyricsMode)
            lyricsPanel.Tick(position + settings.LyricsLead + TimingCorrection + lyrics.SongOffset, dt, media.IsPlaying);
    }

    private void Dismiss()
    {
        if (presentation == Presentation.Preview) CloseRequested?.Invoke();
        else UnlockRequested?.Invoke();
    }

    // MARK: Reacting to changes

    private void OnPlaybackChanged()
    {
        playButton.SymbolName = media.IsPlaying ? "pause.fill" : "play.fill";
        // The artwork shrinks a little while paused (Apple Music style).
        double target = media.IsPlaying ? 1 : 0.84;
        Glass.Animate(artworkScale, ScaleTransform.ScaleXProperty, target, 0.55, Glass.Spring);
        Glass.Animate(artworkScale, ScaleTransform.ScaleYProperty, target, 0.55, Glass.Spring);
        ApplyArtworkShadow();
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppSettings.ArtworkScale):
            case nameof(AppSettings.PlainArtworkScale):
            case nameof(AppSettings.WidgetBottomInset):
            case nameof(AppSettings.PlainBottomInset):
            case nameof(AppSettings.LyricsOffset):
            case nameof(AppSettings.PlainLyricsOffset):
            case nameof(AppSettings.LyricsPosition):
                Relayout(true);
                Refresh();
                break;
            case nameof(AppSettings.LockWallpaper):
                ApplyTheme();
                Relayout(true);
                Refresh();
                Glass.Animate(artworkBackdrop, OpacityProperty, DrawsBackground ? 1 : 0, 0.5);
                break;
            case nameof(AppSettings.LyricsScale):
            case nameof(AppSettings.LyricsFont):
            case nameof(AppSettings.LyricsWeight):
            case nameof(AppSettings.ClockFont):
            case nameof(AppSettings.ClockWeight):
            case nameof(AppSettings.GlassLevel):
            case nameof(AppSettings.LiteMode):
            case nameof(AppSettings.BackgroundStyle):
            case nameof(AppSettings.EffectiveWordByWord):
                ApplyTheme();
                Refresh();
                break;
        }
    }

    // MARK: Content

    private void UpdateTrack()
    {
        titleText.Text = media.Track?.Title ?? "";
        artistText.Text = media.Track?.Artist ?? "";
        sourceIcon.Source = media.SourceIcon;
        sourceIcon.ToolTip = media.SourceName != null ? $"Playing in {media.SourceName}" : null;
        playButton.SymbolName = media.IsPlaying ? "pause.fill" : "play.fill";
        artworkScale.ScaleX = artworkScale.ScaleY = media.IsPlaying || !media.HasTrack ? 1 : 0.84;
        shownSecond = -1;
        UpdateArtwork(false);
        ApplyTheme();
        Refresh();
    }

    /// Cross-fades to the new artwork.
    private void UpdateArtwork(bool animated)
    {
        if (media.Artwork == null)
        {
            foreach (UIElement child in artworkImages.Children) Glass.Animate(child, OpacityProperty, 0, 0.6);
            return;
        }
        // A rounded border filled with the image: corners follow the size
        // exactly while it animates (a clip could lag a frame behind).
        var brush = new ImageBrush(media.Artwork) { Stretch = Stretch.UniformToFill };
        RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.HighQuality);
        brush.Freeze();
        var image = new Border { Background = brush, CornerRadius = artworkCorner };
        if (!animated || artworkImages.Children.Count == 0)
        {
            artworkImages.Children.Clear();
            artworkImages.Children.Add(image);
            return;
        }
        image.Opacity = 0;
        artworkImages.Children.Add(image);
        var fade = new DoubleAnimation(1, TimeSpan.FromSeconds(0.6)) { EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
        fade.Completed += (_, _) =>
        {
            while (artworkImages.Children.Count > 1) artworkImages.Children.RemoveAt(0);
        };
        image.BeginAnimation(OpacityProperty, fade);
    }

    private void ToggleLyrics()
    {
        if (!media.HasTrack) return;
        showsLyrics = !showsLyrics;
        if (collapsed)
        {
            collapsed = false;
            ApplyCollapsed(true);
        }
        Glass.Animate(lyricsHost, OpacityProperty, HasLyricsMode ? 1 : 0, 0.4);
        lyricsHost.IsHitTestVisible = HasLyricsMode;
        // Only what depends on the mode (a full theme rebuild froze a frame).
        background.Update(media, HasLyricsMode ? BackgroundStyle.Blurred : settings.BackgroundStyle, settings.LiteMode);
        Relayout(true);
        Refresh();
    }

    private void ApplyCollapsed(bool animated)
    {
        double opacity = collapsed ? 0 : 1, scale = collapsed ? 0.92 : 1;
        content.IsHitTestVisible = !collapsed;
        if (animated)
        {
            Glass.Animate(content, OpacityProperty, opacity, 0.5);
            Glass.Animate(contentScale, ScaleTransform.ScaleXProperty, scale, 0.5, Glass.Spring);
            Glass.Animate(contentScale, ScaleTransform.ScaleYProperty, scale, 0.5, Glass.Spring);
        }
        else
        {
            content.BeginAnimation(OpacityProperty, null);
            content.Opacity = opacity;
        }
    }

    private void LoadWallpaper()
    {
        wallpaperImage.Source = ArtworkAnalyzer.Wallpaper((int)Math.Max(ActualWidth, 1920));
    }

    /// Toolbar, hint and mode-dependent bits.
    private void Refresh()
    {
        var level = settings.GlassLevel;
        bool lite = settings.LiteMode, dark = !IsLight;
        bool lyricsOn = HasLyricsMode;

        lyricsButton.SymbolName = lyricsOn ? "quote.bubble.fill" : "quote.bubble";
        lyricsButton.ApplyLook(level, lite, dark, lyricsOn);
        lyricsButton.IsEnabled = media.HasTrack;
        lyricsButton.Opacity = media.HasTrack ? 1 : 0.4;
        var extras = lyricsOn ? Visibility.Visible : Visibility.Collapsed;
        laterButton.Visibility = earlierButton.Visibility = positionButton.Visibility = extras;
        offsetLabel.Visibility = lyricsOn && Math.Abs(lyrics.SongOffset) > 0.001 ? Visibility.Visible : Visibility.Collapsed;
        offsetText.Text = lyrics.SongOffset.ToString("+0.00;-0.00", CultureInfo.InvariantCulture) + " s";
        Glass.Apply(offsetLabel, level, lite, dark, shadow: false);
        laterButton.ApplyLook(level, lite, dark, false);
        earlierButton.ApplyLook(level, lite, dark, false);
        positionButton.SymbolName = settings.LyricsPosition switch
        {
            LyricsPosition.Left => "rectangle.lefthalf.inset.filled",
            LyricsPosition.Center => "rectangle.center.inset.filled",
            _ => "rectangle.righthalf.inset.filled",
        };
        positionButton.ToolTip = $"Lyrics position: {Titles.Of(settings.LyricsPosition)}";
        positionButton.ApplyLook(level, lite, dark, false);
        wallpaperButton.SymbolName = settings.LockWallpaper ? "photo.fill" : "photo";
        wallpaperButton.ApplyLook(level, lite, dark, settings.LockWallpaper);
        hideButton.SymbolName = collapsed ? "eye.slash" : "eye";
        hideButton.ToolTip = collapsed ? "Show widget" : "Hide widget";
        hideButton.ApplyLook(level, lite, dark, collapsed);
        closeButton.ApplyLook(level, lite, dark, false);
        closeButton.Visibility = presentation == Presentation.Preview ? Visibility.Visible : Visibility.Collapsed;
        lyricsCardSymbol.SymbolName = lyricsOn ? "quote.bubble.fill" : "quote.bubble";
        artworkFrame.ToolTip = lyricsOn ? "Back to player" : "Show lyrics";

        bool plain = !DrawsBackground;
        account.Visibility = plain && !widgetHidden ? Visibility.Visible : Visibility.Collapsed;
        hint.Text = presentation == Presentation.Preview
            ? "Preview · click the background or press Esc to close"
            : "Press Enter or click here to unlock";
        hint.Visibility = plain || widgetHidden ? Visibility.Collapsed : Visibility.Visible;
        artworkBackdrop.IsHitTestVisible = DrawsBackground;

        content.Visibility = widgetHidden || !media.HasTrack ? Visibility.Hidden : Visibility.Visible;
        toolbar.Visibility = widgetHidden ? Visibility.Collapsed : Visibility.Visible;
    }

    /// Colours, fonts and glass from the artwork palette + settings.
    private void ApplyTheme()
    {
        TextElement.SetForeground(this, Glass.Frozen(new SolidColorBrush(TextColor)));
        bool lite = settings.LiteMode, dark = !IsLight;
        var level = settings.GlassLevel;
        bool mega = level == GlassLevel.Mega && !lite;

        background.Update(media, HasLyricsMode ? BackgroundStyle.Blurred : settings.BackgroundStyle, lite);
        ApplyClockSizes();

        Glass.Apply(controlsGlass, level, lite, dark, shadow: false);
        controlsShadowSize = default;
        UpdateControlsShadow();
        var primary = TextColor;
        progressTrack.Background = new SolidColorBrush(Color.FromArgb(56, primary.R, primary.G, primary.B));
        SetProgressActive();
        previousButton.ApplyLook(level, lite, dark);
        playButton.ApplyLook(level, lite, dark);
        nextButton.ApplyLook(level, lite, dark);

        // Mega Liquid Glass: the artwork sits in a thin glass frame, lyrics on a glass panel.
        double side = artSide;
        artworkInner.Padding = new Thickness(mega ? Math.Min(10, Math.Max(5, side * 0.025)) : 0);
        Glass.Apply(artworkInner, level, lite, dark, onlyInMega: true, shadow: false);
        artworkInner.CornerRadius = new CornerRadius(side * 0.09);
        ApplyArtworkShadow();
        Glass.Apply(lyricsHost, level, lite, dark, onlyInMega: true, shadow: false);
        lyricsHost.Padding = new Thickness(mega ? 14 : 0);
        lyricsPanel.TextShadow = !(DrawsBackground || mega);
        lyricsPanel.Lite = lite;

        lyricsPanel.TextColor = TextColor;
        lyricsPanel.Lite = lite;
        lyricsPanel.WordByWord = settings.EffectiveWordByWord;
        lyricsPanel.TextScale = settings.LyricsScale;
        lyricsPanel.LyricsFont = Glass.Family(settings.LyricsFont);
        lyricsPanel.LyricsWeight = Glass.Weight(settings.LyricsWeight);
        lyricsPanel.ApplyTextColor();
        lyricsPanel.InvalidateLines();
    }

    /// The artwork's soft shadow (stronger and bigger while playing), as an image.
    private void ApplyArtworkShadow()
    {
        double side = artSide;
        if (settings.GlassLevel == GlassLevel.Mega && !settings.LiteMode)
        {
            // The glass frame's own shadow.
            artworkShadow.Set(0.09, 22 / side, 0.28, 10 / side);
            return;
        }
        bool playing = media.IsPlaying;
        double corner = Math.Max(6, side * 0.045);
        if (settings.LiteMode) artworkShadow.Set(corner / side, 8 / side, playing ? 0.42 : 0.25, 4 / side);
        else artworkShadow.Set(0.045, playing ? 0.09 : 0.05, playing ? 0.42 : 0.25, playing ? 0.05 : 0.025);
    }

    /// The player card's shadow, redrawn only when its size really changes.
    private void UpdateControlsShadow()
    {
        var size = new Size(controls.ActualWidth, controls.ActualHeight);
        if (Math.Abs(size.Width - controlsShadowSize.Width) < 6 && Math.Abs(size.Height - controlsShadowSize.Height) < 6) return;
        controlsShadowSize = size;
        if (size.Width > 0 && Glass.ShadowOf(settings.GlassLevel, settings.LiteMode) is { } s)
            Shadows.Apply(controlsShadow, size.Width, size.Height, 24, s.Radius, s.Opacity, s.Y);
        else controlsShadow.Source = null;
    }

    private void ApplyClockSizes()
    {
        double h = ActualHeight > 0 ? ActualHeight : 1080;
        double size = Math.Min(h * 0.12, 150);
        clock.Apply(size, Glass.Family(settings.ClockFont), Glass.Weight(settings.ClockWeight));
        clock.Margin = new Thickness(0, h * 0.06, 0, 0);
        // The "system" clock of plain mode (like the Mac lock screen's own).
        systemClock.Apply(size, Glass.System, FontWeights.SemiBold);
        systemClock.Margin = new Thickness(0, h * 0.06, 0, 0);
        account.Margin = new Thickness(0, 0, 0, h * 0.07);
    }

    // MARK: Layout (same numbers as LockWidgetView.swift)
    //
    // Three independent settings (per mode): artwork size, player height
    // (bottom of artwork + controls) and lyrics position.

    private const double TopFraction = 0.22;
    private const double LyricsHeightFraction = 0.44;
    private const double ControlsHeight = 150;

    private void Relayout(bool animated)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        double top = h * TopFraction;
        double widgetBottom = Math.Max(h * (1 - settings.ActiveBottomInset), top + h * 0.25);
        double lyricsTop = Math.Max(h * 0.01, h * (0.26 + settings.ActiveLyricsOffset));
        double lyricsHeight = Math.Min(h * LyricsHeightFraction, h * 0.98 - lyricsTop);
        double scale = settings.ActiveArtworkScale;
        double BlockTop(double height) => Math.Max(top, widgetBottom - height);
        double MaxArtwork(double width) => Math.Min(width, h * 0.97 - top - ControlsHeight - 20);

        if (!HasLyricsMode)
        {
            // Artwork + controls, centred.
            double baseSide = Math.Min(h * 0.2, w * 0.2);
            double art = Math.Max(90, Math.Min(baseSide * scale, MaxArtwork(w * 0.5)));
            double blockTop = BlockTop(art + 20 + ControlsHeight);
            double cw = Math.Max(art * 0.95, 300);
            Place(artworkFrame, (w - art) / 2, blockTop, art, art, animated);
            artSide = art;
            Place(controls, (w - cw) / 2, blockTop + art + 20, cw, double.NaN, animated);
            Place(lyricsHost, w / 2, lyricsTop, w / 2 * 0.82, lyricsHeight, animated, animateSize: false);
            lyricsPanel.SetAlignment(settings.LyricsPosition == LyricsPosition.Center);
        }
        else if (settings.LyricsPosition is LyricsPosition.Right or LyricsPosition.Left)
        {
            // Screen split in two halves: artwork + controls in one, lyrics in the other.
            double half = w / 2;
            bool right = settings.LyricsPosition == LyricsPosition.Right;
            double baseSide = Math.Min(h * 0.2, w * 0.16);
            double art = Math.Max(90, Math.Min(baseSide * scale, MaxArtwork(half * 0.88)));
            double blockTop = BlockTop(art + 18 + ControlsHeight);
            double cw = Math.Max(Math.Min(art, half * 0.88), 300);
            double centerX = right ? half / 2 : half + half / 2;
            Place(artworkFrame, centerX - art / 2, blockTop, art, art, animated);
            artSide = art;
            Place(controls, centerX - cw / 2, blockTop + art + 18, cw, double.NaN, animated);
            double lw = half * 0.82;
            Place(lyricsHost, right ? half : half - lw, lyricsTop, lw, lyricsHeight, animated, animateSize: false);
            lyricsPanel.SetAlignment(false);
        }
        else
        {
            // Big centred lyrics, a small artwork + controls row underneath.
            const double rowHeight = 150;
            double textTop = lyricsTop;
            double textHeight = Math.Max(160, lyricsHeight - rowHeight - 16);
            double rowTop = Math.Min(Math.Max(textTop + textHeight + 16, widgetBottom - rowHeight), h * 0.97 - rowHeight);
            double lw = Math.Min(w * 0.62, 900);
            Place(lyricsHost, (w - lw) / 2, textTop, lw, textHeight, animated, animateSize: false);
            double art = Math.Min(76 * scale, rowHeight);
            double rowWidth = art + 16 + 340;
            double x = (w - rowWidth) / 2;
            double controlsHeight = controls.ActualHeight > 0 ? controls.ActualHeight : 166;
            double rowInner = Math.Max(art, controlsHeight);
            Place(artworkFrame, x, rowTop + (rowInner - art) / 2, art, art, animated);
            artSide = art;
            Place(controls, x + art + 16, rowTop + (rowInner - controlsHeight) / 2, 340, double.NaN, animated);
            lyricsPanel.SetAlignment(true);
        }
        double inner = artSide - artworkInner.Padding.Left * 2;
        placeholderSymbol.Size = inner * 0.32;
        ApplyArtworkShadow();
    }

    private static readonly System.Collections.Generic.Dictionary<(FrameworkElement, DependencyProperty), double> PlaceTargets = new();

    private static void Place(FrameworkElement element, double x, double y, double width, double height, bool animated, bool animateSize = true)
    {
        if (!animated || double.IsNaN(Canvas.GetLeft(element)))
        {
            foreach (var p in new[] { Canvas.LeftProperty, Canvas.TopProperty, WidthProperty, HeightProperty })
            {
                element.BeginAnimation(p, null);
                PlaceTargets.Remove((element, p));
            }
            Canvas.SetLeft(element, x);
            Canvas.SetTop(element, y);
            element.Width = width;
            element.Height = height;
            return;
        }
        // Like SwiftUI's .spring(response: 0.6, dampingFraction: 0.86).
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        void Go(DependencyProperty p, double to)
        {
            double from = (double)element.GetValue(p);
            if (double.IsNaN(to) || double.IsNaN(from) || Math.Abs(from - to) < 0.5)
            {
                element.BeginAnimation(p, null);
                PlaceTargets.Remove((element, p));
                element.SetValue(p, to);
                return;
            }
            // WPF starts an animation on the *next* frame; until then the base
            // value shows. So the base stays where the element is now (setting
            // it to the target made everything flash at its end position for
            // one frame), and becomes the target when the animation ends.
            element.SetValue(p, from);
            PlaceTargets[(element, p)] = to;
            var animation = new DoubleAnimation(from, to, TimeSpan.FromSeconds(0.55)) { EasingFunction = ease };
            animation.Completed += (_, _) =>
            {
                if (!PlaceTargets.TryGetValue((element, p), out var target) || target != to) return; // superseded
                PlaceTargets.Remove((element, p));
                element.SetValue(p, to);
                element.BeginAnimation(p, null);
            };
            element.BeginAnimation(p, animation);
        }
        Go(Canvas.LeftProperty, x);
        Go(Canvas.TopProperty, y);
        if (animateSize)
        {
            Go(WidthProperty, width);
            Go(HeightProperty, height);
        }
        else
        {
            element.BeginAnimation(WidthProperty, null);
            element.BeginAnimation(HeightProperty, null);
            PlaceTargets.Remove((element, WidthProperty));
            PlaceTargets.Remove((element, HeightProperty));
            element.Width = width;
            element.Height = height;
        }
    }

    // MARK: Progress bar

    private void Scrub(MouseEventArgs e)
    {
        if (progress.ActualWidth <= 0) return;
        scrubFraction = Math.Clamp(e.GetPosition(progress).X / progress.ActualWidth, 0, 1);
    }

    /// Thicker and brighter while hovered or scrubbed.
    private void SetProgressActive()
    {
        bool active = hoveringProgress || scrubbing;
        double thickness = active ? 9 : 5;
        progressTrack.Height = progressFill.Height = thickness;
        progressTrack.CornerRadius = progressFill.CornerRadius = new CornerRadius(thickness / 2);
        var c = TextColor;
        progressFill.Background = new SolidColorBrush(Color.FromArgb(active ? (byte)242 : (byte)179, c.R, c.G, c.B));
    }

    public static string Format(double seconds)
    {
        if (!double.IsFinite(seconds)) return "0:00";
        int total = (int)Math.Floor(seconds);
        int hours = total / 3600, minutes = total % 3600 / 60, secs = total % 60;
        return hours > 0 ? $"{hours}:{minutes:00}:{secs:00}" : $"{minutes}:{secs:00}";
    }
}
