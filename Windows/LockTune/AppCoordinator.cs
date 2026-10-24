using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using Microsoft.Win32;
using LockTune.Core;
using LockTune.Lyrics;
using LockTune.Media;
using LockTune.Platform;
using LockTune.UI;

namespace LockTune;

/// Wires the services together (AppCoordinator.swift): now playing, lyrics,
/// the lock screen and its triggers (shortcut, menu, idle), the turntable
/// and updates.
public sealed class AppCoordinator
{
    public AppSettings Settings { get; }
    public MediaController Media { get; }
    public LyricsController Lyrics { get; }
    public LockScreenController LockScreen { get; }
    public Updater Updater { get; }
    public TurntableWindow? Turntable { get; private set; }
    /// The shortcut could not be registered (already used by another app).
    public bool HotkeyUnavailable { get; private set; }

    /// Something shown on the Status page changed.
    public event Action? StatusChanged;

    private readonly HotkeyManager hotkeys = new();
    private readonly IdleMonitor idle = new();
    private TrayIcon? tray;
    private SettingsWindow? settingsWindow;

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
        Media.ArtworkChanged += OnStatus;
        Lyrics.PropertyChanged += (_, _) => OnStatus();
        Updater.StatusChanged += OnStatus;
        Updater.Found += OnUpdateFound;

        tray = new TrayIcon(this);

        // Global shortcut: lock (the widget then appears).
        hotkeys.Pressed += LockNow;
        ApplyHotkey();
        // Optional: lock after being idle while music plays.
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

        // First launch: show the status window so it's obvious the app runs
        // (the tray icon can hide under the ^ arrow).
        if (!Settings.DidShowWelcome)
        {
            Settings.DidShowWelcome = true;
            Settings.Save();
            OpenSettings();
        }
        else if (updated)
            tray.Notify("LockTune updated", $"You're on {VersionString}.");
        else if (!background)
            OpenSettings();
    }

    // MARK: Actions

    /// Shows the LockTune lock screen (leaving it locks Windows). With the
    /// widget turned off or nothing playing, it just locks Windows.
    public void LockNow()
    {
        if (!Settings.LockWidgetEnabled || !Media.HasTrack)
        {
            Native.LockWorkStation();
            return;
        }
        settingsWindow?.Close();
        LockScreen.Show(Presentation.Lock);
    }

    public void TogglePreview()
    {
        if (LockScreen.IsVisible && LockScreen.Presentation == Presentation.Preview) LockScreen.Hide();
        else LockScreen.Show(Presentation.Preview);
    }

    public void OpenSettings()
    {
        if (LockScreen.IsSecure) return;
        if (settingsWindow == null)
        {
            settingsWindow = new SettingsWindow(this);
            settingsWindow.Closed += (_, _) => settingsWindow = null;
            settingsWindow.Show();
        }
        if (settingsWindow.WindowState == WindowState.Minimized) settingsWindow.WindowState = WindowState.Normal;
        settingsWindow.Activate();
    }

    /// The settings recorder needs the current shortcut to reach it, not us.
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

    public async void InstallUpdate() => await Updater.InstallAsync();

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
                break;
            case nameof(AppSettings.IdleEnabled):
            case nameof(AppSettings.IdleMinutes):
                ApplyIdle();
                break;
            case nameof(AppSettings.TurntableEnabled):
                UpdateTurntable();
                break;
        }
    }

    private void ApplyHotkey()
    {
        if (Settings.HotkeyEnabled) HotkeyUnavailable = !hotkeys.Register(Settings.Hotkey);
        else
        {
            hotkeys.Unregister();
            HotkeyUnavailable = false;
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

    private async void OnUpdateFound(int build, bool userInitiated)
    {
        if (LockScreen.IsVisible) return;
        var answer = MessageBox.Show($"LockTune build {build} is available (you have {VersionString}).\n\nUpdate now? LockTune restarts by itself.",
                                     "LockTune update", MessageBoxButton.YesNo, MessageBoxImage.Information);
        if (answer == MessageBoxResult.Yes) await Updater.InstallAsync();
    }
}
