using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Threading;
using LockTune.Platform;

namespace LockTune.Core;

public enum BackgroundStyle { Blurred, Artwork, Wallpaper }
public enum GlassLevel { Off, Normal, Mega }
public enum LyricsPosition { Right, Left, Center }
public enum PreferredSource { Auto, Spotify, AppleMusic }

/// Fonts offered for the clock and the lyrics (missing ones fall back to Segoe UI).
public static class FontChoices
{
    public static readonly string[] All =
    {
        "Segoe UI Variable Display", "Segoe UI", "Bahnschrift", "Arial", "Arial Rounded MT Bold", "Verdana",
        "Trebuchet MS", "Georgia", "Times New Roman", "Cambria", "Consolas", "Cascadia Code", "Impact",
        "Comic Sans MS", "Ink Free", "Segoe Print",
    };

    public static readonly string[] Weights = { "Light", "Regular", "Medium", "SemiBold", "Bold", "Black" };
}

/// All user settings, saved as JSON in %APPDATA%\LockTune\settings.json.
public sealed class AppSettings : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    // MARK: General
    private bool hotkeyEnabled = true;
    private uint hotkeyModifiers = Hotkey.Default.Modifiers;
    private uint hotkeyKey = Hotkey.Default.VirtualKey;
    private bool requirePassword = true;
    private bool idleEnabled;
    private int idleMinutes = 5;
    private int awakeMinutes = 10;
    private PreferredSource preferredSource = PreferredSource.Auto;
    private bool windowsLockImage;
    private bool autoUpdate = true;

    public bool HotkeyEnabled { get => hotkeyEnabled; set => Set(ref hotkeyEnabled, value); }
    public uint HotkeyModifiers { get => hotkeyModifiers; set => Set(ref hotkeyModifiers, value, nameof(Hotkey)); }
    public uint HotkeyKey { get => hotkeyKey; set => Set(ref hotkeyKey, value, nameof(Hotkey)); }
    [JsonIgnore] public Hotkey Hotkey
    {
        get => new(hotkeyModifiers, hotkeyKey);
        set
        {
            if (value == Hotkey) return;
            hotkeyModifiers = value.Modifiers;
            hotkeyKey = value.VirtualKey;
            Changed(nameof(Hotkey));
        }
    }
    /// Leaving the LockTune lock screen locks Windows (PIN / password).
    public bool RequirePassword { get => requirePassword; set => Set(ref requirePassword, value); }
    public bool IdleEnabled { get => idleEnabled; set => Set(ref idleEnabled, value); }
    public int IdleMinutes { get => idleMinutes; set => Set(ref idleMinutes, Math.Clamp(value, 1, 60)); }
    /// 0 = system default, -1 = always while music plays, otherwise minutes.
    public int AwakeMinutes { get => awakeMinutes; set => Set(ref awakeMinutes, value); }
    public PreferredSource PreferredSource { get => preferredSource; set => Set(ref preferredSource, value); }
    /// Experimental: put the album cover on the real Windows lock screen.
    public bool WindowsLockImage { get => windowsLockImage; set => Set(ref windowsLockImage, value); }
    public bool AutoUpdate { get => autoUpdate; set => Set(ref autoUpdate, value); }

    public static readonly int[] AwakeChoices = { 0, 1, 5, 10, 30, 60, -1 };
    public static string AwakeTitle(int minutes) => minutes switch
    {
        0 => "System default",
        -1 => "Always",
        60 => "1 h",
        _ => $"{minutes} min",
    };

    // MARK: Look
    private BackgroundStyle backgroundStyle = BackgroundStyle.Blurred;
    private GlassLevel glassLevel = GlassLevel.Normal;
    private bool liteMode;
    private string clockFont = "Segoe UI Variable Display";
    private string clockWeight = "SemiBold";
    private string lyricsFont = "Segoe UI Variable Display";
    private string lyricsWeight = "Bold";

    public BackgroundStyle BackgroundStyle { get => backgroundStyle; set => Set(ref backgroundStyle, value); }
    public GlassLevel GlassLevel { get => glassLevel; set => Set(ref glassLevel, value); }
    /// For slow PCs: no animated background, blur or shadows.
    public bool LiteMode { get => liteMode; set => Set(ref liteMode, value); }
    public string ClockFont { get => clockFont; set => Set(ref clockFont, value); }
    public string ClockWeight { get => clockWeight; set => Set(ref clockWeight, value); }
    public string LyricsFont { get => lyricsFont; set => Set(ref lyricsFont, value); }
    public string LyricsWeight { get => lyricsWeight; set => Set(ref lyricsWeight, value); }

    // MARK: Layout
    private double artworkScale = 1.0;
    private double playerHeight = 0.5;
    private double lyricsOffset = 0.5;
    private LyricsPosition lyricsPosition = LyricsPosition.Right;

    /// 0.5…2.5 × the default artwork size.
    public double ArtworkScale { get => artworkScale; set => Set(ref artworkScale, Math.Clamp(value, 0.5, 2.5)); }
    /// 0 = low on the screen, 1 = high.
    public double PlayerHeight { get => playerHeight; set => Set(ref playerHeight, Math.Clamp(value, 0, 1)); }
    /// 0 = high, 1 = low (0.5 = default, just under the clock).
    public double LyricsOffset { get => lyricsOffset; set => Set(ref lyricsOffset, Math.Clamp(value, 0, 1)); }
    public LyricsPosition LyricsPosition { get => lyricsPosition; set => Set(ref lyricsPosition, value); }

    // MARK: Lyrics
    private bool lyricsEnabled = true;
    private bool openWithLyrics = true;
    private bool wordByWord = true;
    private double lyricsLead = 1.5;
    private double lyricsScale = 1.0;

    public bool LyricsEnabled { get => lyricsEnabled; set => Set(ref lyricsEnabled, value); }
    public bool OpenWithLyrics { get => openWithLyrics; set => Set(ref openWithLyrics, value); }
    public bool WordByWord { get => wordByWord; set => Set(ref wordByWord, value); }
    /// Seconds the highlight runs ahead of playback (0…3, 1.5 = middle).
    public double LyricsLead { get => lyricsLead; set => Set(ref lyricsLead, Math.Clamp(value, 0, 3)); }
    public double LyricsScale { get => lyricsScale; set => Set(ref lyricsScale, Math.Clamp(value, 0.6, 1.6)); }
    [JsonIgnore] public bool EffectiveWordByWord => wordByWord && !liteMode;

    // MARK: Desktop turntable
    private bool turntableEnabled;
    private double turntableScale = 1.0;
    private double? turntableLeft;
    private double? turntableTop;

    public bool TurntableEnabled { get => turntableEnabled; set => Set(ref turntableEnabled, value); }
    public double TurntableScale { get => turntableScale; set => Set(ref turntableScale, Math.Clamp(value, 0.6, 1.8)); }
    public double? TurntableLeft { get => turntableLeft; set => Set(ref turntableLeft, value); }
    public double? TurntableTop { get => turntableTop; set => Set(ref turntableTop, value); }

    // MARK: Internal
    private bool firstRunDone;
    public bool FirstRunDone { get => firstRunDone; set => Set(ref firstRunDone, value); }

    // MARK: Layout defaults / presets

    public void ResetLayout()
    {
        ArtworkScale = 1.0;
        PlayerHeight = 0.5;
        LyricsOffset = 0.5;
        LyricsPosition = LyricsPosition.Right;
        LyricsScale = 1.0;
    }

    private static readonly string[] PresetKeys =
    {
        nameof(BackgroundStyle), nameof(GlassLevel), nameof(LiteMode), nameof(ClockFont), nameof(ClockWeight),
        nameof(LyricsFont), nameof(LyricsWeight), nameof(ArtworkScale), nameof(PlayerHeight), nameof(LyricsOffset),
        nameof(LyricsPosition), nameof(WordByWord), nameof(LyricsLead), nameof(LyricsScale), nameof(OpenWithLyrics),
        nameof(TurntableScale),
    };

    /// Look + layout settings as a shareable JSON preset.
    public string ExportPreset()
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(this, JsonOptions));
        var preset = new Dictionary<string, JsonElement>();
        foreach (var key in PresetKeys)
            if (doc.RootElement.TryGetProperty(key, out var value)) preset[key] = value.Clone();
        return JsonSerializer.Serialize(new Dictionary<string, object> { ["lockTunePreset"] = 1, ["settings"] = preset },
                                        JsonOptions);
    }

    /// Returns false if the file isn't a LockTune preset.
    public bool ImportPreset(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("settings", out var values)) return false;
            var current = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(this, JsonOptions), JsonOptions)!;
            using var merged = JsonDocument.Parse(JsonSerializer.Serialize(current, JsonOptions));
            var dict = new Dictionary<string, JsonElement>();
            foreach (var p in merged.RootElement.EnumerateObject()) dict[p.Name] = p.Value.Clone();
            foreach (var key in PresetKeys)
                if (values.TryGetProperty(key, out var v)) dict[key] = v.Clone();
            var imported = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(dict), JsonOptions);
            if (imported == null) return false;
            BackgroundStyle = imported.BackgroundStyle;
            GlassLevel = imported.GlassLevel;
            LiteMode = imported.LiteMode;
            ClockFont = imported.ClockFont;
            ClockWeight = imported.ClockWeight;
            LyricsFont = imported.LyricsFont;
            LyricsWeight = imported.LyricsWeight;
            ArtworkScale = imported.ArtworkScale;
            PlayerHeight = imported.PlayerHeight;
            LyricsOffset = imported.LyricsOffset;
            LyricsPosition = imported.LyricsPosition;
            WordByWord = imported.WordByWord;
            LyricsLead = imported.LyricsLead;
            LyricsScale = imported.LyricsScale;
            OpenWithLyrics = imported.OpenWithLyrics;
            TurntableScale = imported.TurntableScale;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    // MARK: Persistence

    public static string Folder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LockTune");
    private static string FilePath => Path.Combine(Folder, "settings.json");

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private DispatcherTimer? saveTimer;
    /// Only the instance loaded from disk saves itself (not copies / tests).
    private bool persist;
    [ThreadStatic] private static bool loading;

    public static AppSettings Load()
    {
        AppSettings? settings = null;
        loading = true;
        try
        {
            if (File.Exists(FilePath))
                settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions);
        }
        catch (Exception)
        {
            // Corrupt file: start fresh.
        }
        finally
        {
            loading = false;
        }
        settings ??= new AppSettings();
        settings.persist = true;
        return settings;
    }

    public void Save()
    {
        if (!persist) return;
        try
        {
            Directory.CreateDirectory(Folder);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, JsonOptions));
            File.Move(temp, FilePath, true);
        }
        catch (Exception)
        {
            // Not fatal; try again on the next change.
        }
    }

    private void ScheduleSave()
    {
        if (!persist || loading || Dispatcher.FromThread(System.Threading.Thread.CurrentThread) == null) return;
        if (saveTimer == null)
        {
            saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            saveTimer.Tick += (_, _) => { saveTimer.Stop(); Save(); };
        }
        saveTimer.Stop();
        saveTimer.Start();
    }

    private void Set<T>(ref T field, T value, string? also = null, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        Changed(name);
        if (also != null) Changed(also);
        if (name is nameof(WordByWord) or nameof(LiteMode)) Changed(nameof(EffectiveWordByWord));
    }

    private void Changed(string? name)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        ScheduleSave();
    }
}
