using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using LockTune.Core;
using LockTune.Media;
using LockTune.Platform;

namespace LockTune.UI;

/// Desktop turntable widget: a deck tinted with the artwork's colours, a
/// spinning vinyl with the album art as its label, a tone arm that rests on
/// the record while playing, and a now-playing card underneath. It stays
/// below every app window (on the wallpaper); drag it anywhere.
public sealed class TurntableWindow : Window
{
    private const double BaseWidth = 250, BaseHeight = 290, InfoHeight = 78;

    private readonly MediaController media;
    private readonly AppSettings settings;
    private readonly Border deck = new() { CornerRadius = new CornerRadius(30), BorderThickness = new Thickness(1) };
    private readonly Canvas deckCanvas = new();
    private readonly Grid record = new();
    private readonly RotateTransform recordRotation = new();
    private readonly Ellipse label = new();
    private readonly Ellipse powerLight = new();
    private readonly Canvas arm = new();
    private readonly RotateTransform armRotation = new(20);
    private readonly StackPanel dots = new() { Orientation = Orientation.Horizontal };
    private readonly Border info = new() { CornerRadius = new CornerRadius(22), BorderThickness = new Thickness(1) };
    private readonly TextBlock title = new() { FontWeight = FontWeights.Bold, TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock artist = new() { FontWeight = FontWeights.Medium, Opacity = 0.7, TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock status = new() { Opacity = 0.55, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly StackPanel root = new() { Margin = new Thickness(10) };
    private DoubleAnimation? spin;
    private AnimationClock? spinClock;
    private Point dragStart;
    private bool dragging;

    public TurntableWindow(MediaController media, AppSettings settings)
    {
        this.media = media;
        this.settings = settings;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        Title = "LockTune Turntable";
        Content = root;
        TextElement.SetForeground(this, Brushes.White);

        root.Children.Add(deck);
        info.Margin = new Thickness(0, 10, 0, 0);
        root.Children.Add(info);
        deck.Child = deckCanvas;

        var infoStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(14, 0, 14, 0) };
        infoStack.Children.Add(title);
        infoStack.Children.Add(artist);
        infoStack.Children.Add(status);
        info.Child = infoStack;

        BuildDeck();

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            Native.AddExStyle(hwnd, Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE, Native.WS_EX_APPWINDOW);
            HwndSource.FromHwnd(hwnd)?.AddHook(KeepAtBottom);
            SendToBottom();
        };
        MouseLeftButtonDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseLeftButtonUp += OnMouseUp;
        LocationChanged += (_, _) =>
        {
            if (!IsVisible) return;
            settings.TurntableLeft = Left;
            settings.TurntableTop = Top;
        };

        var menu = new ContextMenu();
        var hide = new MenuItem { Header = "Hide turntable" };
        hide.Click += (_, _) => settings.TurntableEnabled = false;
        var reset = new MenuItem { Header = "Reset position" };
        reset.Click += (_, _) => ResetPosition();
        menu.Items.Add(reset);
        menu.Items.Add(hide);
        ContextMenu = menu;

        media.TrackChanged += Update;
        media.ArtworkChanged += Update;
        media.PlaybackChanged += Update;
        settings.PropertyChanged += OnSettingsChanged;
        Update();
        ApplyScale();
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppSettings.TurntableScale)) { ApplyScale(); Update(); }
        if (e.PropertyName == nameof(AppSettings.LiteMode)) Update();
    }

    public void ShowWidget()
    {
        if (settings.TurntableLeft is { } left && settings.TurntableTop is { } top && IsOnScreen(left, top))
        {
            Left = left;
            Top = top;
        }
        else ResetPosition(save: false);
        Show();
        SendToBottom();
    }

    public void ResetPosition(bool save = true)
    {
        var area = SystemParameters.WorkArea;
        double scale = settings.TurntableScale;
        Left = area.Right - (BaseWidth * scale + 20) - 40;
        Top = area.Bottom - (BaseHeight * scale + InfoHeight * scale + 40) - 40;
        if (save)
        {
            settings.TurntableLeft = Left;
            settings.TurntableTop = Top;
        }
    }

    private static bool IsOnScreen(double left, double top) =>
        left > SystemParameters.VirtualScreenLeft - 100 && top > SystemParameters.VirtualScreenTop - 100 &&
        left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 60 &&
        top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 60;

    // MARK: Staying on the wallpaper

    private void SendToBottom()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
            Native.SetWindowPos(hwnd, Native.HWND_BOTTOM, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    private IntPtr KeepAtBottom(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_WINDOWPOSCHANGING)
        {
            var pos = System.Runtime.InteropServices.Marshal.PtrToStructure<Native.WINDOWPOS>(lParam);
            pos.hwndInsertAfter = Native.HWND_BOTTOM;
            pos.flags &= ~Native.SWP_NOZORDER;
            System.Runtime.InteropServices.Marshal.StructureToPtr(pos, lParam, false);
        }
        return IntPtr.Zero;
    }

    // MARK: Dragging / clicking

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        dragStart = e.GetPosition(this);
        dragging = false;
        CaptureMouse();
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!IsMouseCaptured) return;
        var p = e.GetPosition(this);
        if (!dragging && (p - dragStart).Length > 3) dragging = true;
        if (dragging)
        {
            Left += p.X - dragStart.X;
            Top += p.Y - dragStart.Y;
        }
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!IsMouseCaptured) return;
        ReleaseMouseCapture();
        if (!dragging && record.IsMouseOver) media.PlayPause();
        dragging = false;
    }

    // MARK: Drawing

    private double S => settings.TurntableScale;

    private void ApplyScale() => BuildDeck();

    private void BuildDeck()
    {
        double s = S, w = BaseWidth * s, h = BaseHeight * s, recordSize = 176 * s;
        deck.Width = w;
        deck.Height = h;
        deck.CornerRadius = new CornerRadius(30 * s);
        info.Width = w;
        info.Height = InfoHeight * s;
        info.CornerRadius = new CornerRadius(22 * s);
        title.FontSize = 14 * s;
        artist.FontSize = 12 * s;
        status.FontSize = 10 * s;
        status.Margin = new Thickness(0, 2 * s, 0, 0);
        deckCanvas.Children.Clear();

        // Power light (on while playing).
        var ring = new Ellipse { Width = 22 * s, Height = 22 * s, Stroke = new SolidColorBrush(Color.FromArgb(140, 255, 255, 255)), StrokeThickness = 2 * s };
        Canvas.SetLeft(ring, 17 * s); Canvas.SetTop(ring, 17 * s);
        powerLight.Width = powerLight.Height = 7 * s;
        Canvas.SetLeft(powerLight, 24.5 * s); Canvas.SetTop(powerLight, 24.5 * s);
        deckCanvas.Children.Add(ring);
        deckCanvas.Children.Add(powerLight);

        // Record: grooves + label (album art) + spindle.
        record.Children.Clear();
        record.Width = record.Height = recordSize;
        record.Cursor = Cursors.Hand;
        record.ToolTip = "Play / Pause";
        record.Children.Add(new Ellipse { Fill = new SolidColorBrush(Color.FromRgb(18, 18, 18)) });
        for (int i = 0; i < 9; i++)
        {
            double inset = recordSize * 0.04 + i * recordSize * 0.028;
            record.Children.Add(new Ellipse
            {
                Margin = new Thickness(inset),
                Stroke = new SolidColorBrush(Color.FromArgb(i % 3 == 0 ? (byte)23 : (byte)10, 255, 255, 255)),
                StrokeThickness = 1,
            });
        }
        label.Width = label.Height = recordSize * 0.46;
        label.Stroke = new SolidColorBrush(Color.FromArgb(64, 0, 0, 0));
        record.Children.Add(label);
        record.Children.Add(new Ellipse { Width = recordSize * 0.035, Height = recordSize * 0.035, Fill = new SolidColorBrush(Color.FromRgb(38, 38, 38)) });
        record.RenderTransformOrigin = new Point(0.5, 0.5);
        record.RenderTransform = recordRotation;
        Canvas.SetLeft(record, w * 0.5 - recordSize / 2);
        Canvas.SetTop(record, h * 0.42 - recordSize / 2);
        deckCanvas.Children.Add(record);

        // Gloss that stays put while the record turns underneath it.
        var gloss = new Ellipse { Width = recordSize, Height = recordSize, IsHitTestVisible = false };
        var glossBrush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
        glossBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0, 255, 255, 255), 0.2));
        glossBrush.GradientStops.Add(new GradientStop(Color.FromArgb(30, 255, 255, 255), 0.45));
        glossBrush.GradientStops.Add(new GradientStop(Color.FromArgb(0, 255, 255, 255), 0.6));
        gloss.Fill = glossBrush;
        Canvas.SetLeft(gloss, w * 0.5 - recordSize / 2);
        Canvas.SetTop(gloss, h * 0.42 - recordSize / 2);
        deckCanvas.Children.Add(gloss);

        // Colour dots from the palette.
        Canvas.SetLeft(dots, 34 * s);
        Canvas.SetTop(dots, h - 46 * s);
        deckCanvas.Children.Add(dots);

        // Tone arm pivoting from the bottom-right corner.
        arm.Children.Clear();
        var pivot = new Point(w - 46 * s, h - 50 * s);
        var tip = new Point(pivot.X - 72 * s, pivot.Y - 128 * s);
        double angle = Math.Atan2(tip.Y - pivot.Y, tip.X - pivot.X) * 180 / Math.PI + 90;
        var armBrush = new LinearGradientBrush(Color.FromRgb(235, 235, 235), Color.FromRgb(178, 178, 178), 90);
        arm.Children.Add(new Line { X1 = pivot.X, Y1 = pivot.Y, X2 = tip.X, Y2 = tip.Y, Stroke = armBrush, StrokeThickness = 5 * s, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
        var head = new Rectangle { Width = 14 * s, Height = 24 * s, RadiusX = 3 * s, RadiusY = 3 * s, Fill = new SolidColorBrush(Color.FromRgb(230, 230, 230)), RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new RotateTransform(angle) };
        Canvas.SetLeft(head, tip.X - 7 * s); Canvas.SetTop(head, tip.Y - 12 * s);
        arm.Children.Add(head);
        var weight = new Rectangle { Width = 16 * s, Height = 22 * s, RadiusX = 3 * s, RadiusY = 3 * s, Fill = new SolidColorBrush(Color.FromRgb(217, 217, 217)), RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new RotateTransform(angle) };
        Canvas.SetLeft(weight, pivot.X + (pivot.X - tip.X) * 0.12 - 8 * s); Canvas.SetTop(weight, pivot.Y + (pivot.Y - tip.Y) * 0.12 - 11 * s);
        arm.Children.Add(weight);
        arm.Width = w;
        arm.Height = h;
        arm.IsHitTestVisible = false;
        arm.RenderTransformOrigin = new Point(pivot.X / w, pivot.Y / h);
        arm.RenderTransform = armRotation;
        arm.Effect = new DropShadowEffect { BlurRadius = 6, ShadowDepth = 4, Opacity = 0.35, Direction = 315 };
        deckCanvas.Children.Add(arm);

        var pivotBase = new Grid { Width = 46 * s, Height = 46 * s, IsHitTestVisible = false };
        pivotBase.Children.Add(new Ellipse { Fill = new SolidColorBrush(Color.FromArgb(20, 255, 255, 255)) });
        pivotBase.Children.Add(new Ellipse { Width = 14 * s, Height = 14 * s, Fill = new SolidColorBrush(Color.FromRgb(204, 204, 204)) });
        Canvas.SetLeft(pivotBase, pivot.X - 23 * s); Canvas.SetTop(pivotBase, pivot.Y - 23 * s);
        deckCanvas.Children.Add(pivotBase);
        spin = null;
    }

    private void Update()
    {
        var palette = media.HasTrack ? media.Palette : ArtworkPalette.Fallback;
        var top = ArtworkPalette.Mix(palette.Primary, Colors.Black, 0.42);
        var bottom = ArtworkPalette.Mix(palette.Primary, Colors.Black, 0.62);
        deck.Background = new LinearGradientBrush(top, bottom, 90);
        deck.BorderBrush = new SolidColorBrush(Color.FromArgb(20, 255, 255, 255));
        info.Background = new SolidColorBrush(bottom);
        info.BorderBrush = deck.BorderBrush;
        bool lite = settings.LiteMode;
        deck.Effect = lite ? null : new DropShadowEffect { BlurRadius = 18, ShadowDepth = 8, Direction = 270, Opacity = 0.35 };
        info.Effect = lite ? null : new DropShadowEffect { BlurRadius = 10, ShadowDepth = 5, Direction = 270, Opacity = 0.25 };

        powerLight.Fill = new SolidColorBrush(Color.FromArgb(media.IsPlaying ? (byte)230 : (byte)64, 255, 255, 255));
        label.Fill = media.Artwork != null
            ? new ImageBrush(media.Artwork) { Stretch = Stretch.UniformToFill }
            : new SolidColorBrush(Color.FromRgb(235, 235, 235));

        dots.Children.Clear();
        foreach (var c in new[] { palette.Primary, palette.Secondary, palette.Tertiary, ArtworkPalette.Mix(palette.Primary, Colors.White, 0.6) })
            dots.Children.Add(new Ellipse
            {
                Width = 11 * S, Height = 11 * S, Margin = new Thickness(0, 0, 8 * S, 0), Fill = new SolidColorBrush(c),
                Stroke = new SolidColorBrush(Color.FromArgb(64, 255, 255, 255)), StrokeThickness = 0.5,
            });

        title.Text = media.Track?.Title ?? "Nothing playing";
        artist.Text = media.Track?.Artist ?? "Start a song in Spotify or Apple Music";
        status.Text = media.HasTrack ? (media.IsPlaying ? $"Playing on {media.SourceName}" : "Paused") : "Idle";

        // Arm on the record while playing, swung away when paused.
        armRotation.BeginAnimation(RotateTransform.AngleProperty,
            new DoubleAnimation(media.IsPlaying ? 0 : 20, TimeSpan.FromSeconds(0.9)) { EasingFunction = new BackEase { Amplitude = 0.3, EasingMode = EasingMode.EaseOut } });
        SetSpinning(media.IsPlaying);
    }

    /// 33⅓ rpm; stops where it is when paused.
    private void SetSpinning(bool playing)
    {
        if (spin == null)
        {
            spin = new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.8)) { RepeatBehavior = RepeatBehavior.Forever };
            spinClock = spin.CreateClock();
            recordRotation.ApplyAnimationClock(RotateTransform.AngleProperty, spinClock);
            if (!playing) spinClock.Controller?.Pause();
            return;
        }
        if (spinClock?.Controller == null) return;
        if (playing) spinClock.Controller.Resume();
        else spinClock.Controller.Pause();
    }
}
