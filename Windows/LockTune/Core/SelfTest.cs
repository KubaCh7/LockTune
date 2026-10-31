using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LockTune.Lyrics;
using LockTune.Media;
using LockTune.Platform;
using LockTune.UI;

namespace LockTune.Core;

/// `LockTune.exe --self-test <folder>`: checks the parts that can be checked
/// without a person (parsing, palette, settings, media sessions, LRCLIB) and
/// renders previews of every screen into <folder> as PNGs.
public static class SelfTest
{
    private static readonly List<string> Log = new();
    private static int failures;

    public static async Task<int> RunAsync(string folder)
    {
        Directory.CreateDirectory(folder);
        void Check(string name, bool ok, string detail = "")
        {
            Log.Add($"{(ok ? "PASS" : "FAIL")}  {name}{(detail.Length > 0 ? "  — " + detail : "")}");
            if (!ok) failures++;
        }
        void Info(string text) => Log.Add("INFO  " + text);

        Info($"LockTune {AppCoordinator.VersionString} on {Environment.OSVersion.VersionString}");

        // Lyrics parsing.
        try
        {
            var lines = LrcParser.Parse("[offset:+500]\n[00:01.00]intro\n[00:10.00][00:30.00]Hello world\n[00:12.50]\n[00:20.00]<00:20.00>Word <00:20.80>by <00:21.40>word\n");
            Check("LRC: lines parsed", lines.Count(l => !l.IsInstrumental) == 4, $"{lines.Count} lines");
            Check("LRC: offset applied", Math.Abs(lines.First(l => l.Text == "intro").Time - 0.5) < 0.001);
            Check("LRC: repeated timestamps", lines.Count(l => l.Text == "Hello world") == 2);
            var word = lines.First(l => l.Text.StartsWith("Word"));
            Check("LRC: word timing", word.HasWordTiming && word.Words.Count == 3 && Math.Abs(word.Words[1].Time!.Value - 20.3) < 0.001);
            var progress = word.WordProgress(20.6);
            Check("Word progress", progress[0] > 0.5 && progress[2] == 0, string.Join(", ", progress.Select(p => p.ToString("0.00"))));
            var estimated = new LyricLine(0, 10, 14, "one two three four").WordProgress(11);
            Check("Estimated word progress", estimated[0] == 1 && estimated[^1] < 1);
            Check("Active line", LrcParser.ActiveIndex(lines, 10.2) == lines.FindIndex(l => l.Text == "Hello world"));
            Check("Title cleanup", LyricsService.CleanTitle("Song (feat. X) - Remastered 2011") == "Song");
            Check("Primary artist", LyricsService.PrimaryArtist("A, B & C") == "A");
        }
        catch (Exception e) { Check("Lyrics parsing", false, e.ToString()); }

        // Artwork palette + blur.
        var art = ArtworkAnalyzer.TestArtwork();
        try
        {
            var analysis = ArtworkAnalyzer.Analyze(art);
            Check("Palette", analysis.Palette.Colors.Count >= 2, string.Join(" ", analysis.Palette.Colors));
            Check("Blurred artwork", analysis.Blurred is { PixelWidth: 96 });
        }
        catch (Exception e) { Check("Artwork analysis", false, e.ToString()); }

        // Settings + presets.
        try
        {
            var s = new AppSettings { ArtworkScale = 1.7, LyricsPosition = LyricsPosition.Center, GlassLevel = GlassLevel.Mega };
            var json = System.Text.Json.JsonSerializer.Serialize(s, AppSettings.JsonOptions);
            var back = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(json, AppSettings.JsonOptions)!;
            Check("Settings round trip", back.ArtworkScale == 1.7 && back.LyricsPosition == LyricsPosition.Center && back.GlassLevel == GlassLevel.Mega);
            var preset = s.ExportPreset("Test");
            var other = new AppSettings();
            Check("Preset import", other.ImportPreset(preset, out var presetName) && presetName == "Test" && other.ArtworkScale == 1.7 && other.GlassLevel == GlassLevel.Mega);
            Check("Preset rejects junk", !other.ImportPreset("{\"hello\": 1}", out _));
            // A preset exported by the Mac app.
            var mac = "{\"format\":\"LockTune preset\",\"version\":1,\"name\":\"Mac\",\"clockFont\":\"newYork\",\"clockWeight\":\"ultraLight\",\"glassLevel\":\"normal\",\"lyricsPosition\":\"center\",\"artworkScale\":2.2,\"lockWallpaper\":false}";
            Check("Mac preset import", other.ImportPreset(mac, out _) && other.ClockWeight == ClockWeight.UltraLight && other.LyricsPosition == LyricsPosition.Center && !other.LockWallpaper);
            Check("Preset uses Mac names", preset.Contains("\"glassLevel\": \"mega\"") && preset.Contains("\"clockFont\": \"newYork\""), preset.Replace("\n", " "));
            Check("Hotkey text", Hotkey.Default.ToString() == "Ctrl+Shift+L", Hotkey.Default.ToString());
            Check("Ctrl+Alt+L is refused (AltGr)", new Hotkey(Native.MOD_CONTROL | Native.MOD_ALT, 0x4C).CollidesWithAltGr && !Hotkey.Default.CollidesWithAltGr);
        }
        catch (Exception e) { Check("Settings", false, e.ToString()); }

        // Windows APIs.
        try
        {
            var manager = await global::Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            Check("Media sessions available", manager != null, $"{manager?.GetSessions().Count ?? 0} session(s) on this machine");
        }
        catch (Exception e) { Check("Media sessions available", false, e.Message); }
        try
        {
            var guard = new KeyboardGuard();
            guard.Start();
            Check("Keyboard guard hooks", guard.IsActive);
            guard.Stop();
        }
        catch (Exception e) { Check("Keyboard guard hooks", false, e.Message); }
        try
        {
            using var hotkeys = new HotkeyManager();
            bool ok = hotkeys.Register(new Hotkey(Native.MOD_CONTROL | Native.MOD_ALT | Native.MOD_SHIFT, 0x7B)); // Ctrl+Alt+Shift+F12
            Check("Global shortcut registers", ok);
        }
        catch (Exception e) { Check("Global shortcut registers", false, e.Message); }
        Info("Idle for " + Native.IdleTime());
        Info($"Render tier {System.Windows.Media.RenderCapability.Tier >> 16}");
        Info("Wallpaper: " + (Native.DesktopWallpaperPath() ?? "none"));

        // LRCLIB (network; informational only).
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var content = await new LyricsService().LyricsAsync(new TrackInfo("Blinding Lights", "The Weeknd", "After Hours", 200), cts.Token);
            Info("LRCLIB: " + (content is LyricsContent.Synced synced ? $"{synced.Lines.Count} synced lines" : content.GetType().Name));
        }
        catch (Exception e) { Info("LRCLIB unreachable: " + e.Message); }

