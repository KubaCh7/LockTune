using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LockTune.Media;

public sealed record TrackInfo(string Title, string Artist, string Album, double Duration)
{
    /// Identifies a song (for lyrics offsets / caching), independent of the player.
    public string Id => $"{Artist}\u001f{Title}\u001f{Album}".ToLowerInvariant();
}

/// Dominant colours and brightness extracted from album artwork.
public sealed record ArtworkPalette(IReadOnlyList<Color> Colors, double AverageLuminance)
{
    public static readonly ArtworkPalette Fallback = new(new[]
    {
        Color.FromRgb(41, 51, 107), Color.FromRgb(92, 46, 107), Color.FromRgb(20, 26, 56),
    }, 0.18);

    /// Background is bright enough that text should be dark.
    public bool IsLight => AverageLuminance > 0.62;
    public Color Primary => Colors.Count > 0 ? Colors[0] : Color.FromRgb(38, 46, 89);
    public Color Secondary => Colors.Count > 1 ? Colors[1] : Mix(Primary, System.Windows.Media.Colors.Black, 0.35);
    public Color Tertiary => Colors.Count > 2 ? Colors[2] : Mix(Secondary, System.Windows.Media.Colors.White, 0.2);

    /// Main text colour: white/black, lightly tinted with the artwork.
    public Color TextColor => IsLight ? Mix(Primary, System.Windows.Media.Colors.Black, 0.82)
                                      : Mix(Primary, System.Windows.Media.Colors.White, 0.86);

    public static Color Mix(Color a, Color b, double amount) => Color.FromArgb(
        (byte)(a.A + (b.A - a.A) * amount),
        (byte)(a.R + (b.R - a.R) * amount),
        (byte)(a.G + (b.G - a.G) * amount),
        (byte)(a.B + (b.B - a.B) * amount));

    public static Color WithAlpha(Color c, double alpha) => Color.FromArgb((byte)Math.Clamp(alpha * 255, 0, 255), c.R, c.G, c.B);
}

public sealed record ArtworkAnalysis(ArtworkPalette Palette, BitmapSource? Blurred);

/// Palette + pre-blurred background image. Pure functions on frozen bitmaps,
/// safe to run on a worker thread.
public static class ArtworkAnalyzer
{
    public static BitmapSource? Decode(byte[] data)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.StreamSource = new MemoryStream(data);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static ArtworkAnalysis Analyze(BitmapSource image)
    {
        var blurred = MakeBlurred(image, 96, 7, 1.75);
        var palette = ExtractPalette(image, blurred);
        return new ArtworkAnalysis(palette, blurred);
    }

    /// Reads `side`×`side` BGRA pixels.
    public static byte[] Sample(BitmapSource image, int side)
    {
        var scaled = new TransformedBitmap(image, new ScaleTransform((double)side / image.PixelWidth,
                                                                     (double)side / image.PixelHeight));
        var bgra = new FormatConvertedBitmap(scaled, PixelFormats.Bgra32, null, 0);
        int w = bgra.PixelWidth, h = bgra.PixelHeight;
        var pixels = new byte[w * h * 4];
        bgra.CopyPixels(pixels, w * 4, 0);
        if (w == side && h == side) return pixels;
        // Rounding can be off by one pixel; pad/crop to exactly side×side.
        var exact = new byte[side * side * 4];
        for (int y = 0; y < side; y++)
            for (int x = 0; x < side; x++)
            {
                int sx = Math.Min(x, w - 1), sy = Math.Min(y, h - 1);
                Array.Copy(pixels, (sy * w + sx) * 4, exact, (y * side + x) * 4, 4);
            }
        return exact;
    }

    private static double Luminance(double r, double g, double b) => 0.2126 * r + 0.7152 * g + 0.0722 * b;

    public static ArtworkPalette ExtractPalette(BitmapSource image, BitmapSource? luminanceSource = null)
    {
        byte[] px;
        try { px = Sample(image, 32); }
        catch (Exception) { return ArtworkPalette.Fallback; }

        // Bucket colours into an 8×8×8 cube.
        var buckets = new Dictionary<int, (double R, double G, double B, int N)>();
        for (int i = 0; i < px.Length; i += 4)
        {
            if (px[i + 3] < 128) continue;
            double b = px[i] / 255.0, g = px[i + 1] / 255.0, r = px[i + 2] / 255.0;
            int key = ((int)(r * 7.999) << 6) | ((int)(g * 7.999) << 3) | (int)(b * 7.999);
            buckets.TryGetValue(key, out var e);
            buckets[key] = (e.R + r, e.G + g, e.B + b, e.N + 1);
        }
        if (buckets.Count == 0) return ArtworkPalette.Fallback;

        var candidates = buckets.Values.Select(e =>
        {
            double r = e.R / e.N, g = e.G / e.N, b = e.B / e.N;
            double mx = Math.Max(r, Math.Max(g, b)), mn = Math.Min(r, Math.Min(g, b));
            double saturation = mx == 0 ? 0 : (mx - mn) / mx;
            double lum = Luminance(r, g, b);
            double score = e.N * (0.35 + saturation * 1.6);
            if (lum < 0.05 || lum > 0.95) score *= 0.3;
            return (R: r, G: g, B: b, Score: score);
        }).OrderByDescending(c => c.Score);

        var chosen = new List<(double R, double G, double B)>();
        foreach (var c in candidates)
        {
            if (chosen.Count >= 3) break;
            if (chosen.All(o => Math.Sqrt((o.R - c.R) * (o.R - c.R) + (o.G - c.G) * (o.G - c.G) + (o.B - c.B) * (o.B - c.B)) > 0.22))
                chosen.Add((c.R, c.G, c.B));
        }

        // Brightness is measured on the blurred image: that's what text sits on.
        byte[] lumPx = px;
        if (luminanceSource != null)
        {
            try { lumPx = Sample(luminanceSource, 16); } catch (Exception) { }
        }
        double sum = 0;
        int count = 0;
        for (int i = 0; i < lumPx.Length; i += 4)
        {
            sum += Luminance(lumPx[i + 2] / 255.0, lumPx[i + 1] / 255.0, lumPx[i] / 255.0);
            count++;
        }

        var colors = chosen.Select(c => Color.FromRgb((byte)(c.R * 255), (byte)(c.G * 255), (byte)(c.B * 255))).ToList();
        return new ArtworkPalette(colors.Count > 0 ? colors : ArtworkPalette.Fallback.Colors, sum / Math.Max(count, 1));
    }

