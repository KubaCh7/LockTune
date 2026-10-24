using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using LockTune.Core;
using LockTune.Lyrics;
using LockTune.Media;
using LockTune.Platform;
using LockTune.UI;

namespace LockTune;

/// Wires the services together: now playing, lyrics, the lock screen and its
/// triggers (shortcut, tray, idle), the turntable and updates.
public sealed class AppCoordinator
{
    public AppSettings Settings { get; }
    public MediaController Media { get; }
    public LyricsController Lyrics { get; }
    public LockScreenController LockScreen { get; }
    public Updater Updater { get; }
    public TurntableWindow? Turntable { get; private set; }
    public bool HotkeyAvailable { get; private set; } = true;
    public string? LockImageStatus { get; private set; }

    /// Something shown on the Status page changed.
    public event Action? StatusChanged;

    private readonly HotkeyManager hotkeys = new();
    private readonly IdleMonitor idle = new();
    private TrayIcon? tray;
    private SettingsWindow? settingsWindow;
    private DispatcherTimer? lockImageTimer;

    public static int Build => Assembly.GetEntryAssembly()?.GetName().Version?.Build ?? 0;
    public static string VersionString
    {
        get
        {
            var v = Assembly.GetEntryAssembly()?.GetName().Version;
            return v == null ? "dev" : $"{v.Major}.{v.Minor} (build {v.Build})";
        }
    }

    public AppCoordinator(AppSettings settings)
    {
        Settings = settings;
        Media = new MediaController(settings);
        Lyrics = new LyricsController(Media, settings);
        LockScreen = new LockScreenController(Media, Lyrics, settings);
        Updater = new Updater(settings);
    }

    public void Start(bool background, bool updated)
    {
        _ = Media.StartAsync();
        Media.TrackChanged += OnStatus;
        Media.PlaybackChanged += OnStatus;
        Media.ArtworkChanged += OnArtworkChanged;
        Lyrics.PropertyChanged += (_, _) => OnStatus();
        Updater.StatusChanged += OnStatus;
        Updater.Found += OnUpdateFound;

        tray = new TrayIcon(this);

        hotkeys.Pressed += LockNow;
        ApplyHotkey();
        idle.Idle += () =>
        {
            if (Media.IsPlaying && !LockScreen.IsVisible) LockNow();
        };
        ApplyIdle();
        idle.Start();
        Settings.PropertyChanged += OnSettingsChanged;

        SystemEvents.SessionSwitch += OnSessionSwitch;
        LaunchAtLogin.Refresh();
        UpdateTurntable();
        Updater.Start();

        if (!Settings.FirstRunDone)
        {
            Settings.FirstRunDone = true;
            Settings.Save();
            OpenSettings();
            tray.Notify("LockTune is running", $"It lives in the taskbar corner. Press {Settings.Hotkey} to show your music lock screen.");
        }
        else if (updated)
            tray.Notify("LockTune updated", $"You're on {VersionString}.");
        else if (!background)
            OpenSettings();
    }

    // MARK: Actions

    /// Shortcut / tray / idle: show the LockTune lock screen (or just lock
    /// Windows when nothing is playing).
    public void LockNow()
    {
        if (!Media.HasTrack)
        {
            Native.LockWorkStation();
            return;
        }
        settingsWindow?.Close();
        LockScreen.Show(Presentation.Lock);
    }

    /// Same screen, closes with Esc / a click — no password needed.
    public void ShowPreview() => LockScreen.Show(Presentation.Preview);

    public void OpenSettings(int tab = -1)
    {
        if (LockScreen.IsSecure) return;
        if (settingsWindow == null)
        {
            settingsWindow = new SettingsWindow(this);
            settingsWindow.Closed += (_, _) => settingsWindow = null;
            settingsWindow.Show();
        }
        if (tab >= 0) settingsWindow.Select(tab, true);
        if (settingsWindow.WindowState == WindowState.Minimized) settingsWindow.WindowState = WindowState.Normal;
        settingsWindow.Activate();
    }

    public void SuspendHotkey() => hotkeys.Unregister();
    public void ResumeHotkey() => ApplyHotkey();

    public void Quit()
    {
        OverlayWindow.AllowClose = true;
        LockScreen.Hide(false);
        tray?.Dispose();
        hotkeys.Dispose();
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        Settings.Save();
        Application.Current.Shutdown();
    }

    public static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception) { }
    }

    // MARK: Events

    private void OnStatus() => StatusChanged?.Invoke();

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (e.Reason is SessionSwitchReason.SessionLock or SessionSwitchReason.ConsoleDisconnect or SessionSwitchReason.RemoteDisconnect)
                LockScreen.OnSessionLocked();
        });
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppSettings.Hotkey):
            case nameof(AppSettings.HotkeyEnabled):
                ApplyHotkey();
                tray?.Refresh();
                break;
            case nameof(AppSettings.IdleEnabled):
            case nameof(AppSettings.IdleMinutes):
                ApplyIdle();
                break;
            case nameof(AppSettings.TurntableEnabled):
                UpdateTurntable();
                tray?.Refresh();
                break;
            case nameof(AppSettings.WindowsLockImage):
                if (Settings.WindowsLockImage) OnArtworkChanged();
                else LockImageStatus = null;
                break;
        }
    }

    private void ApplyHotkey()
    {
        if (Settings.HotkeyEnabled) HotkeyAvailable = hotkeys.Register(Settings.Hotkey);
        else
        {
            hotkeys.Unregister();
            HotkeyAvailable = true;
        }
        OnStatus();
    }

    private void ApplyIdle()
    {
        idle.IsEnabled = Settings.IdleEnabled;
        idle.Threshold = TimeSpan.FromMinutes(Math.Max(1, Settings.IdleMinutes));
    }

    private void UpdateTurntable()
    {
        if (Settings.TurntableEnabled)
        {
            Turntable ??= new TurntableWindow(Media, Settings);
            Turntable.ShowWidget();
        }
        else Turntable?.Hide();
    }

    private void OnArtworkChanged()
    {
        OnStatus();
        if (!Settings.WindowsLockImage) return;
        // Debounced: skipping through songs shouldn't rewrite the picture each time.
        lockImageTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        lockImageTimer.Stop();
        lockImageTimer.Tick -= OnLockImageTimer;
        lockImageTimer.Tick += OnLockImageTimer;
        lockImageTimer.Start();
    }

    private async void OnLockImageTimer(object? sender, EventArgs e)
    {
        lockImageTimer?.Stop();
        if (!Settings.WindowsLockImage || Media.Artwork == null) return;
        LockImageStatus = await WindowsLockImage.SetAsync(Media);
        OnStatus();
    }

    private async void OnUpdateFound(int build, bool userInitiated)
    {
        if (LockScreen.IsVisible) return;
        if (!userInitiated)
        {
            tray?.Notify("LockTune update available", $"Build {build} is ready. Open Settings → Status, or click here to install.", install: true);
            return;
        }
        var answer = MessageBox.Show($"LockTune build {build} is available (you have {VersionString}).\n\nInstall it now? LockTune restarts by itself.",
                                     "LockTune update", MessageBoxButton.YesNo, MessageBoxImage.Information);
        if (answer == MessageBoxResult.Yes) await Updater.InstallAsync();
    }

    public async void InstallUpdate() => await Updater.InstallAsync();
}
