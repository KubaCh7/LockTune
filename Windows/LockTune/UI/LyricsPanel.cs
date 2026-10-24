using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using LockTune.Core;
using LockTune.Lyrics;

namespace LockTune.UI;

/// Three breathing dots shown during instrumental breaks (InstrumentalDots).
public sealed class InstrumentalDots : StackPanel
{
    private readonly Ellipse[] dots = new Ellipse[3];
    private readonly ScaleTransform scale = new(1, 1);

    public InstrumentalDots(double dotSize)
    {
        Orientation = Orientation.Horizontal;
        RenderTransform = scale;
        RenderTransformOrigin = new Point(0, 0.5);
        for (int k = 0; k < 3; k++)
        {
            dots[k] = new Ellipse { Width = dotSize, Height = dotSize, Margin = new Thickness(0, 0, k < 2 ? dotSize * 0.7 : 0, 0), Opacity = 0.35 };
            dots[k].SetBinding(Shape.FillProperty, new System.Windows.Data.Binding("(TextElement.Foreground)") { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.Self) });
            Children.Add(dots[k]);
        }
    }

    public void Update(double t, bool active)
    {
        for (int k = 0; k < 3; k++)
            dots[k].Opacity = active ? 0.35 + 0.65 * (0.5 + 0.5 * Math.Sin(t * 3 - k * 0.8)) : 0.35;
        scale.ScaleX = scale.ScaleY = active ? 1 + 0.08 * Math.Sin(t * 1.6) : 1;
    }
}

/// Apple Music-style lyrics (LyricsView.swift): the current line near the top,
/// the following lines springing into place one after another (the "wave"),
/// sung words lighting up one by one. Plain lyrics scroll; messages otherwise.
public sealed class LyricsPanel : Grid
{
    private sealed class LineVisual
    {
        public required LyricLine Line;
        /// Positioned with a transform (no layout per frame) and cached on
        /// the GPU, so moving lines cost almost nothing.
        public required Border Box;
        /// Inside the cache: blur is computed once, not every frame.
        public required Border Inner;
        /// Text shadow (over a normal wallpaper), also inside the cache.
        public required Border Shade;
        public required TextBlock Text;
        public required ScaleTransform Scale;
        public required TranslateTransform Move;
        public InstrumentalDots? Dots;
        public SolidColorBrush[] RunBrushes = Array.Empty<SolidColorBrush>();
        public double Y, Height, Opacity, ScaleValue = 0.94, Blur = -1;
        public bool WasActive;
    }

