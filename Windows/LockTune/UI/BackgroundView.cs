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

/// Full-screen background built from the artwork: a blurred, saturated,
/// slowly drifting version (Apple Music style), the artwork itself, or the
/// blurred Windows wallpaper — tinted with the artwork's dominant colours.
public sealed class BackgroundView : Grid
{
    private readonly Rectangle baseGradient = new();
    private readonly Grid imageHost = new() { ClipToBounds = true };
    private readonly Rectangle tint = new() { IsHitTestVisible = false };
    private readonly Rectangle scrim = new() { IsHitTestVisible = false };
    private readonly Rectangle vignette = new() { IsHitTestVisible = false };
    private (int Token, BackgroundStyle Style, bool Lite) shown = (-1, BackgroundStyle.Blurred, false);
    private static BitmapSource? wallpaper;
    private static DateTime wallpaperLoaded;

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
        tint.Visibility = style == BackgroundStyle.Wallpaper ? Visibility.Collapsed : Visibility.Visible;
        bool light = palette.IsLight && style != BackgroundStyle.Wallpaper;
        scrim.Fill = Glass.Frozen(new SolidColorBrush(light ? Color.FromArgb(15, 255, 255, 255)
                                                            : Color.FromArgb(style == BackgroundStyle.Blurred ? (byte)46 : (byte)70, 0, 0, 0)));
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
            case BackgroundStyle.Wallpaper:
                if (wallpaper == null || DateTime.UtcNow - wallpaperLoaded > TimeSpan.FromMinutes(2))
                {
                    wallpaper = ArtworkAnalyzer.BlurredWallpaper() ?? wallpaper;
                    wallpaperLoaded = DateTime.UtcNow;
                }
                return wallpaper == null ? null : Smooth(new Image { Source = wallpaper, Stretch = Stretch.UniformToFill });
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
/// different speeds; GPU-composited transforms only, so it's cheap.
public sealed class DriftingArtwork : Canvas
{
    private readonly (Image Image, double SizeFactor, double Seconds, double Phase, double Drift, bool Soft)[] layers;

    public DriftingArtwork(BitmapSource image)
    {
        ClipToBounds = true;
        layers = new[]
        {
            (MakeImage(image, false), 1.9, 120.0, 0.0, 0.06, false),
            (MakeImage(image, true), 1.1, -72.0, 120.0, 0.2, true),
            (MakeImage(image, true), 0.8, 51.0, 240.0, 0.28, true),
        };
        for (int i = 0; i < layers.Length; i++)
        {
            var l = layers[i];
            l.Image.Opacity = i == 0 ? 1 : i == 1 ? 0.8 : 0.65;
            Children.Add(l.Image);
        }
        SizeChanged += (_, e) => Relayout(e.NewSize.Width, e.NewSize.Height);
    }

    private static Image MakeImage(BitmapSource source, bool soft)
    {
        var image = new Image { Source = source, Stretch = Stretch.Fill, RenderTransformOrigin = new Point(0.5, 0.5) };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.Linear);
        if (soft)
        {
            var mask = new RadialGradientBrush();
            mask.GradientStops.Add(new GradientStop(Colors.Black, 0));
            mask.GradientStops.Add(new GradientStop(Color.FromArgb(150, 0, 0, 0), 0.55));
            mask.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
            image.OpacityMask = Glass.Frozen(mask);
        }
        return image;
    }

    public void Relayout(double width, double height)
    {
        if (width <= 0 || height <= 0) return;
        double side = Math.Max(width, height);
        foreach (var l in layers)
        {
            double s = side * l.SizeFactor;
            l.Image.Width = s;
            l.Image.Height = s;
            SetLeft(l.Image, (width - s) / 2);
            SetTop(l.Image, (height - s) / 2);

            var rotate = new RotateTransform(l.Phase);
            var move = new TranslateTransform();
            var group = new TransformGroup();
            group.Children.Add(rotate);
            group.Children.Add(move);
            l.Image.RenderTransform = group;

            // l.Seconds = time for a full turn (negative = counter-clockwise).
            var spin = new DoubleAnimation(l.Phase, l.Phase + (l.Seconds > 0 ? 360 : -360), TimeSpan.FromSeconds(Math.Abs(l.Seconds)))
            { RepeatBehavior = RepeatBehavior.Forever };
            rotate.BeginAnimation(RotateTransform.AngleProperty, spin);

            var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
            var driftX = new DoubleAnimation(-width * l.Drift, width * l.Drift, TimeSpan.FromSeconds(45 + l.Phase / 10))
            { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease };
            var driftY = new DoubleAnimation(height * l.Drift, -height * l.Drift, TimeSpan.FromSeconds(63 - l.Phase / 12))
            { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = ease };
            move.BeginAnimation(TranslateTransform.XProperty, driftX);
            move.BeginAnimation(TranslateTransform.YProperty, driftY);
        }
    }
}
