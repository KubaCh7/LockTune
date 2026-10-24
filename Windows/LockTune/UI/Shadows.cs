using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LockTune.UI;

/// Soft shadows as plain images, blurred once on the CPU. WPF's shadow
/// effect is recomputed every frame while anything behind it moves (the
/// background always does), which made the lock screen stutter.
public static class Shadows
{
    private const double Scale = 0.25; // it's a blur: quarter resolution is plenty
    private static readonly Dictionary<(int, int, int, int, int), BitmapSource> Cache = new();

    /// Shadow of a `width`×`height` rounded rectangle, like SwiftUI's
    /// .shadow(color: .black.opacity(opacity), radius: radius). The image
    /// is `padding` larger on every side.
    public static BitmapSource RoundedRect(double width, double height, double corner, double radius, double opacity, out double padding)
    {
        padding = Math.Max(2, radius * 2);
        var key = ((int)Math.Round(width / 4), (int)Math.Round(height / 4), (int)Math.Round(corner), (int)Math.Round(radius), (int)Math.Round(opacity * 100));
        if (Cache.TryGetValue(key, out var cached)) return cached;
        if (Cache.Count > 64) Cache.Clear();

        int w = Math.Max(4, (int)Math.Ceiling((width + padding * 2) * Scale));
        int h = Math.Max(4, (int)Math.Ceiling((height + padding * 2) * Scale));
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(Scale, Scale));
            dc.DrawRoundedRectangle(Brushes.Black, null, new Rect(padding, padding, width, height), corner, corner);
            dc.Pop();
        }
        var target = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        var px = new byte[w * h * 4];
        target.CopyPixels(px, w * 4, 0);

        // Alpha channel only: three box passes ≈ a gaussian.
        var alpha = new double[w * h];
        for (int i = 0; i < alpha.Length; i++) alpha[i] = px[i * 4 + 3];
        int r = Math.Max(1, (int)Math.Round(radius * Scale * 0.6));
        for (int pass = 0; pass < 3; pass++)
        {
            Blur(alpha, w, h, r, horizontal: true);
            Blur(alpha, w, h, r, horizontal: false);
        }
        for (int i = 0; i < alpha.Length; i++)
        {
            byte a = (byte)Math.Clamp(alpha[i] * opacity, 0, 255);
            px[i * 4] = px[i * 4 + 1] = px[i * 4 + 2] = 0;
            px[i * 4 + 3] = a;
        }
        var result = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, px, w * 4);
        result.Freeze();
        Cache[key] = result;
        return result;
    }

    private static void Blur(double[] a, int w, int h, int r, bool horizontal)
    {
        int length = horizontal ? w : h, lines = horizontal ? h : w;
        var line = new double[length];
        double window = r * 2 + 1;
        for (int l = 0; l < lines; l++)
        {
            for (int i = 0; i < length; i++) line[i] = a[horizontal ? l * w + i : i * w + l];
            double sum = 0;
            for (int k = -r; k <= r; k++) sum += k < 0 || k >= length ? 0 : line[k];
            for (int i = 0; i < length; i++)
            {
                a[horizontal ? l * w + i : i * w + l] = sum / window;
                int add = i + r + 1, remove = i - r;
                sum += (add < length ? line[add] : 0) - (remove >= 0 ? line[remove] : 0);
            }
        }
    }

    /// Puts a shadow image behind an element of the given size (offset down by `y`).
    public static void Apply(Image image, double width, double height, double corner, double radius, double opacity, double y)
    {
        if (width <= 0 || height <= 0 || opacity <= 0)
        {
            image.Source = null;
            return;
        }
        image.Source = RoundedRect(width, height, corner, radius, opacity, out double pad);
        image.Stretch = Stretch.Fill;
        image.IsHitTestVisible = false;
        image.Margin = new Thickness(-pad, -pad + y, -pad, -pad - y);
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.Linear);
    }
}

/// Holds a shadow image behind an element without taking part in its size:
/// it's measured as 0×0 and stretched over the parent (plus the image's
/// negative margins), so it follows resize animations for free.
public sealed class Backdrop : Decorator
{
    public Backdrop() { IsHitTestVisible = false; }

    protected override Size MeasureOverride(Size constraint)
    {
        Child?.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return new Size(0, 0);
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        Child?.Arrange(new Rect(arrangeSize));
        return arrangeSize;
    }
}

/// A shadow that scales with its element: it's made once for a reference
/// size and drawn stretched to the element's current size every frame, so
/// it moves and grows together with the artwork during layout animations.
public sealed class ScalingShadow : FrameworkElement
{
    private const double Reference = 400;
    private BitmapSource? image;
    private double padRatio, yRatio;

    public ScalingShadow()
    {
        IsHitTestVisible = false;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.Linear);
        SizeChanged += (_, _) => InvalidateVisual();
    }

    /// Radius and offset as fractions of the element's width (like the Mac's
    /// side × 0.09 etc.); the corner radius too.
    public void Set(double cornerRatio, double radiusRatio, double opacity, double offsetRatio)
    {
        if (opacity <= 0) { image = null; InvalidateVisual(); return; }
        image = Shadows.RoundedRect(Reference, Reference, cornerRatio * Reference, radiusRatio * Reference, opacity, out double pad);
        padRatio = pad / Reference;
        yRatio = offsetRatio;
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize) => new(0, 0);

    protected override void OnRender(DrawingContext dc)
    {
        if (image == null) return;
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        double pad = padRatio * w;
        dc.DrawImage(image, new Rect(-pad, -pad + yRatio * w, w + pad * 2, h + pad * 2));
    }
}
