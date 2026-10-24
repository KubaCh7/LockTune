using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LockTune.Core;
using LockTune.Lyrics;
using LockTune.Media;
using LockTune.Platform;
using Forms = System.Windows.Forms;

namespace LockTune.UI;

/// A borderless, top-most window covering one monitor exactly.
public sealed class OverlayWindow : Window
{
    public readonly Image Underlay = new() { Stretch = Stretch.Fill, IsHitTestVisible = false };
    /// Set when the app quits (Alt+F4 etc. must not close the lock screen).
    public static bool AllowClose { get; set; }
    private System.Drawing.Rectangle bounds;

    public OverlayWindow(FrameworkElement content)
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = true;
        Topmost = true;
        Background = Brushes.Black;
        Title = "LockTune";
        var root = new Grid();
        root.Children.Add(Underlay);
        root.Children.Add(content);
        Content = root;
        Closing += (_, e) => { if (!AllowClose) e.Cancel = true; };
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            Native.AddExStyle(hwnd, Native.WS_EX_TOOLWINDOW, Native.WS_EX_APPWINDOW); // not in Alt+Tab
        };
    }

    public IntPtr Handle => new WindowInteropHelper(this).EnsureHandle();

    /// Covers `screen` (physical pixels; WPF adapts to the monitor's DPI).
    public void Cover(Forms.Screen screen)
    {
        bounds = screen.Bounds;
        Left = bounds.Left;
        Top = bounds.Top;
        Width = Math.Max(bounds.Width, 100);
        Height = Math.Max(bounds.Height, 100);
        Native.SetWindowPos(Handle, Native.HWND_TOPMOST, bounds.Left, bounds.Top, bounds.Width, bounds.Height, Native.SWP_NOACTIVATE);
    }

    public void Reassert()
    {
        Native.SetWindowPos(Handle, Native.HWND_TOPMOST, bounds.Left, bounds.Top, bounds.Width, bounds.Height,
                            Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
    }
}

/// Shows the LockTune lock screen on every monitor (player on the primary one,
/// the artwork background on the others).
///
/// Windows doesn't let apps draw on its own lock screen, so LockTune shows its
/// lock screen first; leaving it locks Windows for real (PIN / password), and
/// anything that tries to get around it (Ctrl+Alt+Del → Task Manager, another
/// window stealing focus) locks Windows immediately.
public sealed class LockScreenController
{
    private readonly MediaController media;
    private readonly LyricsController lyrics;
    private readonly AppSettings settings;
    private readonly KeyboardGuard keyboardGuard = new();
    private readonly DisplayAwake displayAwake = new();
    private readonly DispatcherTimer watchdog = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private readonly DispatcherTimer cursorTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private LockScreenView? view;
    private OverlayWindow? main;
    private readonly List<(OverlayWindow Window, LockScreenView View)> extras = new();
    private DateTime shownAt;
    private DateTime lastMouseMove;
    private Point lastMousePosition;
    private bool cursorHidden;
    private bool unlocking;
    private bool rendering;

    public bool IsVisible { get; private set; }
    public Presentation Presentation { get; private set; }
    /// The lock screen is up; leaving it locks Windows (PIN / password).
    public bool IsSecure => IsVisible && Presentation == Presentation.Lock;

    public event Action? VisibilityChanged;