    /// Small, heavily blurred and saturated copy (scaled up smoothly by WPF
    /// it looks like a huge gaussian blur, at almost no cost).
    public static BitmapSource? MakeBlurred(BitmapSource image, int side, int radius, double saturation)
    {
        byte[] px;
        try { px = Sample(image, side); }
        catch (Exception) { return null; }

        for (int pass = 0; pass < 3; pass++)
        {
            BoxBlur(px, side, radius, horizontal: true);
            BoxBlur(px, side, radius, horizontal: false);
        }

        for (int i = 0; i < px.Length; i += 4)
        {
            double b = px[i], g = px[i + 1], r = px[i + 2];
            double gray = 0.299 * r + 0.587 * g + 0.114 * b;
            px[i] = ClampByte(gray + (b - gray) * saturation);
            px[i + 1] = ClampByte(gray + (g - gray) * saturation);
            px[i + 2] = ClampByte(gray + (r - gray) * saturation);
            px[i + 3] = 255;
        }

        var bitmap = BitmapSource.Create(side, side, 96, 96, PixelFormats.Bgra32, null, px, side * 4);
        bitmap.Freeze();
        return bitmap;
    }

    private static byte ClampByte(double v) => (byte)Math.Clamp(v, 0, 255);

    /// Box blur with clamped edges (three passes ≈ gaussian).
    private static void BoxBlur(byte[] px, int side, int radius, bool horizontal)
    {
        var line = new int[side * 4];
        int window = radius * 2 + 1;
        for (int a = 0; a < side; a++)
        {
            for (int i = 0; i < side; i++)
            {
                int idx = horizontal ? (a * side + i) * 4 : (i * side + a) * 4;
                for (int c = 0; c < 4; c++) line[i * 4 + c] = px[idx + c];
            }
            for (int c = 0; c < 4; c++)
            {
                int sum = 0;
                for (int k = -radius; k <= radius; k++) sum += line[Math.Clamp(k, 0, side - 1) * 4 + c];
                for (int i = 0; i < side; i++)
                {
                    int idx = horizontal ? (a * side + i) * 4 : (i * side + a) * 4;
                    px[idx + c] = (byte)(sum / window);
                    int add = Math.Min(i + radius + 1, side - 1), remove = Math.Max(i - radius, 0);
                    sum += line[add * 4 + c] - line[remove * 4 + c];
                }
            }
        }
    }

    /// The desktop wallpaper (the backdrop of "without wallpaper" mode,
    /// like the normal lock screen showing your wallpaper on the Mac).
    public static BitmapSource? Wallpaper(int width)
    {
        var path = Platform.Native.DesktopWallpaperPath();
        if (path == null || !File.Exists(path)) return null;
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = Math.Clamp(width, 640, 3840);
            image.UriSource = new Uri(path);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// Solid square "artwork" for previews/tests.
    public static BitmapSource TestArtwork()
    {
        const int side = 300;
        var px = new byte[side * side * 4];
        for (int y = 0; y < side; y++)
            for (int x = 0; x < side; x++)
            {
                int i = (y * side + x) * 4;
                double t = (x + y) / (2.0 * side);
                bool sun = Math.Pow(x - 200, 2) + Math.Pow(y - 110, 2) < 55 * 55;
                px[i] = (byte)(sun ? 90 : 160 - 120 * t);
                px[i + 1] = (byte)(sun ? 200 : 60 + 40 * t);
                px[i + 2] = (byte)(sun ? 255 : 230 - 120 * t);
                px[i + 3] = 255;
            }
        var bitmap = BitmapSource.Create(side, side, 96, 96, PixelFormats.Bgra32, null, px, side * 4);
        bitmap.Freeze();
        return bitmap;
    }

    public static Rect UniformToFill(Size image, Size target)
    {
        double scale = Math.Max(target.Width / image.Width, target.Height / image.Height);
        double w = image.Width * scale, h = image.Height * scale;
        return new Rect((target.Width - w) / 2, (target.Height - h) / 2, w, h);
    }
}
