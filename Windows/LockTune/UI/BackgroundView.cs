using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using LockTune.Core;
using LockTune.Media;

namespace LockTune.UI;

/// Full-screen background built from the artwork (ArtworkBackgroundView.swift):
/// a blurred, saturated, slowly drifting version (Apple Music style) or the
/// artwork as a wallpaper — tinted with the artwork's dominant colours.
public sealed class BackgroundView : Grid
{
    private readonly Rectangle baseGradient = new();
    private readonly Grid imageHost = new() { ClipToBounds = true };
    private readonly Rectangle tint = new() { IsHitTestVisible = false };
    private readonly Rectangle scrim = new() { IsHitTestVisible = false };
    private readonly Rectangle vignette = new() { IsHitTestVisible = false };
    private (int Token, BackgroundStyle Style, bool Lite) shown = (-1, BackgroundStyle.Blurred, false);

    public BackgroundView()
    {
        ClipToBounds = true;
        Children.Add(baseGradient);
        Children.Add(imageHost);
        Children.Add(tint);
        Children.Add(scrim);
        Children.Add(vignette);
        SizeChanged += (_, _) => Relayout();
    }

    /// Re-renders when the artwork, style or lite mode changed.
    public void Update(MediaController media, BackgroundStyle style, bool lite, bool force = false)
    {
        var palette = media.HasTrack ? media.Palette : ArtworkPalette.Fallback;
        baseGradient.Fill = Glass.Frozen(new LinearGradientBrush(new GradientStopCollection
        {
            new(palette.Primary, 0), new(palette.Secondary, 0.55), new(palette.Tertiary, 1),
        }, new Point(0, 0), new Point(1, 1)));
        var tintBrush = new LinearGradientBrush { StartPoint = new Point(0.5, 0), EndPoint = new Point(0.5, 1) };
        tintBrush.GradientStops.Add(new GradientStop(ArtworkPalette.WithAlpha(palette.Primary, 0.35), 0));
        tintBrush.GradientStops.Add(new GradientStop(Colors.Transparent, 0.5));
        tintBrush.GradientStops.Add(new GradientStop(ArtworkPalette.WithAlpha(palette.Secondary, 0.4), 1));
        tint.Fill = Glass.Frozen(tintBrush);
        bool light = palette.IsLight;
        // Legibility scrim + vignette.
        scrim.Fill = Glass.Frozen(new SolidColorBrush(light ? Color.FromArgb(15, 255, 255, 255)
                                                            : Color.FromArgb(style == BackgroundStyle.Artwork ? (byte)66 : (byte)46, 0, 0, 0)));
        var vignetteBrush = new RadialGradientBrush { GradientOrigin = new Point(0.5, 0.5), RadiusX = 0.75, RadiusY = 0.75 };
        vignetteBrush.GradientStops.Add(new GradientStop(Colors.Transparent, 0.45));
        vignetteBrush.GradientStops.Add(new GradientStop(Color.FromArgb(light ? (byte)20 : (byte)80, 0, 0, 0), 1));
        vignette.Fill = Glass.Frozen(vignetteBrush);

        var key = (media.ArtworkToken, style, lite);
        if (!force && key == shown) return;
        bool crossfade = shown.Token != -1;
        shown = key;
        SetImages(BuildLayer(media, style, lite), crossfade && !lite);
    }

    private FrameworkElement? BuildLayer(MediaController media, BackgroundStyle style, bool lite)
    {
        switch (style)
        {
            case BackgroundStyle.Artwork when media.Artwork != null:
                return new Image { Source = media.Artwork, Stretch = Stretch.UniformToFill };
            default:
                if (media.BlurredArtwork == null) return null;
                return lite ? Smooth(new Image { Source = media.BlurredArtwork, Stretch = Stretch.UniformToFill })
                            : new DriftingArtwork(media.BlurredArtwork);
        }
    }

    private static Image Smooth(Image image)
    {
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.Linear);
        return image;
    }

    private void SetImages(FrameworkElement? layer, bool crossfade)
    {
        if (layer == null)
        {
            foreach (UIElement child in imageHost.Children) Glass.Animate(child, OpacityProperty, 0, 0.6);
            ScheduleCleanup(null);
            return;
        }
        if (!crossfade)
        {
            imageHost.Children.Clear();
            imageHost.Children.Add(layer);
            return;
        }
        layer.Opacity = 0;
        imageHost.Children.Add(layer);
        Glass.Animate(layer, OpacityProperty, 1, 1.0, new SineEase { EasingMode = EasingMode.EaseInOut });
        ScheduleCleanup(layer);
    }

    private void ScheduleCleanup(UIElement? keep)
    {
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1.1) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            for (int i = imageHost.Children.Count - 1; i >= 0; i--)
                if (imageHost.Children[i] != keep) imageHost.Children.RemoveAt(i);
        };
        timer.Start();
    }

    private void Relayout()
    {
        foreach (var child in imageHost.Children)
            if (child is DriftingArtwork drifting) drifting.Relayout(ActualWidth, ActualHeight);
    }
}