        // Screens.
        try { RenderScreens(folder, art, Check); }
        catch (Exception e) { Check("Rendering", false, e.ToString()); }

        Log.Add(failures == 0 ? "ALL PASSED" : $"{failures} FAILED");
        File.WriteAllLines(Path.Combine(folder, "selftest.log"), Log);
        return failures == 0 ? 0 : 1;
    }

    private static void RenderScreens(string folder, BitmapSource art, Action<string, bool, string> check)
    {
        var settings = new AppSettings();
        var app = new AppCoordinator(settings);
        var track = new TrackInfo("Midnight City", "M83", "Hurry Up, We're Dreaming", 243);
        app.Media.SetTestState(track, art, playing: true, position: 61);
        app.Lyrics.SetContentForTesting(new LyricsContent.Synced(LrcParser.Parse(string.Join("\n", new[]
        {
            "[00:52.00]Waiting in a car", "[00:55.00]Waiting for a ride in the dark", "[00:59.00]The night city grows",
            "[01:02.00]Look and see her eyes, they glow", "[01:06.00]Waiting in a car", "[01:09.00]Waiting for a ride in the dark",
            "[01:13.00]Drinking in the lounge", "[01:16.00]Following the neon signs", "[01:20.00]Waiting for a roar",
        }))));

        var view = new LockScreenView(app.Media, app.Lyrics, settings) { Presentation = Presentation.Lock };
        bool withLyrics = false;
        void Shot(string name, Action prepare)
        {
            prepare();
            var size = new Size(1920, 1080);
            view.Measure(size);
            view.Arrange(new Rect(size));
            view.ShowStatic(withLyrics);
            view.UpdateLayout();
            for (int i = 0; i < 120; i++) view.Tick(1.0 / 60);
            view.UpdateLayout();
            var bitmap = Save(view, size, Path.Combine(folder, name + ".png"));
            check($"Render {name}", !IsBlank(bitmap), "");
        }

        Shot("lock-player", () => withLyrics = false);
        Shot("lock-lyrics-right", () => withLyrics = true);
        Shot("lock-lyrics-center", () => settings.LyricsPosition = LyricsPosition.Center);
        Shot("lock-mega-glass", () => { settings.LyricsPosition = LyricsPosition.Left; settings.GlassLevel = GlassLevel.Mega; });
        Shot("lock-no-wallpaper", () => { settings.GlassLevel = GlassLevel.Normal; settings.LyricsPosition = LyricsPosition.Right; settings.LockWallpaper = false; withLyrics = false; });
        Shot("lock-no-wallpaper-lyrics", () => withLyrics = true);
        Shot("lock-lite-paused", () =>
        {
            settings.LockWallpaper = true;
            settings.LiteMode = true;
            withLyrics = false;
            app.Media.SetTestState(track, art, playing: false, position: 61);
        });
        view.Presentation = Presentation.Preview;
        Shot("preview", () => settings.LiteMode = false);

        // Switching the lyrics position with the real animations (in a live window).
        {
            var live = new LockScreenView(app.Media, app.Lyrics, settings) { Presentation = Presentation.Lock };
            var host = new Window
            {
                WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, ShowActivated = false,
                Left = -10000, Top = -10000, Width = 1920, Height = 1080, Content = live,
            };
            host.Show();
            void Frame() { live.Tick(); }
            System.Windows.Media.CompositionTarget.Rendering += (_, _) => Frame();
            settings.LockWallpaper = true;
            settings.LyricsPosition = LyricsPosition.Right;
            app.Media.SetTestState(track, art, playing: true, position: 61);

            // Frame by frame through each switch (catches one-frame glitches).
            live.ShowStatic(false);
            Wait(1.0);
            var steps = new (string Name, Action Do)[]
            {
                ("lyrics-on", () => live.HandleKey(System.Windows.Input.Key.L)),
                ("to-left", () => settings.LyricsPosition = LyricsPosition.Left),
                ("to-center", () => settings.LyricsPosition = LyricsPosition.Center),
                ("to-right", () => settings.LyricsPosition = LyricsPosition.Right),
                ("lyrics-off", () => live.HandleKey(System.Windows.Input.Key.L)),
            };
            for (int i = 0; i < steps.Length; i++)
            {
                steps[i].Do();
                for (int k = 0; k < 6; k++)
                {
                    if (k > 0) Wait(0.05);
                    live.UpdateLayout();
                    Save(live, new Size(live.ActualWidth, live.ActualHeight), Path.Combine(folder, $"anim-{i}-{steps[i].Name}-{k}.png"));
                }
                Wait(0.8);
            }
            live.ShowStatic(true);
            Wait(1.0);
            foreach (var position in new[] { LyricsPosition.Left, LyricsPosition.Center, LyricsPosition.Right, LyricsPosition.Center, LyricsPosition.Left })
            {
                settings.LyricsPosition = position;
                Wait(1.0);
                var size = new Size(live.ActualWidth, live.ActualHeight);
                var bitmap = Save(live, size, Path.Combine(folder, $"switch-to-{position.ToString().ToLowerInvariant()}-{Environment.TickCount64 % 1000}.png"));
                check($"Switch lyrics to {position}", !IsBlank(bitmap), "");
            }
            host.Close();
        }

        // The player window (lock-screen look in a normal window).
        {
            settings.LockWallpaper = false; // the window shows the artwork anyway
            app.Media.SetTestState(track, art, playing: true, position: 61);
            var player = new PlayerWindow(app.Media, app.Lyrics, settings, () => false)
            {
                WindowStartupLocation = WindowStartupLocation.Manual, Left = -10000, Top = -10000, Width = 1280, Height = 760, ShowActivated = false,
            };
            player.Open();
            Wait(1.2);
            if (player.Content is FrameworkElement body)
            {
                var bitmap = Save(body, new Size(body.ActualWidth, body.ActualHeight), Path.Combine(folder, "player-window.png"));
                check("Render player window", !IsBlank(bitmap), $"{body.ActualWidth:0}x{body.ActualHeight:0}");
                if (body is LockScreenView pv)
                {
                    pv.HandleKey(System.Windows.Input.Key.L);
                    Wait(1.2);
                    bitmap = Save(body, new Size(body.ActualWidth, body.ActualHeight), Path.Combine(folder, "player-window-lyrics.png"));
                    check("Render player window with lyrics", !IsBlank(bitmap), "");
                }
            }
            player.ToggleFullScreen();
            Wait(0.5);
            check("Player window full screen", player.WindowState == WindowState.Maximized && player.WindowStyle == WindowStyle.None, "");
            player.ToggleFullScreen();
            check("Player window back from full screen", player.WindowStyle == WindowStyle.SingleBorderWindow, "");
            player.Close();
            check("Player window hides (kept for next time)", !player.IsVisible, "");
            settings.PlayerBounds = null;
            settings.LockWallpaper = true;
        }

        var turntable = new TurntableWindow(app.Media, settings) { Left = -5000, Top = -5000 };
        turntable.Show();
        turntable.UpdateLayout();
        if (turntable.Content is FrameworkElement deck)
        {
            var bitmap = Save(deck, new Size(deck.ActualWidth, deck.ActualHeight), Path.Combine(folder, "turntable.png"));
            check("Render turntable", !IsBlank(bitmap), "");
        }
        turntable.Close();

        settings.LiteMode = false;
        var window = new SettingsWindow(app) { Left = -5000, Top = -5000, ShowActivated = false };
        window.Show();
        for (int tab = 0; tab < 5; tab++)
        {
            window.SelectTab(tab);
            window.UpdateLayout();
            if (window.Content is FrameworkElement content)
            {
                // Opaque backdrop so the PNG looks like the real (acrylic) window.
                var host = new System.Windows.Controls.Border
                {
                    Background = new LinearGradientBrush(Color.FromRgb(32, 33, 44), Color.FromRgb(18, 18, 24), 90),
                    Child = new System.Windows.Shapes.Rectangle { Fill = new VisualBrush(content), Width = content.ActualWidth, Height = content.ActualHeight },
                };
                var size = new Size(content.ActualWidth, content.ActualHeight);
                host.Measure(size);
                host.Arrange(new Rect(size));
                var bitmap = Save(host, size, Path.Combine(folder, $"settings-{tab + 1}.png"));
                check($"Render settings tab {tab + 1}", !IsBlank(bitmap), "");
            }
        }
        window.Close();
    }

    /// Lets the dispatcher (and animations) run for a while.
    private static void Wait(double seconds)
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    private static RenderTargetBitmap Save(FrameworkElement element, Size size, string path)
    {
        var bitmap = new RenderTargetBitmap((int)Math.Max(1, size.Width), (int)Math.Max(1, size.Height), 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(element);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
        return bitmap;
    }

    private static bool IsBlank(BitmapSource bitmap)
    {
        int w = bitmap.PixelWidth, h = bitmap.PixelHeight;
        var px = new byte[w * h * 4];
        bitmap.CopyPixels(px, w * 4, 0);
        int first = BitConverter.ToInt32(px, 0), different = 0;
        for (int i = 0; i < px.Length; i += 4 * 97)
            if (BitConverter.ToInt32(px, i) != first) different++;
        return different < 10;
    }
}
