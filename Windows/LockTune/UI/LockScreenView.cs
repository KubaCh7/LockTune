using System;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using LockTune.Core;
using LockTune.Lyrics;
using LockTune.Media;

namespace LockTune.UI;

public enum Presentation { Lock, Preview }

/// The LockTune lock screen: clock + date, the artwork (smaller while
/// paused), glass player controls, synced lyrics and the toolbar on the left
/// — on top of the blurred, drifting artwork.
public sealed class LockScreenView : Grid
{
    private readonly MediaController media;
    private readonly LyricsController lyrics;
    private readonly AppSettings settings;

    private readonly BackgroundView background = new();
    private readonly ScaleTransform backgroundScale = new(1, 1);
    private readonly StackPanel clock = new() { HorizontalAlignment = HorizontalAlignment.Center, IsHitTestVisible = false };
    private readonly TextBlock dateText = new() { HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock timeText = new() { HorizontalAlignment = HorizontalAlignment.Center };
    private readonly Canvas content = new();
    private readonly TranslateTransform contentOffset = new();
    private readonly Border artworkFrame = new();
    private readonly Border artwork = new() { ClipToBounds = true };
    private readonly TextBlock artworkPlaceholder = new() { Text = Icons.Music, FontFamily = Icons.Font, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.6 };
    private readonly ScaleTransform artworkScale = new(1, 1);
    private readonly Border controls = new() { CornerRadius = new CornerRadius(24), Padding = new Thickness(18, 14, 18, 12) };
    private readonly TextBlock titleText = new() { FontSize = 15, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock artistText = new() { FontSize = 13, Opacity = 0.65, TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly Grid progress = new() { Height = 16, Background = Brushes.Transparent, Cursor = Cursors.Hand };
    private readonly Border progressTrack = new() { Height = 5, CornerRadius = new CornerRadius(3), VerticalAlignment = VerticalAlignment.Center };
    private readonly Border progressFill = new() { Height = 5, CornerRadius = new CornerRadius(3), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock elapsedText = new() { FontSize = 11, Opacity = 0.6 };
    private readonly TextBlock remainingText = new() { FontSize = 11, Opacity = 0.6, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly TextBlock sourceText = new() { FontSize = 11, Opacity = 0.55, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly GlassButton previousButton = new(Icons.Previous, 46, 20, "Previous", glass: false);
    private readonly GlassButton playButton = new(Icons.Pause, 56, 28, "Play / Pause (Space)", glass: false);
    private readonly GlassButton nextButton = new(Icons.Next, 46, 20, "Next", glass: false);
    private readonly GlassButton lyricsCardButton = new(Icons.Lyrics, 34, 16, "Lyrics (L)", glass: false);
    private readonly Border lyricsHost = new() { CornerRadius = new CornerRadius(30) };
    private readonly LyricsPanel lyricsPanel = new();
    private readonly StackPanel toolbar = new() { Margin = new Thickness(26, 34, 0, 0), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
    private readonly GlassButton lyricsButton = new(Icons.Lyrics, 36, 14, "Lyrics (L)");
    private readonly GlassButton laterButton = new(Icons.Remove, 36, 13, "Lyrics later (this song)");
    private readonly Border offsetLabel = new() { CornerRadius = new CornerRadius(10), Padding = new Thickness(6, 3, 6, 3), HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock offsetText = new() { FontSize = 10, FontWeight = FontWeights.Bold };
    private readonly GlassButton earlierButton = new(Icons.Add, 36, 13, "Lyrics earlier (this song)");
    private readonly GlassButton positionButton = new(Icons.DockRight, 36, 14, "Lyrics position");
    private readonly GlassButton backgroundButton = new(Icons.Photo, 36, 14, "Background style");
    private readonly GlassButton hideButton = new(Icons.View, 36, 14, "Hide widget");
    private readonly GlassButton closeButton = new(Icons.Close, 36, 13, "Close preview (Esc)");
    private readonly Border unlockPill = new() { CornerRadius = new CornerRadius(22), Padding = new Thickness(18, 9, 18, 9), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Cursor = Cursors.Hand };
    private readonly TextBlock unlockIcon = new() { FontFamily = Icons.Font, FontSize = 14, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
    private readonly TextBlock unlockText = new() { FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };

    private readonly DispatcherTimer clockTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool showsLyrics;
    private bool lyricsChosenAutomatically;
    private bool collapsed;
    private bool scrubbing;
    private double scrubFraction;
    private int shownSecond = -1;
    private DateTime lastTick = DateTime.UtcNow;
    private Point? swipeStart;
    private Presentation presentation = Presentation.Preview;

    public event Action? UnlockRequested;
    public event Action? CloseRequested;

    public Presentation Presentation
    {
        get => presentation;
        set { presentation = value; UpdateChrome(); }
    }

    public LockScreenView(MediaController media, LyricsController lyrics, AppSettings settings)
    {
        this.media = media;
        this.lyrics = lyrics;
        this.settings = settings;
        // Transparent: while it fades in, a screenshot of the desktop shows through.
        Background = Brushes.Transparent;
        ClipToBounds = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.Grayscale);

        background.RenderTransformOrigin = new Point(0.5, 0.5);
        background.RenderTransform = backgroundScale;
        Children.Add(background);

        clock.Children.Add(dateText);
        clock.Children.Add(timeText);
        Children.Add(clock);

        BuildArtwork();
        BuildControls();
        lyricsHost.Child = lyricsPanel;
        lyricsHost.Opacity = 0;
        lyricsHost.IsHitTestVisible = false;
        lyricsPanel.SeekRequested += t => media.Seek(t);
        content.Children.Add(lyricsHost);
        content.Children.Add(artworkFrame);
        content.Children.Add(controls);
        content.RenderTransform = contentOffset;
        Children.Add(content);

        BuildToolbar();
        Children.Add(toolbar);
        BuildUnlockPill();
        Children.Add(unlockPill);

        clockTimer.Tick += (_, _) => UpdateClock();
        SizeChanged += (_, _) => { Relayout(false); UpdateClockSize(); };
        MouseLeftButtonDown += (_, e) => { swipeStart = e.GetPosition(this); };
        MouseMove += (_, e) =>
        {
            if (swipeStart is not { } start || e.LeftButton != MouseButtonState.Pressed) return;
            if (start.Y - e.GetPosition(this).Y > 140) { swipeStart = null; Dismiss(); }
        };
        MouseLeftButtonUp += (_, _) => swipeStart = null;

        media.TrackChanged += OnTrackChanged;
        media.ArtworkChanged += OnArtworkChanged;
        media.PlaybackChanged += OnPlaybackChanged;
        lyrics.PropertyChanged += (_, _) => OnLyricsChanged();
        settings.PropertyChanged += OnSettingsChanged;

        ApplyTheme();
        UpdateTrack();
        UpdateClock();
        OnLyricsChanged();
    }

    // MARK: Building

    private void BuildArtwork()
    {
        artwork.Child = artworkPlaceholder;
        artworkFrame.Child = artwork;
        artworkFrame.RenderTransformOrigin = new Point(0.5, 0.5);
        artworkFrame.RenderTransform = artworkScale;
        artworkFrame.Cursor = Cursors.Hand;
        artworkFrame.ToolTip = "Show lyrics";
        artworkFrame.MouseLeftButtonUp += (_, e) => { ToggleLyrics(); e.Handled = true; };
    }

    private void BuildControls()
    {
        var stack = new StackPanel();
        stack.Children.Add(titleText);
        stack.Children.Add(artistText);
        progress.Margin = new Thickness(0, 10, 0, 0);
        progress.Children.Add(progressTrack);
        progress.Children.Add(progressFill);
        progress.MouseLeftButtonDown += (_, e) => { scrubbing = true; progress.CaptureMouse(); Scrub(e); e.Handled = true; };
        progress.MouseMove += (_, e) => { if (scrubbing) Scrub(e); };
        progress.MouseLeftButtonUp += (_, e) =>
        {
            if (!scrubbing) return;
            scrubbing = false;
            progress.ReleaseMouseCapture();
            if (media.Duration > 0) media.Seek(scrubFraction * media.Duration);
            e.Handled = true;
        };
        progress.MouseEnter += (_, _) => SetProgressThickness(9);
        progress.MouseLeave += (_, _) => { if (!scrubbing) SetProgressThickness(5); };
        stack.Children.Add(progress);

        var times = new Grid { Margin = new Thickness(0, 3, 0, 0) };
        times.Children.Add(elapsedText);
        times.Children.Add(remainingText);
        stack.Children.Add(times);

        var buttons = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        buttons.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        buttons.Children.Add(sourceText);
        var transport = new StackPanel { Orientation = Orientation.Horizontal };
        previousButton.Margin = new Thickness(0, 0, 18, 0);
        nextButton.Margin = new Thickness(18, 0, 0, 0);
        transport.Children.Add(previousButton);
        transport.Children.Add(playButton);
        transport.Children.Add(nextButton);
        Grid.SetColumn(transport, 1);
        buttons.Children.Add(transport);
        lyricsCardButton.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetColumn(lyricsCardButton, 2);
        buttons.Children.Add(lyricsCardButton);
        stack.Children.Add(buttons);
        controls.Child = stack;

        previousButton.Click += () => media.Previous();
        playButton.Click += () => media.PlayPause();
        nextButton.Click += () => media.Next();
        lyricsCardButton.Click += ToggleLyrics;
    }

    private void BuildToolbar()
    {
        offsetLabel.Child = offsetText;
        foreach (var element in new FrameworkElement[] { lyricsButton, laterButton, offsetLabel, earlierButton, positionButton, backgroundButton, hideButton, closeButton })
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
        backgroundButton.Click += () =>
        {
            settings.BackgroundStyle = settings.BackgroundStyle switch
            {
                BackgroundStyle.Blurred => BackgroundStyle.Artwork,
                BackgroundStyle.Artwork => BackgroundStyle.Wallpaper,
                _ => BackgroundStyle.Blurred,
            };
        };
        hideButton.Click += () => { collapsed = !collapsed; ApplyCollapsed(); UpdateChrome(); };
        closeButton.Click += Dismiss;
    }

    private void BuildUnlockPill()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(unlockIcon);
        row.Children.Add(unlockText);
        unlockPill.Child = row;
        unlockPill.Margin = new Thickness(0, 0, 0, 28);
        unlockPill.MouseLeftButtonUp += (_, e) => { Dismiss(); e.Handled = true; };
    }

    // MARK: Public API

    /// Entrance: the background zooms in out of a blur, the widget rises into place.
    public void PlayEntrance()
    {
        lyrics.ReloadIfNeeded();
        collapsed = false;
        ApplyCollapsed(false);
        lyricsChosenAutomatically = false;
        bool hasLyrics = lyrics.State is LyricsState.Loaded or LyricsState.Loading;
        SetShowsLyrics(settings.LyricsEnabled && settings.OpenWithLyrics && hasLyrics && media.HasTrack, animated: false);
        lyricsChosenAutomatically = showsLyrics;
        Relayout(false);
        lyricsPanel.Reveal();
        clockTimer.Start();
        UpdateClock();
        lastTick = DateTime.UtcNow;

        bool lite = settings.LiteMode;
        background.Opacity = 0;
        Glass.Animate(background, OpacityProperty, 1, lite ? 0.25 : 0.55);
        if (!lite)
        {
            backgroundScale.ScaleX = backgroundScale.ScaleY = 1.12;
            Glass.Animate(backgroundScale, ScaleTransform.ScaleXProperty, 1, 0.7);
            Glass.Animate(backgroundScale, ScaleTransform.ScaleYProperty, 1, 0.7);
        }
        clock.Opacity = 0;
        Glass.Animate(clock, OpacityProperty, 1, 0.5, delay: 0.1);
        content.Opacity = 0;
        Glass.Animate(content, OpacityProperty, 1, 0.45, delay: 0.08);
        contentOffset.Y = lite ? 0 : 70;
        if (!lite) Glass.Animate(contentOffset, TranslateTransform.YProperty, 0, 0.7, Glass.Spring, 0.08);
        toolbar.Opacity = 0;
        Glass.Animate(toolbar, OpacityProperty, 1, 0.4, delay: 0.25);
        unlockPill.Opacity = 0;
        Glass.Animate(unlockPill, OpacityProperty, 1, 0.4, delay: 0.35);
    }

    /// Exit (preview): everything fades away. Calls `done` when finished.
    public void PlayExit(Action done)
    {
        clockTimer.Stop();
        var fade = new DoubleAnimation(0, TimeSpan.FromSeconds(settings.LiteMode ? 0.12 : 0.25));
        fade.Completed += (_, _) => done();
        BeginAnimation(OpacityProperty, fade);
    }

    public void ResetOpacity()
    {
        BeginAnimation(OpacityProperty, null);
        Opacity = 1;
    }

    public void Stop() => clockTimer.Stop();

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
            case Key.Up:
                lyrics.Nudge(LyricsController.OffsetStep);
                return true;
            case Key.Down:
                lyrics.Nudge(-LyricsController.OffsetStep);
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
        SetShowsLyrics(withLyrics && media.HasTrack, animated: false);
        foreach (var element in new UIElement[] { background, clock, content, toolbar, unlockPill })
        {
            element.BeginAnimation(OpacityProperty, null);
            element.Opacity = 1;
        }
        backgroundScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        backgroundScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        backgroundScale.ScaleX = backgroundScale.ScaleY = 1;
        contentOffset.BeginAnimation(TranslateTransform.YProperty, null);
        contentOffset.Y = 0;
        artworkScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        artworkScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        artworkScale.ScaleX = artworkScale.ScaleY = media.IsPlaying ? 1 : 0.86;
        UpdateClock();
    }

    /// Per-frame update: progress bar, lyrics, play/pause icon.
    /// `step` overrides the frame time (self-test).
    public void Tick(double? step = null)
    {
        var now = DateTime.UtcNow;
        double dt = step ?? Math.Clamp((now - lastTick).TotalSeconds, 0, 0.1);
        lastTick = now;
        double position = media.Position(now);
        double duration = media.Duration;

        double fraction = scrubbing ? scrubFraction : duration > 0 ? Math.Clamp(position / duration, 0, 1) : 0;
        progressFill.Width = Math.Max(0, progress.ActualWidth * fraction);
        double shown = scrubbing ? scrubFraction * duration : position;
        int second = (int)shown;
        if (second != shownSecond)
        {
            shownSecond = second;
            elapsedText.Text = Format(shown);
            remainingText.Text = duration > 0 ? "-" + Format(Math.Max(0, duration - shown)) : "LIVE";
        }

        if (showsLyrics)
            lyricsPanel.Tick(position + settings.LyricsLead + lyrics.SongOffset, dt, media.IsPlaying);
    }

    private void Dismiss()
    {
        if (presentation == Presentation.Preview) CloseRequested?.Invoke();
        else UnlockRequested?.Invoke();
    }

    // MARK: Reacting to changes

    private void OnTrackChanged()
    {
        UpdateTrack();
        Relayout(true);
    }

    private void OnArtworkChanged()
    {
        ApplyTheme();
        UpdateArtwork();
    }

    private void OnPlaybackChanged()
    {
        playButton.Icon = media.IsPlaying ? Icons.Pause : Icons.Play;
        // Like the iPhone: the artwork shrinks a little while paused.
        double target = media.IsPlaying ? 1 : 0.86;
        Glass.Animate(artworkScale, ScaleTransform.ScaleXProperty, target, 0.55, Glass.Spring);
        Glass.Animate(artworkScale, ScaleTransform.ScaleYProperty, target, 0.55, Glass.Spring);
    }

    private void OnLyricsChanged()
    {
        lyricsPanel.SetContent(lyrics.State, lyrics.Content, lyrics.Revision);
        if (lyricsChosenAutomatically && showsLyrics && lyrics.State is LyricsState.NotFound or LyricsState.Failed or LyricsState.Idle)
        {
            lyricsChosenAutomatically = false;
            SetShowsLyrics(false, animated: true);
        }
        UpdateChrome();
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppSettings.ArtworkScale):
            case nameof(AppSettings.PlayerHeight):
            case nameof(AppSettings.LyricsOffset):
            case nameof(AppSettings.LyricsPosition):
                Relayout(true);
                UpdateChrome();
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
                UpdateChrome();
                break;
        }
    }

    // MARK: Content

    private void UpdateTrack()
    {
        titleText.Text = media.Track?.Title ?? "Nothing playing";
        artistText.Text = media.Track?.Artist ?? "Start a song in Spotify, Apple Music or your browser";
        sourceText.Text = media.SourceName ?? "";
        playButton.Icon = media.IsPlaying ? Icons.Pause : Icons.Play;
        artworkScale.ScaleX = artworkScale.ScaleY = media.IsPlaying || !media.HasTrack ? 1 : 0.86;
        shownSecond = -1;
        UpdateArtwork();
        UpdateChrome();
    }

    private void UpdateArtwork()
    {
        if (media.Artwork != null)
        {
            var brush = new ImageBrush(media.Artwork) { Stretch = Stretch.UniformToFill };
            RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.HighQuality);
            artwork.Background = Glass.Frozen(brush);
            artworkPlaceholder.Visibility = Visibility.Collapsed;
        }
        else
        {
            var palette = media.Palette;
            artwork.Background = Glass.Frozen(new LinearGradientBrush(palette.Primary, palette.Secondary, 45));
            artworkPlaceholder.Visibility = Visibility.Visible;
        }
    }

    private void ToggleLyrics()
    {
        if (!media.HasTrack) return;
        lyricsChosenAutomatically = false;
        SetShowsLyrics(!showsLyrics, animated: true);
    }

    private void SetShowsLyrics(bool value, bool animated)
    {
        showsLyrics = value;
        lyricsHost.IsHitTestVisible = value;
        if (value) lyricsPanel.Reveal();
        if (animated) Glass.Animate(lyricsHost, OpacityProperty, value ? 1 : 0, 0.35);
        else { lyricsHost.BeginAnimation(OpacityProperty, null); lyricsHost.Opacity = value ? 1 : 0; }
        Relayout(animated);
        UpdateChrome();
    }

    private void ApplyCollapsed(bool animated = true)
    {
        double target = collapsed ? 0 : 1;
        content.IsHitTestVisible = !collapsed;
        if (animated) Glass.Animate(content, OpacityProperty, target, 0.3);
        else { content.BeginAnimation(OpacityProperty, null); content.Opacity = target; }
    }

    /// Toolbar, unlock pill and other state-dependent bits.
    private void UpdateChrome()
    {
        var level = settings.GlassLevel;
        bool light = IsLight, lite = settings.LiteMode;
        lyricsButton.ApplyLook(level, light, lite, isOn: showsLyrics);
        lyricsButton.IsEnabled = media.HasTrack;
        lyricsButton.Opacity = media.HasTrack ? 1 : 0.45;
        var lyricsExtras = showsLyrics ? Visibility.Visible : Visibility.Collapsed;
        laterButton.Visibility = earlierButton.Visibility = positionButton.Visibility = lyricsExtras;
        offsetLabel.Visibility = showsLyrics && Math.Abs(lyrics.SongOffset) > 0.001 ? Visibility.Visible : Visibility.Collapsed;
        offsetText.Text = lyrics.SongOffset.ToString("+0.00;-0.00", CultureInfo.InvariantCulture) + " s";
        Glass.Apply(offsetLabel, level, light, true);
        laterButton.ApplyLook(level, light, lite);
        earlierButton.ApplyLook(level, light, lite);
        positionButton.ApplyLook(level, light, lite);
        positionButton.Icon = settings.LyricsPosition switch
        {
            LyricsPosition.Left => Icons.DockLeft,
            LyricsPosition.Center => Icons.Center,
            _ => Icons.DockRight,
        };
        positionButton.ToolTip = $"Lyrics position: {settings.LyricsPosition}";
        backgroundButton.ApplyLook(level, light, lite, isOn: settings.BackgroundStyle != BackgroundStyle.Blurred);
        backgroundButton.ToolTip = $"Background: {settings.BackgroundStyle}";
        hideButton.ApplyLook(level, light, lite, isOn: collapsed);
        hideButton.Icon = collapsed ? Icons.Hide : Icons.View;
        hideButton.ToolTip = collapsed ? "Show widget" : "Hide widget";
        closeButton.ApplyLook(level, light, lite);
        closeButton.Visibility = presentation == Presentation.Preview ? Visibility.Visible : Visibility.Collapsed;
        lyricsCardButton.Icon = Icons.Lyrics;
        lyricsCardButton.Opacity = showsLyrics ? 1 : 0.7;

        Glass.Apply(unlockPill, level, light, lite);
        if (presentation == Presentation.Preview)
        {
            unlockIcon.Text = Icons.Close;
            unlockText.Text = "Preview  ·  Esc or click here to close";
        }
        else if (settings.RequirePassword)
        {
            unlockIcon.Text = Icons.Lock;
            unlockText.Text = "Click here, swipe up or press Enter to unlock";
        }
        else
        {
            unlockIcon.Text = Icons.Unlock;
            unlockText.Text = "Click here, swipe up or press Enter to close";
        }
    }

    private bool IsLight => settings.BackgroundStyle != BackgroundStyle.Wallpaper && media.HasTrack && media.Palette.IsLight;

    /// Colours, fonts and glass from the artwork palette + settings.
    private void ApplyTheme()
    {
        var palette = media.HasTrack ? media.Palette : ArtworkPalette.Fallback;
        var textColor = settings.BackgroundStyle == BackgroundStyle.Wallpaper ? Colors.White : palette.TextColor;
        var text = Glass.Frozen(new SolidColorBrush(textColor));
        TextElement.SetForeground(this, text);
        bool light = IsLight, lite = settings.LiteMode;
        var level = settings.GlassLevel;

        background.Update(media, settings.BackgroundStyle, lite);

        dateText.FontFamily = timeText.FontFamily = Glass.Family(settings.ClockFont);
        dateText.FontWeight = FontWeights.SemiBold;
        timeText.FontWeight = Glass.Weight(settings.ClockWeight);
        clock.Effect = lite ? null : new DropShadowEffect { BlurRadius = 18, ShadowDepth = 2, Opacity = 0.18, Direction = 270 };
        UpdateClockSize();

        Glass.Apply(controls, level, light, lite);
        progressTrack.Background = Glass.Frozen(new SolidColorBrush(Color.FromArgb(56, textColor.R, textColor.G, textColor.B)));
        progressFill.Background = Glass.Frozen(new SolidColorBrush(Color.FromArgb(220, textColor.R, textColor.G, textColor.B)));
        foreach (var b in new[] { previousButton, playButton, nextButton, lyricsCardButton }) b.ApplyLook(level, light, lite);

        bool mega = level == GlassLevel.Mega;
        artworkFrame.Padding = new Thickness(mega ? 8 : 0);
        if (mega) Glass.Apply(artworkFrame, level, light, lite);
        else
        {
            artworkFrame.Background = Brushes.Transparent;
            artworkFrame.BorderThickness = new Thickness(0);
            artworkFrame.Effect = lite ? null : new DropShadowEffect { BlurRadius = 40, ShadowDepth = 12, Direction = 270, Opacity = 0.35 };
        }
        artwork.Effect = null;
        if (mega) Glass.Apply(lyricsHost, level, light, lite);
        else { lyricsHost.Background = Brushes.Transparent; lyricsHost.BorderThickness = new Thickness(0); lyricsHost.Effect = null; }
        lyricsHost.Padding = new Thickness(mega ? 14 : 0);

        lyricsPanel.TextColor = textColor;
        lyricsPanel.Lite = lite;
        lyricsPanel.WordByWord = settings.EffectiveWordByWord;
        lyricsPanel.TextScale = settings.LyricsScale;
        lyricsPanel.LyricsFont = Glass.Family(settings.LyricsFont);
        lyricsPanel.LyricsWeight = Glass.Weight(settings.LyricsWeight);
        lyricsPanel.ApplyTextColor();
        lyricsPanel.InvalidateLines();
        UpdateChrome();
    }

    // MARK: Clock

    private void UpdateClock()
    {
        var now = DateTime.Now;
        var culture = CultureInfo.CurrentCulture;
        bool twelveHour = culture.DateTimeFormat.ShortTimePattern.Contains('h');
        var time = now.ToString(twelveHour ? "h:mm" : "H:mm", culture);
        if (timeText.Text != time) timeText.Text = time;
        var date = culture.TextInfo.ToTitleCase(now.ToString("dddd, d MMMM", culture));
        if (dateText.Text != date) dateText.Text = date;
    }

    private void UpdateClockSize()
    {
        double h = ActualHeight > 0 ? ActualHeight : 1080;
        double size = Math.Min(h * 0.12, 150);
        timeText.FontSize = size;
        timeText.Margin = new Thickness(0, -size * 0.12, 0, 0);
        dateText.FontSize = size * 0.15;
        clock.Margin = new Thickness(0, h * 0.06, 0, 0);
        clock.VerticalAlignment = VerticalAlignment.Top;
    }

    // MARK: Layout
    //
    // Three independent settings: artwork size, player height and lyrics
    // position — "left = less / lower, right = more / higher".

    private const double ControlsHeight = 168;

    private void Relayout(bool animated)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        double top = h * 0.25;
        double bottom = h * (0.90 - 0.30 * settings.PlayerHeight);
        double limit = h - 86; // keep clear of the unlock pill
        double lyricsTop = h * (0.20 + 0.16 * settings.LyricsOffset);
        double lyricsHeight = Math.Min(h * 0.6, limit - lyricsTop);
        double scale = settings.ArtworkScale;

        double BlockTop(double height) => Math.Max(top, Math.Min(bottom, limit) - height);

        if (!showsLyrics || !media.HasTrack)
        {
            double maxArt = Math.Min(w * 0.5, limit - top - ControlsHeight - 20);
            double art = Math.Clamp(Math.Min(h * 0.26, w * 0.22) * scale, 90, Math.Max(90, maxArt));
            double cw = Math.Max(art * 0.95, 340);
            double blockTop = BlockTop(art + 20 + ControlsHeight);
            Place(artworkFrame, (w - art) / 2, blockTop, art, art, animated);
            Place(controls, (w - cw) / 2, blockTop + art + 20, cw, ControlsHeight, animated);
            Place(lyricsHost, w * 0.25, lyricsTop, w * 0.5, lyricsHeight, animated);
            lyricsPanel.SetAlignment(settings.LyricsPosition == LyricsPosition.Center);
        }
        else if (settings.LyricsPosition is LyricsPosition.Right or LyricsPosition.Left)
        {
            double half = w / 2;
            bool right = settings.LyricsPosition == LyricsPosition.Right;
            double maxArt = Math.Min(half * 0.88, limit - top - ControlsHeight - 18);
            double art = Math.Clamp(Math.Min(h * 0.26, w * 0.17) * scale, 90, Math.Max(90, maxArt));
            double cw = Math.Max(Math.Min(art, half * 0.88), 340);
            double blockTop = BlockTop(art + 18 + ControlsHeight);
            double centerX = right ? half / 2 : half + half / 2;
            Place(artworkFrame, centerX - art / 2, blockTop, art, art, animated);
            Place(controls, centerX - cw / 2, blockTop + art + 18, cw, ControlsHeight, animated);
            double lw = half * 0.82;
            Place(lyricsHost, right ? half + half * 0.02 : half * 0.16, lyricsTop, lw, lyricsHeight, animated);
            lyricsPanel.SetAlignment(false);
        }
        else
        {
            const double rowHeight = 150;
            double textHeight = Math.Max(160, lyricsHeight - rowHeight - 16);
            double rowTop = Math.Min(Math.Max(lyricsTop + textHeight + 16, bottom - rowHeight), limit - rowHeight);
            double lw = Math.Min(w * 0.62, 900);
            Place(lyricsHost, (w - lw) / 2, lyricsTop, lw, textHeight, animated);
            double art = Math.Min(76 * scale, rowHeight);
            double rowWidth = art + 16 + 340;
            double x = (w - rowWidth) / 2;
            Place(artworkFrame, x, rowTop + (ControlsHeight - art) / 2 - 9, art, art, animated);
            Place(controls, x + art + 16, rowTop - 9, 340, ControlsHeight, animated);
            lyricsPanel.SetAlignment(true);
        }
        artwork.CornerRadius = new CornerRadius(Math.Max(8, artworkFrame.Width * 0.06));
        artworkFrame.CornerRadius = new CornerRadius(Math.Max(8, artworkFrame.Width * 0.06) + artworkFrame.Padding.Left);
        artworkPlaceholder.FontSize = Math.Max(24, artworkFrame.Width * 0.3);
        artworkFrame.ToolTip = showsLyrics ? "Back to player" : "Show lyrics";
    }

    private static void Place(FrameworkElement element, double x, double y, double width, double height, bool animated)
    {
        if (!animated || double.IsNaN(element.Width) || element.Width <= 0)
        {
            foreach (var p in new[] { Canvas.LeftProperty, Canvas.TopProperty, WidthProperty, HeightProperty })
                element.BeginAnimation(p, null);
            Canvas.SetLeft(element, x);
            Canvas.SetTop(element, y);
            element.Width = width;
            element.Height = height;
            return;
        }
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        void Go(DependencyProperty p, double to)
        {
            // From = what's on screen now (possibly mid-animation).
            double from = (double)element.GetValue(p);
            if (double.IsNaN(from) || Math.Abs(from - to) < 0.5)
            {
                element.BeginAnimation(p, null);
                element.SetValue(p, to);
                return;
            }
            element.BeginAnimation(p, new DoubleAnimation(from, to, TimeSpan.FromSeconds(0.5)) { EasingFunction = ease });
            element.SetValue(p, to); // base value for when the animation is cleared
        }
        Go(Canvas.LeftProperty, x);
        Go(Canvas.TopProperty, y);
        Go(WidthProperty, width);
        Go(HeightProperty, height);
    }

    // MARK: Progress bar

    private void Scrub(MouseEventArgs e)
    {
        if (progress.ActualWidth <= 0) return;
        scrubFraction = Math.Clamp(e.GetPosition(progress).X / progress.ActualWidth, 0, 1);
    }

    private void SetProgressThickness(double value)
    {
        progressTrack.Height = progressFill.Height = value;
        progressTrack.CornerRadius = progressFill.CornerRadius = new CornerRadius(value / 2);
    }

    public static string Format(double seconds)
    {
        if (!double.IsFinite(seconds)) return "0:00";
        int total = (int)Math.Floor(seconds);
        int hours = total / 3600, minutes = total % 3600 / 60, secs = total % 60;
        return hours > 0 ? $"{hours}:{minutes:00}:{secs:00}" : $"{minutes}:{secs:00}";
    }
}