/// Three copies of the tiny blurred artwork rotating and drifting at
/// different speeds (DriftingArtwork in the Mac app). It's all blur, so each
/// frame is drawn into a ~256 px bitmap — on a background thread, so it
/// never takes time from the lyrics and controls — and stretched over the
/// screen: the graphics card only scales one small image.
public sealed class DriftingArtwork : Image
{
    private const int TargetWidth = 256;
    private static readonly (double SizeFactor, double DegreesPerSecond, double Phase, double Drift, bool Soft, double Opacity)[] Layers =
    {
        (1.9, 3, 0.0, 0.06, false, 1),
        (1.1, -5, 2.1, 0.2, true, 0.8),
        (0.8, 7, 4.2, 0.28, true, 0.65),
    };
    private static readonly Brush SoftMask = MakeMask();
    private static System.Windows.Threading.Dispatcher? worker;
    private static readonly object WorkerLock = new();

    private readonly BitmapSource source;
    private double aspect = 16.0 / 9;
    private DateTime lastFrame = DateTime.MinValue;
    private bool running;
    private bool inFlight;

    /// Frames per second of the drift (it moves slowly; 30 looks the same as 60).
    public static int FramesPerSecond { get; set; } = 30;

    public DriftingArtwork(BitmapSource image)
    {
        source = image;
        Stretch = Stretch.Fill;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.Linear);
        Loaded += (_, _) => Start();
        Unloaded += (_, _) => Stop();
        SizeChanged += (_, e) =>
        {
            if (e.NewSize.Width > 0 && e.NewSize.Height > 0) aspect = e.NewSize.Width / e.NewSize.Height;
            // First frame right away (also used by the self-test snapshots).
            var (w, h) = PixelSize();
            Source = Render(source, w, h, Seconds(DateTime.UtcNow));
        };
    }

    private static Brush MakeMask()
    {
        var mask = new RadialGradientBrush();
        mask.GradientStops.Add(new GradientStop(Colors.Black, 0));
        mask.GradientStops.Add(new GradientStop(Color.FromArgb(150, 0, 0, 0), 0.55));
        mask.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
        mask.Freeze();
        return mask;
    }

    /// One shared background thread draws the frames.
    private static System.Windows.Threading.Dispatcher Worker
    {
        get
        {
            lock (WorkerLock)
            {
                if (worker != null) return worker;
                using var ready = new System.Threading.ManualResetEventSlim();
                var thread = new System.Threading.Thread(() =>
                {
                    worker = System.Windows.Threading.Dispatcher.CurrentDispatcher;
                    ready.Set();
                    System.Windows.Threading.Dispatcher.Run();
                }) { IsBackground = true, Name = "LockTune background" };
                thread.SetApartmentState(System.Threading.ApartmentState.STA);
                thread.Start();
                ready.Wait();
                return worker!;
            }
        }
    }

    private void Start()
    {
        if (running) return;
        running = true;
        CompositionTarget.Rendering += OnRendering;
    }

    private void Stop()
    {
        if (!running) return;
        running = false;
        CompositionTarget.Rendering -= OnRendering;
    }

    private (int, int) PixelSize() => (TargetWidth, Math.Max(16, (int)Math.Round(TargetWidth / Math.Max(aspect, 0.2))));

    private static double Seconds(DateTime now) => now.Subtract(DateTime.UnixEpoch).TotalSeconds % 100000;

    private void OnRendering(object? sender, EventArgs e)
    {
        if (!IsVisible || inFlight) return;
        var now = DateTime.UtcNow;
        if ((now - lastFrame).TotalSeconds < 1.0 / Math.Max(FramesPerSecond, 1) * 0.9) return;
        lastFrame = now;
        inFlight = true;
        var (w, h) = PixelSize();
        var image = source;
        double t = Seconds(now);
        var ui = Dispatcher;
        Worker.BeginInvoke(() =>
        {
            var frame = Render(image, w, h, t);
            ui.BeginInvoke(() =>
            {
                Source = frame;
                inFlight = false;
            });
        });
    }

    /// Draws one frame (any thread; the result is frozen).
    private static BitmapSource Render(BitmapSource image, int w, int h, double t)
    {
        double side = Math.Max(w, h);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            foreach (var l in Layers)
            {
                double s = side * l.SizeFactor;
                double x = w / 2.0 + Math.Sin(t * 0.07 + l.Phase) * w * l.Drift;
                double y = h / 2.0 + Math.Cos(t * 0.05 + l.Phase) * h * l.Drift;
                dc.PushTransform(new TranslateTransform(x, y));
                dc.PushTransform(new RotateTransform(t * l.DegreesPerSecond + l.Phase * 57.3));
                dc.PushOpacity(l.Opacity);
                if (l.Soft) dc.PushOpacityMask(SoftMask);
                dc.DrawImage(image, new Rect(-s / 2, -s / 2, s, s));
                if (l.Soft) dc.Pop();
                dc.Pop();
                dc.Pop();
                dc.Pop();
            }
        }
        var target = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }

    /// Kept for the layout code (the bitmap follows the control's size by itself).
    public void Relayout(double width, double height) { }
}
