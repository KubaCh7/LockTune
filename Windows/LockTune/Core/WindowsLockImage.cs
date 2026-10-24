using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LockTune.Media;
using Windows.Storage;

namespace LockTune.Core;

/// Experimental: puts the album cover (on its blurred artwork) on the real
/// Windows lock screen, so pressing Win+L shows the song too.
public static class WindowsLockImage
{
    private static int counter;

    /// Returns a status line for the settings window.
    public static async Task<string> SetAsync(MediaController media)
    {
        if (media.Artwork == null) return "Waiting for album art";
        try
        {
            var image = Compose(media.Artwork, media.BlurredArtwork, media.Palette);
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LockTune");
            Directory.CreateDirectory(folder);
            // Alternate file names: Windows caches the picture by path.
            var path = Path.Combine(folder, $"lockscreen-{counter++ % 2}.jpg");
            var encoder = new JpegBitmapEncoder { QualityLevel = 92 };
            encoder.Frames.Add(BitmapFrame.Create(image));
            using (var stream = File.Create(path)) encoder.Save(stream);

            var file = await StorageFile.GetFileFromPathAsync(path);
            await global::Windows.System.UserProfile.LockScreen.SetImageFileAsync(file);
            return $"Showing \"{media.Track?.Title}\"";
        }
        catch (Exception e)
        {
            return "Windows refused: " + e.Message;
        }
    }

    /// 1920×1080: blurred artwork, the cover in the middle (a bit low, below
    /// the Windows 11 clock).
    public static BitmapSource Compose(BitmapSource artwork, BitmapSource? blurred, ArtworkPalette palette)
    {
        const int width = 1920, height = 1080;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new LinearGradientBrush(palette.Primary, palette.Secondary, 45), null, new Rect(0, 0, width, height));
            if (blurred != null)
                dc.DrawImage(blurred, ArtworkAnalyzer.UniformToFill(new Size(blurred.PixelWidth, blurred.PixelHeight), new Size(width, height)));
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(60, 0, 0, 0)), null, new Rect(0, 0, width, height));

            const double side = 520;
            var rect = new Rect((width - side) / 2, height * 0.6 - side / 2, side, side);
            for (int i = 1; i <= 6; i++)
            {
                var shadow = rect;
                shadow.Inflate(i * 6, i * 6);
                shadow.Offset(0, 14);
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb((byte)(26 - i * 3), 0, 0, 0)), null, shadow, 30 + i * 6, 30 + i * 6);
            }
            dc.PushClip(new RectangleGeometry(rect, 30, 30));
            dc.DrawImage(artwork, rect);
            dc.Pop();
        }
        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }
}
