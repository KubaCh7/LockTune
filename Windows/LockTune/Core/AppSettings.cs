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

// Same options, names, ranges and defaults as the Mac app (AppSettings.swift).

/// Fonts for the clock and the lyrics. Each one uses the Mac typeface when
/// it's installed, otherwise the closest font that ships with Windows.
public enum FontChoice
{
    SfPro, Rounded, NewYork, Mono,
    HelveticaNeue, AvenirNext, Futura, GillSans, Optima,
    Didot, Baskerville, Georgia, Bodoni,
    AmericanTypewriter, Chalkboard, MarkerFelt,
}

public enum ClockWeight { UltraLight, Thin, Light, Regular, Medium, Semibold, Bold }

public enum BackgroundStyle
{
    /// Heavily blurred, slowly drifting artwork (Apple Music style).
    Blurred,
    /// The artwork itself, scaled to fill the screen like a wallpaper.
    Artwork,
}

public enum LyricsPosition
{
    /// Artwork + controls on the left, lyrics on the right.
    Right,
    /// Lyrics on the left, artwork + controls on the right.
    Left,
    /// Large centred lyrics, small artwork + controls underneath.
    Center,
}

public enum PreferredSource { Automatic, Music, Spotify }

/// How much Liquid Glass the widget uses.
public enum GlassLevel { Off, Normal, Mega }

public static class Titles
{
    public static string Of(FontChoice f) => f switch
    {
        FontChoice.SfPro => "SF Pro",
        FontChoice.Rounded => "SF Pro Rounded",
        FontChoice.NewYork => "New York (serif)",
        FontChoice.Mono => "SF Mono",
        FontChoice.HelveticaNeue => "Helvetica Neue",
        FontChoice.AvenirNext => "Avenir Next",
        FontChoice.Futura => "Futura",
        FontChoice.GillSans => "Gill Sans",
        FontChoice.Optima => "Optima",
        FontChoice.Didot => "Didot",
        FontChoice.Baskerville => "Baskerville",
        FontChoice.Georgia => "Georgia",
        FontChoice.Bodoni => "Bodoni 72",
        FontChoice.AmericanTypewriter => "American Typewriter",
        FontChoice.Chalkboard => "Chalkboard",
        _ => "Marker Felt",
    };

    /// Font family list: the Mac font first, then Windows look-alikes.
    public static string Family(FontChoice f) => f switch
    {
        FontChoice.SfPro => "SF Pro Display, Segoe UI Variable Display, Segoe UI",
        FontChoice.Rounded => "SF Pro Rounded, Arial Rounded MT Bold, Segoe UI Variable Display, Segoe UI",
        FontChoice.NewYork => "New York, Sitka Display, Georgia",
        FontChoice.Mono => "SF Mono, Cascadia Mono, Consolas",
        FontChoice.HelveticaNeue => "Helvetica Neue, Helvetica, Arial",
        FontChoice.AvenirNext => "Avenir Next, Century Gothic, Segoe UI",
        FontChoice.Futura => "Futura, Century Gothic, Segoe UI",
        FontChoice.GillSans => "Gill Sans, Gill Sans MT, Calibri",
        FontChoice.Optima => "Optima, Candara, Segoe UI",
        FontChoice.Didot => "Didot, Bodoni MT, Times New Roman",
        FontChoice.Baskerville => "Baskerville, Baskerville Old Face, Palatino Linotype",
        FontChoice.Georgia => "Georgia",
        FontChoice.Bodoni => "Bodoni 72, Bodoni MT, Times New Roman",
        FontChoice.AmericanTypewriter => "American Typewriter, Courier New",
        FontChoice.Chalkboard => "Chalkboard SE, Comic Sans MS",
        _ => "Marker Felt, Ink Free, Comic Sans MS",
    };

    public static string Of(ClockWeight w) => w switch
    {
        ClockWeight.UltraLight => "UltraLight",
        _ => w.ToString(),
    };