    public LockScreenController(MediaController media, LyricsController lyrics, AppSettings settings)
    {
        this.media = media;
        this.lyrics = lyrics;
        this.settings = settings;
        watchdog.Tick += (_, _) => Watch();
        cursorTimer.Tick += (_, _) => UpdateCursor();
        keyboardGuard.Blocked += () => { if (IsVisible) Activate(); };
        media.PlaybackChanged += UpdateDisplayAwake;
        settings.PropertyChanged += OnSettingsChanged;
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(AppSettings.AwakeDurationIndex)) UpdateDisplayAwake();
        if (e.PropertyName == nameof(AppSettings.AllDisplays))
            foreach (var extra in extras) extra.View.WidgetHidden = !settings.AllDisplays;
    }

    // MARK: Show / hide

    public void Show(Presentation presentation)
    {
        if (IsVisible)
        {
            // Preview → lock: upgrade in place.
            if (presentation == Presentation.Lock && Presentation == Presentation.Preview) SetPresentation(presentation);
            Activate();
            return;
        }
        unlocking = false;
        var screens = Forms.Screen.AllScreens;
        var primary = Forms.Screen.PrimaryScreen ?? screens[0];

        EnsureMainWindow();
        main!.Underlay.Source = settings.LiteMode ? null : Capture(primary);
        main.Cover(primary);
        view!.ResetOpacity();
        SetPresentation(presentation);

        // The other monitors: the widget too ("Show on all displays"), or just the backdrop.
        int index = 0;
        foreach (var screen in screens)
        {
            if (screen.DeviceName == primary.DeviceName) continue;
            if (index >= extras.Count)
            {
                var extraView = new LockScreenView(media, lyrics, settings);
                extraView.UnlockRequested += Unlock;
                extraView.CloseRequested += () => Hide();
                var extraWindow = new OverlayWindow(extraView);
                extraWindow.PreviewKeyDown += OnKey;
                extras.Add((extraWindow, extraView));
            }
            var (window, extra) = extras[index++];
            extra.ResetOpacity();
            extra.Presentation = presentation;
            extra.WidgetHidden = !settings.AllDisplays;
            window.Underlay.Source = settings.LiteMode ? null : Capture(screen);
            window.Cover(screen);
            window.Show();
            window.Reassert();
            extra.PlayEntrance();
        }
        for (int i = index; i < extras.Count; i++) extras[i].Window.Hide();

        main.Show();
        main.Reassert();
        view.PlayEntrance();
        Activate();

        IsVisible = true;
        shownAt = DateTime.UtcNow;
        lastMouseMove = DateTime.UtcNow;
        if (!rendering)
        {
            CompositionTarget.Rendering += OnRendering;
            rendering = true;
        }
        if (IsSecure) keyboardGuard.Start();
        // The desktop screenshot only shows during the entrance; drawing it
        // under everything afterwards would cost a full-screen layer per frame.
        var dropUnderlay = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.2) };
        dropUnderlay.Tick += (_, _) =>
        {
            dropUnderlay.Stop();
            if (main != null) main.Underlay.Source = null;
            foreach (var extra in extras) extra.Window.Underlay.Source = null;
        };
        dropUnderlay.Start();
        watchdog.Start();
        cursorTimer.Start();
        UpdateDisplayAwake();
        VisibilityChanged?.Invoke();
    }

    private void SetPresentation(Presentation presentation)
    {
        Presentation = presentation;
        if (view != null) view.Presentation = presentation;
        foreach (var extra in extras) extra.View.Presentation = presentation;
        if (IsSecure) keyboardGuard.Start(); else keyboardGuard.Stop();
    }

    /// Hides everything; `animated` fades out first.
    public void Hide(bool animated = true)
    {
        if (!IsVisible) return;
        IsVisible = false;
        keyboardGuard.Stop();
        watchdog.Stop();
        cursorTimer.Stop();
        ShowCursor();
        displayAwake.Release();
        void Finish()
        {
            if (IsVisible) return; // shown again meanwhile
            if (rendering)
            {
                CompositionTarget.Rendering -= OnRendering;
                rendering = false;
            }
            view?.Stop();
            main?.Hide();
            if (main != null) main.Underlay.Source = null;
            foreach (var extra in extras) { extra.View.Stop(); extra.Window.Hide(); extra.Window.Underlay.Source = null; }
        }
        if (animated && view != null)
        {
            foreach (var extra in extras) if (extra.Window.IsVisible) extra.View.PlayExit(() => { });
            view.PlayExit(Finish);
        }
        else Finish();
        VisibilityChanged?.Invoke();
    }

    /// Enter / click on the lock screen: lock Windows for real (PIN / password).
    private void Unlock()
    {
        if (Presentation == Presentation.Lock) LockWindows();
        else Hide();
    }

    /// Locks Windows for real; the LockTune windows go away once it's locked.
    public void LockWindows()
    {
        if (unlocking) return;
        unlocking = true;
        keyboardGuard.Stop();
        if (!Native.LockWorkStation())
        {
            // Locking is disabled by policy: nothing more we can do.
            Hide();
            return;
        }
        // Keep covering the desktop until the Windows lock screen is up.
        var fallback = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        fallback.Tick += (_, _) => { fallback.Stop(); Hide(false); };
        fallback.Start();
    }

    /// Windows itself got locked (Win+L, LockWorkStation, …).
    public void OnSessionLocked() => Hide(false);

    // MARK: Internals

    private void EnsureMainWindow()
    {
        if (main != null) return;
        view = new LockScreenView(media, lyrics, settings);
        view.UnlockRequested += Unlock;
        view.CloseRequested += () => Hide();
        main = new OverlayWindow(view);
        main.PreviewKeyDown += OnKey;
        main.MouseMove += (_, e) =>
        {
            var p = e.GetPosition(main);
            if ((p - lastMousePosition).Length < 2) return;
            lastMousePosition = p;
            lastMouseMove = DateTime.UtcNow;
            ShowCursor();
        };
        main.Deactivated += (_, _) =>
        {
            if (!IsSecure) return;
            var check = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            check.Tick += (_, _) => { check.Stop(); Watch(); };
            check.Start();
        };
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape && Presentation == Presentation.Preview) { Hide(); e.Handled = true; return; }
        if (view != null && view.HandleKey(key)) e.Handled = true;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        view?.Tick();
        foreach (var extra in extras)
            if (extra.Window.IsVisible && !extra.View.WidgetHidden) extra.View.Tick();
    }

    private void Activate()
    {
        if (main == null) return;
        var hwnd = main.Handle;
        // Windows only lets the foreground app hand out focus; borrow its input
        // queue for a moment (needed when the idle timer opens the lock screen).
        var foreground = Native.GetForegroundWindow();
        uint foregroundThread = Native.GetWindowThreadProcessId(foreground, out _);
        uint ourThread = GetCurrentThreadId();
        bool attached = foregroundThread != 0 && foregroundThread != ourThread && AttachThreadInput(ourThread, foregroundThread, true);
        main.Activate();
        Native.SetForegroundWindow(hwnd);
        main.Focus();
        Keyboard.Focus(main);
        if (attached) AttachThreadInput(ourThread, foregroundThread, false);
    }

    /// Secure mode: if something else got the foreground, lock Windows.
    private void Watch()
    {
        if (!IsSecure || unlocking) return;
        foreach (var w in extras) if (w.Window.IsVisible) w.Window.Reassert();
        main?.Reassert();
        if (Native.ForegroundIsOursOrNone(out _)) return;
        // A short grace period right after showing, while we take focus.
        if ((DateTime.UtcNow - shownAt).TotalSeconds < 1.5)
        {
            Activate();
            return;
        }
        LockWindows();
    }

    /// Keeps the display on while the lock screen is up and music plays.
    private void UpdateDisplayAwake()
    {
        double seconds = settings.AwakeDuration;
        if (!IsSecure || seconds <= 0 || !media.IsPlaying)
        {
            displayAwake.Release();
            return;
        }
        displayAwake.Hold(double.IsInfinity(seconds) ? null : shownAt.AddSeconds(seconds));
    }

    private void UpdateCursor()
    {
        if (!IsVisible) return;
        if (!cursorHidden && (DateTime.UtcNow - lastMouseMove).TotalSeconds > 3)
        {
            cursorHidden = true;
            if (main != null) main.Cursor = Cursors.None;
        }
    }

    private void ShowCursor()
    {
        if (!cursorHidden) return;
        cursorHidden = false;
        if (main != null) main.Cursor = null;
    }

    /// Screenshot of a monitor, shown under the lock screen while it fades in.
    private static BitmapSource? Capture(Forms.Screen screen)
    {
        try
        {
            var b = screen.Bounds;
            using var bitmap = new System.Drawing.Bitmap(b.Width, b.Height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
            using (var g = System.Drawing.Graphics.FromImage(bitmap))
                g.CopyFromScreen(b.Left, b.Top, 0, 0, b.Size, System.Drawing.CopyPixelOperation.SourceCopy);
            var hbitmap = bitmap.GetHbitmap();
            try
            {
                var source = Imaging.CreateBitmapSourceFromHBitmap(hbitmap, IntPtr.Zero, Int32Rect.Empty,
                                                                   BitmapSizeOptions.FromEmptyOptions());
                source.Freeze();
                return source;
            }
            finally
            {
                DeleteObject(hbitmap);
            }
        }
        catch (Exception)
        {
            return null;
        }
    }

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
}
