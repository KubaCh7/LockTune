using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using LockTune.Lyrics;

namespace LockTune.UI;

/// Apple Music-style lyrics: the current line near the top, the following
/// lines springing into place one after another (the "wave"), sung words
/// lighting up one by one. Plain lyrics scroll; messages otherwise.
public sealed class LyricsPanel : Grid
{
    private sealed class LineVisual
    {
        public required LyricLine Line;
        public required Border Box;
        public required TextBlock Text;
        public required ScaleTransform Scale;
        public List<Run> Runs = new();
        public SolidColorBrush[] RunBrushes = Array.Empty<SolidColorBrush>();
        public double Y, Height, Opacity, ScaleValue = 1, Blur = -1;
        public bool WasActive;
    }

    private readonly Canvas canvas = new() { ClipToBounds = true };
    private readonly ScrollViewer plainScroll = new()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        Visibility = Visibility.Collapsed,
    };
    private readonly TextBlock message = new()
    {
        FontSize = 22, FontWeight = FontWeights.SemiBold, Opacity = 0.6, TextWrapping = TextWrapping.Wrap,
        VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed,
    };
    private readonly List<LineVisual> lines = new();
    private IReadOnlyList<LyricLine> syncedLines = Array.Empty<LyricLine>();
    private double[] tops = Array.Empty<double>();
    private double laidOutWidth = -1;
    private double fontSize = 30;
    private object? contentKey;
    private double scrollOffset;
    private DateTime lastScroll = DateTime.MinValue;
    private int hovered = -1;

    public bool Centered { get; private set; }
    public bool WordByWord { get; set; } = true;
    public bool Lite { get; set; }
    public double TextScale { get; set; } = 1;
    public FontFamily LyricsFont { get; set; } = Glass.Family("Segoe UI Variable Display");
    public FontWeight LyricsWeight { get; set; } = FontWeights.Bold;
    public Color TextColor { get; set; } = Colors.White;
    public event Action<double>? SeekRequested;

    public LyricsPanel()
    {
        Children.Add(canvas);
        Children.Add(plainScroll);
        Children.Add(message);
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
        mask.GradientStops.Add(new GradientStop(Colors.Black, 0.1));
        mask.GradientStops.Add(new GradientStop(Colors.Black, 0.86));
        mask.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
        OpacityMask = Glass.Frozen(mask);
    }

    public void SetAlignment(bool centered)
    {
        if (centered == Centered) return;
        Centered = centered;
        foreach (var l in lines) ApplyAlignment(l);
        laidOutWidth = -1;
        message.TextAlignment = centered ? TextAlignment.Center : TextAlignment.Left;
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
                ShowMessage("♪  Instrumental");
                break;
            default:
                ShowMessage(state switch
                {
                    LyricsState.Loading => "• • •",
                    LyricsState.NotFound => "No lyrics found for this song",
                    LyricsState.Failed => "Couldn't load lyrics",
                    _ => "Lyrics are turned off",
                });
                break;
        }
    }

    private void ShowMessage(string text)
    {
        message.Text = text;
        message.Foreground = new SolidColorBrush(TextColor);
        message.FontFamily = LyricsFont;
        message.TextAlignment = Centered ? TextAlignment.Center : TextAlignment.Left;
        message.Visibility = Visibility.Visible;
    }

    private void ShowPlain(IReadOnlyList<string> text)
    {
        var stack = new StackPanel { Margin = new Thickness(12, 60, 12, 60) };
        stack.Children.Add(new TextBlock
        {
            Text = "LYRICS AREN'T SYNCED FOR THIS SONG", FontSize = 12, FontWeight = FontWeights.SemiBold, Opacity = 0.5,
            Margin = new Thickness(0, 0, 0, 14), Foreground = new SolidColorBrush(TextColor),
            TextAlignment = Centered ? TextAlignment.Center : TextAlignment.Left,
        });
        foreach (var line in text)
            stack.Children.Add(new TextBlock
            {
                Text = line.Length == 0 ? " " : line, FontSize = 26 * TextScale, FontFamily = LyricsFont,
                FontWeight = LyricsWeight, Opacity = 0.85, TextWrapping = TextWrapping.Wrap,
                Foreground = new SolidColorBrush(TextColor), Margin = new Thickness(0, 0, 0, 10),
                TextAlignment = Centered ? TextAlignment.Center : TextAlignment.Left,
            });
        plainScroll.Content = stack;
        plainScroll.ScrollToTop();
        plainScroll.Visibility = Visibility.Visible;
    }

    private LineVisual MakeLine(LyricLine line)
    {
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(TextColor) };
        var scale = new ScaleTransform(1, 1);
        var box = new Border
        {
            Child = text,
            Padding = new Thickness(12, 6, 12, 6),
            CornerRadius = new CornerRadius(14),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            RenderTransform = scale,
            Opacity = 0,
        };
        var visual = new LineVisual { Line = line, Box = box, Text = text, Scale = scale };
        if (line.IsInstrumental)
        {
            text.Text = "•  •  •";
        }
        else
        {
            foreach (var word in line.Words)
            {
                var brush = new SolidColorBrush(TextColor);
                var run = new Run(word.Text) { Foreground = brush };
                visual.Runs.Add(run);
                text.Inlines.Add(run);
            }
            visual.RunBrushes = visual.Runs.ConvertAll(r => (SolidColorBrush)r.Foreground).ToArray();
        }
        int index = line.Id;
        box.MouseEnter += (_, _) => { hovered = index; box.Background = new SolidColorBrush(Color.FromArgb(20, 255, 255, 255)); };
        box.MouseLeave += (_, _) => { if (hovered == index) hovered = -1; box.Background = Brushes.Transparent; };
        box.MouseLeftButtonUp += (_, e) => { SeekRequested?.Invoke(line.Time); e.Handled = true; };
        ApplyAlignment(visual);
        canvas.Children.Add(box);
        return visual;
    }

    private void ApplyAlignment(LineVisual l)
    {
        l.Text.TextAlignment = Centered ? TextAlignment.Center : TextAlignment.Left;
        l.Box.RenderTransformOrigin = Centered ? new Point(0.5, 0.5) : new Point(0, 0.5);
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
            l.Text.FontFamily = LyricsFont;
            l.Text.FontWeight = LyricsWeight;
            l.Text.FontSize = l.Line.IsInstrumental ? fontSize * 0.8 : fontSize;
            l.Text.LineHeight = fontSize * 1.18;
            l.Box.Width = width;
            l.Box.Measure(new Size(width, double.PositiveInfinity));
            l.Height = l.Box.DesiredSize.Height;
            tops[i] = y;
            y += l.Height + spacing - 12; // the box padding already adds 12
        }
    }

    public void InvalidateLines() => laidOutWidth = -1;

    /// Called every frame with the (lead-adjusted) playback time.
    public void Tick(double time, double dt, bool isPlaying)
    {
        if (lines.Count == 0) return;
        if (Math.Abs(laidOutWidth - ActualWidth) > 0.5) LayoutLines();
        if (tops.Length != lines.Count) return;

        int active = LrcParser.ActiveIndex(syncedLines, time);
        int focus = Math.Clamp(active, 0, lines.Count - 1);
        double anchor = ActualHeight * (Centered ? 0.3 : 0.16);
        bool browsing = (DateTime.UtcNow - lastScroll).TotalSeconds < 3;
        if (!browsing && Math.Abs(scrollOffset) > 0.5) scrollOffset *= Math.Exp(-dt * 6);
        else if (!browsing) scrollOffset = 0;
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
            double k = Lite ? 14 : below > 0 ? 9.5 - Math.Min(below, 10) * 0.45 : 9.5;
            if (l.Opacity <= 0.001 && l.Y == 0) l.Y = target + 36; // entrance: rise into place
            l.Y += (target - l.Y) * (1 - Math.Exp(-dt * k));

            double opacityTarget = isActive ? 1 : browsing ? 0.65 : Math.Max(0.28, 0.6 - (distance - 1) * 0.06);
            l.Opacity += (opacityTarget - l.Opacity) * (1 - Math.Exp(-dt * 8));
            double scaleTarget = isActive ? 1 : 0.94;
            l.ScaleValue += (scaleTarget - l.ScaleValue) * (1 - Math.Exp(-dt * 9));

            bool visible = l.Y + l.Height > -40 && l.Y < ActualHeight + 40;
            l.Box.Visibility = visible ? Visibility.Visible : Visibility.Hidden;
            if (!visible) continue;

            Canvas.SetTop(l.Box, l.Y);
            l.Box.Opacity = l.Opacity;
            l.Scale.ScaleX = l.Scale.ScaleY = l.ScaleValue;

            double blur = Lite || browsing || distance <= 1 ? 0 : Math.Min((distance - 1) * 0.6, 2.5);
            if (Math.Abs(blur - l.Blur) > 0.05)
            {
                l.Blur = blur;
                l.Box.Effect = blur > 0 ? new BlurEffect { Radius = blur, KernelType = KernelType.Gaussian } : null;
            }

            UpdateWords(l, isActive, time, isPlaying);
        }
    }

    private void UpdateWords(LineVisual l, bool isActive, double time, bool isPlaying)
    {
        if (l.Line.IsInstrumental)
        {
            double pulse = isActive && isPlaying ? 0.55 + 0.45 * Math.Sin(time * 3) : 1;
            l.Text.Opacity = pulse;
            return;
        }
        if (isActive && WordByWord && l.RunBrushes.Length > 0)
        {
            var progress = l.Line.WordProgress(time);
            for (int w = 0; w < l.RunBrushes.Length && w < progress.Length; w++)
                l.RunBrushes[w].Color = ArtworkAlpha(0.38 + 0.62 * progress[w]);
            l.WasActive = true;
        }
        else if (l.WasActive || l.RunBrushes.Length > 0 && l.RunBrushes[0].Color.A != 255)
        {
            foreach (var brush in l.RunBrushes) brush.Color = TextColor;
            l.WasActive = false;
        }
    }

    private Color ArtworkAlpha(double alpha) => Color.FromArgb((byte)(Math.Clamp(alpha, 0, 1) * 255), TextColor.R, TextColor.G, TextColor.B);

    /// Re-colours every line (palette change).
    public void ApplyTextColor()
    {
        foreach (var l in lines)
        {
            l.Text.Foreground = new SolidColorBrush(TextColor);
            foreach (var brush in l.RunBrushes) brush.Color = TextColor;
        }
        message.Foreground = new SolidColorBrush(TextColor);
        if (plainScroll.Content is StackPanel stack)
            foreach (var child in stack.Children)
                if (child is TextBlock tb) tb.Foreground = new SolidColorBrush(TextColor);
    }

    /// Restarts the entrance animation (when the lock screen opens).
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