    public static string Of(BackgroundStyle s) => s == BackgroundStyle.Blurred ? "Blurred & animated" : "Artwork wallpaper";

    public static string Of(LyricsPosition p) => p switch
    {
        LyricsPosition.Right => "Right of the artwork",
        LyricsPosition.Left => "Left of the artwork",
        _ => "Centered (big)",
    };

    public static string Of(PreferredSource s) => s switch
    {
        PreferredSource.Automatic => "Automatic",
        PreferredSource.Music => "Apple Music only",
        _ => "Spotify only",
    };

    public static string Of(GlassLevel g) => g switch
    {
        GlassLevel.Off => "Off",
        GlassLevel.Normal => "Liquid Glass",
        _ => "Mega (Experimental)",
    };
}

/// User preferences, saved as JSON in %APPDATA%\LockTune\settings.json.
public sealed class AppSettings : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    // Slider ranges, centred on the defaults.
    public static readonly (double Min, double Max) WallpaperLyricsRange = (-0.18, 0.55);
    public static readonly (double Min, double Max) PlainLyricsRange = (-0.25, 0.45);
    /// Bottom edge of artwork + controls, as a fraction of the screen height.
    public static readonly (double Min, double Max) PlayerBottomRange = (0.48, 0.96);
    public static readonly (double Min, double Max) LyricsLeadRange = (0, 3);
    public const double MaxArtworkScale = 2.5;
    public static double MinBottomInset => 1 - PlayerBottomRange.Max;
    public static readonly int[] FpsOptions = { 30, 60, 120 };

    /// Seconds; 0 = Windows default, infinity = while music plays.
    public static readonly double[] AwakeDurations = { 0, 30, 60, 120, 300, 600, 900, 1800, 3600, double.PositiveInfinity };

    public static string AwakeTitle(double seconds)
    {
        if (seconds == 0) return "Windows default";
        if (double.IsInfinity(seconds)) return "Whole time music plays";
        return seconds < 60 ? $"{(int)seconds} s" : $"{(int)(seconds / 60)} min";
    }

    private bool hotkeyEnabled = true;
    private uint hotkeyModifiers = Hotkey.Default.Modifiers;
    private uint hotkeyKey = Hotkey.Default.VirtualKey;
    private bool idleEnabled;
    private int idleMinutes = 5;
    private bool lockWidgetEnabled = true;
    private bool lockWallpaper = true;
    private FontChoice clockFont = FontChoice.NewYork;
    private ClockWeight clockWeight = ClockWeight.Regular;
    private BackgroundStyle backgroundStyle = BackgroundStyle.Blurred;
    private LyricsPosition lyricsPosition = LyricsPosition.Right;
    private double widgetBottomInset = 0.253;
    private int awakeDurationIndex = 9;
    private bool wordByWord = true;
    private double artworkScale = 2.5;
    private double plainBottomInset = 0.28;
    private double plainArtworkScale = 2.0;
    private bool turntableEnabled;
    private double turntableScale = 1;
    private double? turntableLeft, turntableTop;
    private bool liteMode;
    private int animationFPS = 60;
    private GlassLevel glassLevel = GlassLevel.Normal;
    private double lyricsOffset = 0.184;
    private double plainLyricsOffset = 0.10;
    private FontChoice lyricsFont = FontChoice.SfPro;
    private ClockWeight lyricsWeight = ClockWeight.Bold;
    private double lyricsScale = 0.85;
    private double lyricsLead = 1.5;
    private bool lyricsEnabled = true;
    private PreferredSource preferredSource = PreferredSource.Automatic;
    private bool allDisplays = true;
    private bool autoCheckUpdates = true;
    private bool didShowWelcome;

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
    public bool IdleEnabled { get => idleEnabled; set => Set(ref idleEnabled, value); }
    public int IdleMinutes { get => idleMinutes; set => Set(ref idleMinutes, Math.Clamp(value, 1, 120)); }
    /// Show the widget when you lock.
    public bool LockWidgetEnabled { get => lockWidgetEnabled; set => Set(ref lockWidgetEnabled, value); }
    /// Full-screen artwork + clock behind the widget.
    public bool LockWallpaper { get => lockWallpaper; set => Set(ref lockWallpaper, value); }
    public FontChoice ClockFont { get => clockFont; set => Set(ref clockFont, value); }
    public ClockWeight ClockWeight { get => clockWeight; set => Set(ref clockWeight, value); }
    public BackgroundStyle BackgroundStyle { get => backgroundStyle; set => Set(ref backgroundStyle, value); }
    public LyricsPosition LyricsPosition { get => lyricsPosition; set => Set(ref lyricsPosition, value); }
    /// Gap between the bottom of the widget and the bottom of the screen (fraction of the height).
    public double WidgetBottomInset { get => widgetBottomInset; set => Set(ref widgetBottomInset, Math.Clamp(value, MinBottomInset, 0.6)); }
    /// How long the display stays on after locking (index into AwakeDurations).
    public int AwakeDurationIndex { get => awakeDurationIndex; set => Set(ref awakeDurationIndex, Math.Clamp(value, 0, AwakeDurations.Length - 1)); }
    [JsonIgnore] public double AwakeDuration => AwakeDurations[Math.Clamp(awakeDurationIndex, 0, AwakeDurations.Length - 1)];
    /// Apple Music-style word-by-word highlight of the current lyric line.
    public bool WordByWord { get => wordByWord; set => Set(ref wordByWord, value); }
    /// Artwork size with the artwork wallpaper on.
    public double ArtworkScale { get => artworkScale; set => Set(ref artworkScale, Math.Clamp(value, 0.6, MaxArtworkScale)); }
    /// Same two settings for the plain mode (over the normal wallpaper).
    public double PlainBottomInset { get => plainBottomInset; set => Set(ref plainBottomInset, Math.Clamp(value, MinBottomInset, 0.6)); }
    public double PlainArtworkScale { get => plainArtworkScale; set => Set(ref plainArtworkScale, Math.Clamp(value, 0.6, MaxArtworkScale)); }
    /// Turntable widget on the desktop (off by default) and its size.
    public bool TurntableEnabled { get => turntableEnabled; set => Set(ref turntableEnabled, value); }
    public double TurntableScale { get => turntableScale; set => Set(ref turntableScale, Math.Clamp(value, 0.6, 1.5)); }
    public double? TurntableLeft { get => turntableLeft; set => Set(ref turntableLeft, value); }
    public double? TurntableTop { get => turntableTop; set => Set(ref turntableTop, value); }
    /// Lite mode for slow PCs: everything still works, the heavy effects are off.
    public bool LiteMode { get => liteMode; set => Set(ref liteMode, value); }
    /// 30, 60 or 120 frames per second for the continuous animations.
    public int AnimationFPS { get => animationFPS; set => Set(ref animationFPS, Array.IndexOf(FpsOptions, value) >= 0 ? value : 60); }
    [JsonIgnore] public double FrameInterval => liteMode ? 1.0 / 30 : 1.0 / Math.Max(animationFPS, 1);
    /// Word-by-word needs a redraw every frame; Lite mode lights whole lines.
    [JsonIgnore] public bool EffectiveWordByWord => wordByWord && !liteMode;
    public GlassLevel GlassLevel { get => glassLevel; set => Set(ref glassLevel, value); }
    /// Moves the lyrics block down (fraction of the screen height), per mode.
    public double LyricsOffset { get => lyricsOffset; set => Set(ref lyricsOffset, Math.Clamp(value, WallpaperLyricsRange.Min, WallpaperLyricsRange.Max)); }
    public double PlainLyricsOffset { get => plainLyricsOffset; set => Set(ref plainLyricsOffset, Math.Clamp(value, PlainLyricsRange.Min, PlainLyricsRange.Max)); }
    public FontChoice LyricsFont { get => lyricsFont; set => Set(ref lyricsFont, value); }
    public ClockWeight LyricsWeight { get => lyricsWeight; set => Set(ref lyricsWeight, value); }
    /// Lyrics text size multiplier (0.6 … 1.4).
    public double LyricsScale { get => lyricsScale; set => Set(ref lyricsScale, Math.Clamp(value, 0.6, 1.4)); }
    /// How many seconds early lyrics are highlighted (0 … 3, 1.5 = middle).
    public double LyricsLead { get => lyricsLead; set => Set(ref lyricsLead, Math.Clamp(value, LyricsLeadRange.Min, LyricsLeadRange.Max)); }
    public bool LyricsEnabled { get => lyricsEnabled; set => Set(ref lyricsEnabled, value); }
    public PreferredSource PreferredSource { get => preferredSource; set => Set(ref preferredSource, value); }
    public bool AllDisplays { get => allDisplays; set => Set(ref allDisplays, value); }
    public bool AutoCheckUpdates { get => autoCheckUpdates; set => Set(ref autoCheckUpdates, value); }
    public bool DidShowWelcome { get => didShowWelcome; set => Set(ref didShowWelcome, value); }

    // Values for the current mode.
    [JsonIgnore] public double ActiveBottomInset => Math.Max(lockWallpaper ? widgetBottomInset : plainBottomInset, MinBottomInset);
    [JsonIgnore] public double ActiveArtworkScale => lockWallpaper ? artworkScale : plainArtworkScale;
    [JsonIgnore] public double ActiveLyricsOffset => lockWallpaper ? lyricsOffset : plainLyricsOffset;

    public void ResetLayout()
    {
        WidgetBottomInset = 0.253;
        ArtworkScale = 2.5;
        PlainBottomInset = 0.28;
        PlainArtworkScale = 2.0;
        LyricsScale = 0.85;
        LyricsOffset = 0.184;
        PlainLyricsOffset = 0.10;
    }

    // MARK: Presets (same JSON as the Mac app, so presets work on both)

    private sealed class Preset
    {
        [JsonPropertyName("format")] public string? Format { get; set; }
        [JsonPropertyName("version")] public int Version { get; set; } = 1;
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("lockWallpaper")] public bool? LockWallpaper { get; set; }
        [JsonPropertyName("backgroundStyle")] public string? BackgroundStyle { get; set; }
        [JsonPropertyName("widgetBottomInset")] public double? WidgetBottomInset { get; set; }
        [JsonPropertyName("artworkScale")] public double? ArtworkScale { get; set; }
        [JsonPropertyName("lyricsOffset")] public double? LyricsOffset { get; set; }
        [JsonPropertyName("plainBottomInset")] public double? PlainBottomInset { get; set; }
        [JsonPropertyName("plainArtworkScale")] public double? PlainArtworkScale { get; set; }
        [JsonPropertyName("plainLyricsOffset")] public double? PlainLyricsOffset { get; set; }
        [JsonPropertyName("lyricsEnabled")] public bool? LyricsEnabled { get; set; }
        [JsonPropertyName("lyricsPosition")] public string? LyricsPosition { get; set; }
        [JsonPropertyName("lyricsScale")] public double? LyricsScale { get; set; }
        [JsonPropertyName("lyricsLead")] public double? LyricsLead { get; set; }
        [JsonPropertyName("wordByWord")] public bool? WordByWord { get; set; }
        [JsonPropertyName("clockFont")] public string? ClockFont { get; set; }
        [JsonPropertyName("clockWeight")] public string? ClockWeight { get; set; }
        [JsonPropertyName("lyricsFont")] public string? LyricsFont { get; set; }
        [JsonPropertyName("lyricsWeight")] public string? LyricsWeight { get; set; }
        [JsonPropertyName("glassLevel")] public string? GlassLevel { get; set; }
        [JsonPropertyName("animationFPS")] public int? AnimationFPS { get; set; }
        [JsonPropertyName("awakeDurationIndex")] public int? AwakeDurationIndex { get; set; }
    }

    /// FontChoice.NewYork → "newYork" (the Mac's raw values).
    public static string Raw<T>(T value) where T : struct, Enum
    {
        var s = value.ToString();
        return char.ToLowerInvariant(s[0]) + s[1..];
    }

    private static T? Parse<T>(string? raw) where T : struct, Enum =>
        raw != null && Enum.TryParse<T>(raw, ignoreCase: true, out var v) && Enum.IsDefined(v) ? v : null;

    public string ExportPreset(string? name)
    {
        var p = new Preset
        {
            Format = "LockTune preset",
            Name = name,
            LockWallpaper = lockWallpaper,
            BackgroundStyle = Raw(backgroundStyle),
            WidgetBottomInset = widgetBottomInset,
            ArtworkScale = artworkScale,
            LyricsOffset = lyricsOffset,
            PlainBottomInset = plainBottomInset,
            PlainArtworkScale = plainArtworkScale,
            PlainLyricsOffset = plainLyricsOffset,
            LyricsEnabled = lyricsEnabled,
            LyricsPosition = Raw(lyricsPosition),
            LyricsScale = lyricsScale,
            LyricsLead = lyricsLead,
            WordByWord = wordByWord,
            ClockFont = Raw(clockFont),
            ClockWeight = Raw(clockWeight),
            LyricsFont = Raw(lyricsFont),
            LyricsWeight = Raw(lyricsWeight),
            GlassLevel = Raw(glassLevel),
            AnimationFPS = animationFPS,
            AwakeDurationIndex = awakeDurationIndex,
        };
        return JsonSerializer.Serialize(p, new JsonSerializerOptions { WriteIndented = true });
    }

    /// Applies every value present (missing ones are kept). Returns false if
    /// it isn't a LockTune preset; `name` is the preset's name, if any.
    public bool ImportPreset(string json, out string? name)
    {
        name = null;
        Preset? p;
        try { p = JsonSerializer.Deserialize<Preset>(json); }
        catch (JsonException) { return false; }
        if (p == null || p.Format != "LockTune preset") return false;
        name = p.Name;
        if (p.LockWallpaper is { } lw) LockWallpaper = lw;
        if (Parse<BackgroundStyle>(p.BackgroundStyle) is { } bs) BackgroundStyle = bs;
        if (p.WidgetBottomInset is { } wbi) WidgetBottomInset = wbi;
        if (p.ArtworkScale is { } a) ArtworkScale = a;
        if (p.LyricsOffset is { } lo) LyricsOffset = lo;
        if (p.PlainBottomInset is { } pbi) PlainBottomInset = pbi;
        if (p.PlainArtworkScale is { } pa) PlainArtworkScale = pa;
        if (p.PlainLyricsOffset is { } plo) PlainLyricsOffset = plo;
        if (p.LyricsEnabled is { } le) LyricsEnabled = le;
        if (Parse<LyricsPosition>(p.LyricsPosition) is { } lp) LyricsPosition = lp;
        if (p.LyricsScale is { } ls) LyricsScale = ls;
        if (p.LyricsLead is { } lead) LyricsLead = lead;
        if (p.WordByWord is { } wbw) WordByWord = wbw;
        if (Parse<FontChoice>(p.ClockFont) is { } cf) ClockFont = cf;
        if (Parse<ClockWeight>(p.ClockWeight) is { } cw) ClockWeight = cw;
        if (Parse<FontChoice>(p.LyricsFont) is { } lf) LyricsFont = lf;
        if (Parse<ClockWeight>(p.LyricsWeight) is { } lwt) LyricsWeight = lwt;
        if (Parse<GlassLevel>(p.GlassLevel) is { } gl) GlassLevel = gl;
        if (p.AnimationFPS is { } fps && Array.IndexOf(FpsOptions, fps) >= 0) AnimationFPS = fps;
        if (p.AwakeDurationIndex is { } adi) AwakeDurationIndex = adi;
        return true;
    }

    // MARK: Persistence

    public static string Folder { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LockTune");
    private static string FilePath => Path.Combine(Folder, "settings.json");

    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
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
            // Unreadable (or from an older build): start from the defaults.
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