    private readonly Canvas canvas = new() { ClipToBounds = true };
    private readonly ScrollViewer plainScroll = new()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        Visibility = Visibility.Collapsed,
    };
    private readonly StackPanel message = new() { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.6, Visibility = Visibility.Collapsed };
    private readonly Symbol messageIcon = new("quote.bubble", 22, 1.2) { Margin = new Thickness(0, 0, 10, 0) };
    private readonly TextBlock messageText = new() { FontSize = 22, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private readonly InstrumentalDots loadingDots = new(14) { VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
    private readonly List<LineVisual> lines = new();
    private IReadOnlyList<LyricLine> syncedLines = Array.Empty<LyricLine>();
    private double[] tops = Array.Empty<double>();
    private double laidOutWidth = -1;
    private double fontSize = 30;
    private object? contentKey;
    private double scrollOffset;
    private DateTime lastScroll = DateTime.MinValue;

    public bool Centered { get; private set; }
    public bool WordByWord { get; set; } = true;
    public bool Lite { get; set; }
    public double TextScale { get; set; } = 0.85;
    public FontFamily LyricsFont { get; set; } = Glass.Family(FontChoice.SfPro);
    public FontWeight LyricsWeight { get; set; } = FontWeights.Bold;
    public Color TextColor { get; set; } = Colors.White;
    /// Soft shadow under the text (for legibility over a normal wallpaper).
    public bool TextShadow { get; set; }
    public event Action<double>? SeekRequested;

    public LyricsPanel()
    {
        message.Children.Add(messageIcon);
        message.Children.Add(messageText);
        Children.Add(canvas);
        Children.Add(plainScroll);
        Children.Add(message);
        Children.Add(loadingDots);
        Background = Brushes.Transparent;
        SizeChanged += (_, _) => laidOutWidth = -1;
        MouseWheel += (_, e) =>
        {
            if (lines.Count == 0) return;
            scrollOffset += e.Delta * 0.6;
            lastScroll = DateTime.UtcNow;
            e.Handled = true;
        };
        var mask = new LinearGradientBrush { StartPoint = new Point(0.5, 0), EndPoint = new Point(0.5, 1) };
        mask.GradientStops.Add(new GradientStop(Colors.Transparent, 0));
        mask.GradientStops.Add(new GradientStop(Colors.Black, 0.12));
        mask.GradientStops.Add(new GradientStop(Colors.Black, 0.86));
        mask.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
        canvas.OpacityMask = Glass.Frozen(mask);
    }

    public void SetAlignment(bool centered)
    {
        if (centered == Centered) return;
        Centered = centered;
        foreach (var l in lines) ApplyAlignment(l);
        laidOutWidth = -1;
        var h = centered ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        message.HorizontalAlignment = loadingDots.HorizontalAlignment = h;
        if (plainScroll.Content is StackPanel stack)
            foreach (var child in stack.Children)
                if (child is TextBlock tb) tb.TextAlignment = centered ? TextAlignment.Center : TextAlignment.Left;
    }

    /// Shows the lyrics state; rebuilds only when the content changed.
    public void SetContent(LyricsState state, LyricsContent? content, int revision)
    {
        object key = content != null ? (object)(content, revision) : state;
        if (Equals(key, contentKey)) return;
        contentKey = key;

        canvas.Children.Clear();
        lines.Clear();
        syncedLines = Array.Empty<LyricLine>();
        tops = Array.Empty<double>();
        plainScroll.Visibility = Visibility.Collapsed;
        message.Visibility = Visibility.Collapsed;
        loadingDots.Visibility = Visibility.Collapsed;
        scrollOffset = 0;

        switch (content)
        {
            case LyricsContent.Synced synced:
                foreach (var line in synced.Lines) lines.Add(MakeLine(line));
                syncedLines = synced.Lines;
                laidOutWidth = -1;
                break;
            case LyricsContent.Plain plain:
                ShowPlain(plain.Lines);
                break;
            case LyricsContent.Instrumental:
                ShowMessage("Instrumental", "music.note");
                break;
            default:
                switch (state)
                {
                    case LyricsState.Loading:
                        loadingDots.Visibility = Visibility.Visible;
                        break;
                    case LyricsState.NotFound:
                        ShowMessage("No lyrics found for this song", "text.badge.xmark");
                        break;
                    case LyricsState.Failed:
                        ShowMessage("Couldn't load lyrics", "wifi.exclamationmark");
                        break;
                    default:
                        ShowMessage("Lyrics are turned off", "quote.bubble");
                        break;
                }
                break;
        }
    }

    private void ShowMessage(string text, string symbol)
    {
        messageText.Text = text;
        messageIcon.SymbolName = symbol;
        message.Visibility = Visibility.Visible;
    }

    private void ShowPlain(IReadOnlyList<string> text)
    {
        var align = Centered ? TextAlignment.Center : TextAlignment.Left;
        var stack = new StackPanel { Margin = new Thickness(12, 60, 12, 60) };
        stack.Children.Add(new TextBlock
        {
            Text = "LYRICS AREN'T SYNCED FOR THIS SONG", FontSize = 12, FontWeight = FontWeights.SemiBold, Opacity = 0.5,
            FontFamily = Glass.SystemText, Margin = new Thickness(0, 0, 0, 20), TextAlignment = align,
        });
        foreach (var line in text)
            stack.Children.Add(new TextBlock
            {
                Text = line.Length == 0 ? " " : line, FontSize = 26, FontFamily = LyricsFont,
                FontWeight = LyricsWeight, Opacity = 0.85, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12), TextAlignment = align,
            });
        plainScroll.Content = stack;
        plainScroll.ScrollToTop();
        plainScroll.Visibility = Visibility.Visible;
    }

    private LineVisual MakeLine(LyricLine line)
    {
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var scale = new ScaleTransform(0.94, 0.94);
        var move = new TranslateTransform();
        var transforms = new TransformGroup();
        transforms.Children.Add(scale);
        transforms.Children.Add(move);
        var shade = new Border();
        var inner = new Border { Padding = new Thickness(12, 6, 12, 6), Child = shade };
        var box = new Border
        {
            Child = inner,
            CornerRadius = new CornerRadius(14),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            RenderTransform = transforms,
            Opacity = 0,
            CacheMode = new BitmapCache { EnableClearType = false, SnapsToDevicePixels = false },
        };
        var visual = new LineVisual { Line = line, Box = box, Inner = inner, Shade = shade, Text = text, Scale = scale, Move = move };
        if (line.IsInstrumental)
        {
            visual.Dots = new InstrumentalDots(10) { HorizontalAlignment = HorizontalAlignment.Left };
            shade.Child = visual.Dots;
        }
        else
        {
            shade.Child = text;
            var brushes = new List<SolidColorBrush>();
            foreach (var word in line.Words)
            {
                var brush = new SolidColorBrush(TextColor);
                text.Inlines.Add(new Run(word.Text) { Foreground = brush });
                brushes.Add(brush);
            }
            visual.RunBrushes = brushes.ToArray();
        }
        box.MouseEnter += (_, _) => box.Background = new SolidColorBrush(Color.FromArgb(20, TextColor.R, TextColor.G, TextColor.B));
        box.MouseLeave += (_, _) => box.Background = Brushes.Transparent;
        box.MouseLeftButtonUp += (_, e) => { SeekRequested?.Invoke(line.Time); e.Handled = true; };
        ApplyAlignment(visual);
        canvas.Children.Add(box);
        return visual;
    }

    private void ApplyAlignment(LineVisual l)
    {
        l.Text.TextAlignment = Centered ? TextAlignment.Center : TextAlignment.Left;
        l.Box.RenderTransformOrigin = Centered ? new Point(0.5, 0.5) : new Point(0, 0.5);
        if (l.Dots != null) l.Dots.HorizontalAlignment = Centered ? HorizontalAlignment.Center : HorizontalAlignment.Left;
    }

    /// Re-measures lines after a size / font change.
    private void LayoutLines()
    {
        double width = Math.Max(ActualWidth, 50);
        laidOutWidth = width;
        fontSize = Math.Clamp(width * 0.05, 18, 42) * TextScale;
        double spacing = fontSize * 0.3, y = 0;
        tops = new double[lines.Count];
        for (int i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            l.Shade.Effect = TextShadow ? Glass.Shadow(0.45, 10, 0) : null;
            l.Text.FontFamily = LyricsFont;
            l.Text.FontWeight = LyricsWeight;
            l.Text.FontSize = fontSize;
            l.Text.LineHeight = fontSize * 1.2;
            if (l.Dots != null)
            {
                foreach (Ellipse dot in l.Dots.Children)
                {
                    dot.Width = dot.Height = fontSize * 0.32;
                    dot.Margin = new Thickness(0, 0, dot == l.Dots.Children[2] ? 0 : fontSize * 0.32 * 0.7, 0);
                }
                l.Dots.Height = fontSize * 1.1;
            }
            l.Box.Width = width;
            l.Box.Measure(new Size(width, double.PositiveInfinity));
            Canvas.SetTop(l.Box, 0);
            l.Height = l.Box.DesiredSize.Height;
            tops[i] = y;
            y += l.Height + spacing;
        }
    }

    public void InvalidateLines() => laidOutWidth = -1;

    /// Called every frame with the (lead-adjusted) playback time.
    public void Tick(double time, double dt, bool isPlaying)
    {
        double wall = Environment.TickCount64 / 1000.0;
        if (loadingDots.Visibility == Visibility.Visible) loadingDots.Update(wall, true);
        if (lines.Count == 0) return;
        if (Math.Abs(laidOutWidth - ActualWidth) > 0.5) LayoutLines();
        if (tops.Length != lines.Count) return;

        int active = LrcParser.ActiveIndex(syncedLines, time);
        int focus = Math.Clamp(active, 0, lines.Count - 1);
        // Current line near the top so several upcoming lines are visible.
        double anchor = ActualHeight * (Centered ? 0.3 : 0.16);
        bool browsing = (DateTime.UtcNow - lastScroll).TotalSeconds < 3;
        if (!browsing) scrollOffset = Math.Abs(scrollOffset) > 0.5 ? scrollOffset * Math.Exp(-dt * 6) : 0;
        // Manual scrolling, clamped between the first and the last line.
        scrollOffset = Math.Clamp(scrollOffset, tops[focus] - tops[^1], tops[focus]);

        for (int i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            int distance = active < 0 ? i + 1 : Math.Abs(i - active);
            bool isActive = i == active;
            double target = anchor - tops[focus] + scrollOffset + tops[i];

            // Lines below the current one follow a little later (the wave).
            int below = i - focus + 1;
            double k = Lite ? 12 : below > 0 ? 9.5 - Math.Min(below, 10) * 0.45 : 9.5;
            if (l.Opacity <= 0.001 && l.Y == 0) l.Y = target + 36; // entrance: rise into place
            l.Y += (target - l.Y) * (1 - Math.Exp(-dt * k));

            double opacityTarget = isActive ? 1 : browsing ? 0.65 : Math.Max(0.28, 0.6 - (distance - 1) * 0.06);
            l.Opacity += (opacityTarget - l.Opacity) * (1 - Math.Exp(-dt * 8));
            l.ScaleValue += ((isActive ? 1 : 0.94) - l.ScaleValue) * (1 - Math.Exp(-dt * 9));

            bool visible = l.Y + l.Height > -40 && l.Y < ActualHeight + 40;
            var visibility = visible ? Visibility.Visible : Visibility.Hidden;
            if (l.Box.Visibility != visibility) l.Box.Visibility = visibility;
            if (!visible) continue;

            // Only touch properties that actually changed (each write costs a redraw).
            if (Math.Abs(l.Move.Y - l.Y) > 0.05) l.Move.Y = l.Y;
            if (Math.Abs(l.Box.Opacity - l.Opacity) > 0.002) l.Box.Opacity = l.Opacity;
            if (Math.Abs(l.Scale.ScaleX - l.ScaleValue) > 0.0005) l.Scale.ScaleX = l.Scale.ScaleY = l.ScaleValue;

            // Rounded so the cached line is only re-rendered when the blur really changes.
            double blur = Lite || browsing || distance <= 1 || distance > 10 ? 0 : Math.Round(Math.Min((distance - 1) * 0.6, 2.5) * 2) / 2;
            if (blur != l.Blur)
            {
                l.Blur = blur;
                l.Inner.Effect = blur > 0 ? new BlurEffect { Radius = blur * 2, KernelType = KernelType.Gaussian } : null;
            }

            if (l.Dots != null) l.Dots.Update(wall, isActive && isPlaying);
            else UpdateWords(l, isActive, time);
        }
    }

    /// Words already sung are fully lit, the current one fills in, the rest stay dim.
    private void UpdateWords(LineVisual l, bool isActive, double time)
    {
        if (isActive && WordByWord && l.RunBrushes.Length > 0)
        {
            var progress = l.Line.WordProgress(time);
            for (int w = 0; w < l.RunBrushes.Length && w < progress.Length; w++)
            {
                var color = WithAlpha(0.38 + 0.62 * progress[w]);
                if (l.RunBrushes[w].Color != color) l.RunBrushes[w].Color = color;
            }
            l.WasActive = true;
        }
        else if (l.WasActive)
        {
            foreach (var brush in l.RunBrushes) brush.Color = TextColor;
            l.WasActive = false;
        }
    }

    private Color WithAlpha(double alpha) => Color.FromArgb((byte)(Math.Clamp(alpha, 0, 1) * 255), TextColor.R, TextColor.G, TextColor.B);

    /// Re-colours every line (palette change).
    public void ApplyTextColor()
    {
        foreach (var l in lines)
            foreach (var brush in l.RunBrushes) brush.Color = TextColor;
    }

    /// Restarts the entrance animation (lines rise and fade in).
    public void Reveal()
    {
        foreach (var l in lines)
        {
            l.Opacity = 0;
            l.Y = 0;
            l.Box.Opacity = 0;
        }
    }
}
