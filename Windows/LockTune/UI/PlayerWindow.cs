using System;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LockTune.Core;
using LockTune.Lyrics;
using LockTune.Media;
using LockTune.Platform;

namespace LockTune.UI;

/// The lock-screen look in a normal window: same artwork wallpaper, clock,
/// player and lyrics, but nothing gets locked. It can be moved, resized,
/// maximised or made full screen (F11 or a double click on the background).
public sealed class PlayerWindow : Window
{
    private readonly LockScreenView view;
    private readonly AppSettings settings;
    private readonly Func<bool> paused;
    private bool rendering;
    private bool fullScreen;
    private WindowState stateBeforeFullScreen;

    public PlayerWindow(MediaController media, LyricsController lyrics, AppSettings settings, Func<bool> paused)
    {
        this.settings = settings;
        this.paused = paused;
        Title = "LockTune";
        Background = Brushes.Black;
        MinWidth = 720;
        MinHeight = 460;
        try { Icon = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/LockTune.ico")); }
        catch (Exception) { }

        view = new LockScreenView(media, lyrics, settings) { Presentation = Presentation.Window };
        view.BackgroundDoubleClicked += ToggleFullScreen;
        Content = view;
        ApplySavedBounds(settings.PlayerBounds);

        PreviewKeyDown += OnKey;
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            Native.SetAttribute(hwnd, Native.DWMWA_USE_IMMERSIVE_DARK_MODE, 1);
        };
        IsVisibleChanged += (_, _) => UpdateRendering();
        StateChanged += (_, _) => UpdateRendering();
        Closing += (_, e) =>
        {
            SaveBounds();
            if (OverlayWindow.AllowClose) return;
            // Kept for next time (the view listens to the app for good).
            e.Cancel = true;
            if (fullScreen) ToggleFullScreen();
            view.Stop();
            Hide();
        };
    }

    /// Shows the window (or brings it to the front) with the entrance animation.
    public void Open()
    {
        bool wasVisible = IsVisible;
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        if (!wasVisible)
        {
            view.ResetOpacity();
            view.PlayEntrance();
        }
    }

    public void ToggleFullScreen()
    {
        if (!fullScreen)
        {
            stateBeforeFullScreen = WindowState;
            fullScreen = true;
            // Normal first, so Windows covers the taskbar when maximising without a frame.
            WindowState = WindowState.Normal;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Maximized;
        }
        else
        {
            fullScreen = false;
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            WindowState = stateBeforeFullScreen == WindowState.Maximized ? WindowState.Maximized : WindowState.Normal;
        }
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.F11 || (key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Alt))
        {
            ToggleFullScreen();
            e.Handled = true;
        }
        else if (key == Key.Escape)
        {
            if (fullScreen) ToggleFullScreen();
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.None && view.HandleKey(key)) e.Handled = true;
    }

    // MARK: Frames

    private void UpdateRendering()
    {
        bool want = IsVisible && WindowState != WindowState.Minimized;
        if (want == rendering) return;
        rendering = want;
        if (want) CompositionTarget.Rendering += OnRendering;
        else CompositionTarget.Rendering -= OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        // The lock screen covers everything anyway: don't draw twice.
        if (paused()) return;
        view.Tick(frameTime: (e as RenderingEventArgs)?.RenderingTime);
    }

    // MARK: Size and place, remembered between launches

    private void ApplySavedBounds(string? saved)
    {
        var work = SystemParameters.WorkArea;
        double width = Math.Min(1280, work.Width * 0.8), height = Math.Min(780, work.Height * 0.8);
        Width = width;
        Height = height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var parts = saved?.Split(',');
        if (parts is not { Length: 5 }) return;
        var n = new double[4];
        for (int i = 0; i < 4; i++)
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out n[i])) return;
        var rect = new Rect(n[0], n[1], Math.Max(MinWidth, n[2]), Math.Max(MinHeight, n[3]));
        // Only where it's still on a screen (a monitor may be gone).
        var desktop = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                               SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        if (!desktop.IntersectsWith(new Rect(rect.Left + 40, rect.Top, Math.Max(1, rect.Width - 80), 40))) return;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = rect.Left;
        Top = rect.Top;
        Width = rect.Width;
        Height = rect.Height;
        if (parts[4] == "max") WindowState = WindowState.Maximized;
    }

    private void SaveBounds()
    {
        var r = fullScreen || WindowState != WindowState.Normal ? RestoreBounds : new Rect(Left, Top, ActualWidth, ActualHeight);
        if (r.IsEmpty || r.Width <= 0) return;
        bool maximized = fullScreen ? stateBeforeFullScreen == WindowState.Maximized : WindowState == WindowState.Maximized;
        settings.PlayerBounds = string.Join(",", r.Left.ToString(CultureInfo.InvariantCulture), r.Top.ToString(CultureInfo.InvariantCulture),
                                            r.Width.ToString(CultureInfo.InvariantCulture), r.Height.ToString(CultureInfo.InvariantCulture),
                                            maximized ? "max" : "normal");
        settings.Save();
    }
}
