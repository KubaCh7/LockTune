using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace LockTune.Platform;

/// Finds the icon of the app behind a media session (by its running process).
public static class AppIcons
{
    private static readonly Dictionary<string, BitmapSource?> Cache = new();

    public static BitmapSource? For(string? appId)
    {
        if (string.IsNullOrEmpty(appId)) return null;
        if (Cache.TryGetValue(appId, out var cached)) return cached;
        BitmapSource? icon = null;
        foreach (var name in ProcessNames(appId))
        {
            icon = FromProcess(name);
            if (icon != null) break;
        }
        Cache[appId] = icon;
        return icon;
    }

    private static IEnumerable<string> ProcessNames(string appId)
    {
        var id = appId.ToLowerInvariant();
        if (id.Contains("spotify")) yield return "Spotify";
        if (id.Contains("applemusic")) yield return "AppleMusic";
        if (id.Contains("itunes")) yield return "iTunes";
        // "Spotify.exe", "chrome.exe", "Microsoft.ZuneMusic_…!Microsoft.ZuneMusic"
        var first = appId.Split('!')[0];
        if (first.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) yield return first[..^4];
        var last = appId.Split('!')[^1];
        if (last.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) last = last[..^4];
        yield return last;
    }

    private static BitmapSource? FromProcess(string name)
    {
        try
        {
            foreach (var process in Process.GetProcessesByName(name))
            {
                using (process)
                {
                    string? path;
                    try { path = process.MainModule?.FileName; } catch { continue; }
                    if (path == null) continue;
                    using var icon = System.Drawing.Icon.ExtractAssociatedIcon(path);
                    if (icon == null) continue;
                    var source = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    source.Freeze();
                    return source;
                }
            }
        }
        catch (Exception) { }
        return null;
    }
}
