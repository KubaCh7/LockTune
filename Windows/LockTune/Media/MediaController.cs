using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LockTune.Core;
using Windows.Media.Control;
using GSession = Windows.Media.Control.GlobalSystemMediaTransportControlsSession;
using GManager = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager;

namespace LockTune.Media;

/// What's playing, from Windows' media sessions (the same source as the
/// volume flyout): Spotify, Apple Music, iTunes, browsers, … — no setup or
/// permissions needed.
public sealed class MediaController
{
    private readonly AppSettings settings;
    private readonly Dispatcher dispatcher;
    private readonly DispatcherTimer poll;
    private GManager? manager;
    private GSession? session;
    private int propertiesVersion;
    private byte[]? artworkBytes;

    // Position estimate: anchor + elapsed while playing.
    private double anchorPosition;
    private DateTime anchorTime = DateTime.UtcNow;
    private (double, long) lastTimelineSignature;

    public TrackInfo? Track { get; private set; }
    public bool HasTrack => Track != null;
    public bool IsPlaying { get; private set; }
    public double Duration => Track?.Duration ?? 0;
    public BitmapSource? Artwork { get; private set; }
    public BitmapSource? BlurredArtwork { get; private set; }
    public ArtworkPalette Palette { get; private set; } = ArtworkPalette.Fallback;
    /// Changes whenever the artwork does (drives the crossfade).
    public int ArtworkToken { get; private set; }
    public string? SourceAppId { get; private set; }
    public string? SourceName { get; private set; }
    public string Status { get; private set; } = "Starting…";
    public int SessionCount { get; private set; }

    public event Action? TrackChanged;
    public event Action? ArtworkChanged;
    public event Action? PlaybackChanged;
    /// Seek / position correction.
    public event Action? TimelineChanged;

    public MediaController(AppSettings settings)
    {
        this.settings = settings;
        dispatcher = Dispatcher.CurrentDispatcher;
        poll = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
        poll.Tick += (_, _) => Refresh();
        settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppSettings.PreferredSource)) Refresh();
        };
    }

    public async Task StartAsync()
    {
        try
        {
            manager = await GManager.RequestAsync();
        }
        catch (Exception e)
        {
            Status = "Windows media sessions unavailable: " + e.Message;
            return;
        }
        manager.CurrentSessionChanged += (_, _) => Post(Refresh);
        manager.SessionsChanged += (_, _) => Post(Refresh);
        Status = "Waiting for music";
        Refresh();
        poll.Start();
    }

    /// Current playback position in seconds.
    public double Position(DateTime? at = null)
    {
        var now = at ?? DateTime.UtcNow;
        double p = anchorPosition + (IsPlaying ? (now - anchorTime).TotalSeconds : 0);
        if (Duration > 0) p = Math.Min(p, Duration);
        return Math.Max(0, p);
    }

    // MARK: Commands

    public async void PlayPause()
    {
        if (session == null) return;
        SetPlaying(!IsPlaying);
        try { await session.TryTogglePlayPauseAsync(); } catch (Exception) { }
    }

    public async void Next()
    {
        if (session == null) return;
        try { await session.TrySkipNextAsync(); } catch (Exception) { }
    }

    public async void Previous()
    {
        if (session == null) return;
        try { await session.TrySkipPreviousAsync(); } catch (Exception) { }
    }

    public async void Seek(double seconds)
    {
        if (session == null) return;
        seconds = Math.Max(0, Duration > 0 ? Math.Min(seconds, Duration - 0.5) : seconds);
        Anchor(seconds);
        try { await session.TryChangePlaybackPositionAsync((long)(seconds * TimeSpan.TicksPerSecond)); } catch (Exception) { }
    }

    // MARK: Session tracking

    private void Post(Action action) => dispatcher.BeginInvoke(action);

    private static bool Matches(GSession s, PreferredSource source)
    {
        var id = s.SourceAppUserModelId ?? "";
        return source switch
        {
            PreferredSource.Spotify => id.Contains("spotify", StringComparison.OrdinalIgnoreCase),
            PreferredSource.AppleMusic => id.Contains("AppleMusic", StringComparison.OrdinalIgnoreCase) ||
                                          id.Contains("AppleInc.", StringComparison.OrdinalIgnoreCase) ||
                                          id.Contains("iTunes", StringComparison.OrdinalIgnoreCase),
            _ => true,
        };
    }

    private static bool IsMusicApp(GSession s) => Matches(s, PreferredSource.Spotify) || Matches(s, PreferredSource.AppleMusic);

    private static bool Playing(GSession s)
    {
        try { return s.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing; }
        catch (Exception) { return false; }
    }

    private GSession? ChooseSession()
    {
        if (manager == null) return null;
        var sessions = manager.GetSessions().ToList();
        SessionCount = sessions.Count;
        if (sessions.Count == 0) return null;

        if (settings.PreferredSource != PreferredSource.Auto)
        {
            var preferred = sessions.Where(s => Matches(s, settings.PreferredSource)).ToList();
            if (preferred.Count > 0) return preferred.FirstOrDefault(Playing) ?? preferred[0];
        }

        // Auto: whatever is playing (music apps first), else the system's current session.
        var playing = sessions.Where(Playing).ToList();
        if (playing.Count > 0)
        {
            if (session != null && playing.Any(s => s.SourceAppUserModelId == session.SourceAppUserModelId))
                return playing.First(s => s.SourceAppUserModelId == session.SourceAppUserModelId);
            return playing.FirstOrDefault(IsMusicApp) ?? playing[0];
        }
        if (session != null && sessions.Any(s => s.SourceAppUserModelId == session.SourceAppUserModelId))
            return sessions.First(s => s.SourceAppUserModelId == session.SourceAppUserModelId);
        return manager.GetCurrentSession() ?? sessions[0];
    }

    private void Refresh()
    {
        GSession? chosen;
        try { chosen = ChooseSession(); }
        catch (Exception e) { Status = "Media sessions error: " + e.Message; return; }

        if (chosen?.SourceAppUserModelId != session?.SourceAppUserModelId || (chosen == null) != (session == null))
            Attach(chosen);
        else
        {
            UpdatePlayback();
            UpdateTimeline(force: false);
        }
    }

    private void Attach(GSession? next)
    {
        if (session != null)
        {
            session.MediaPropertiesChanged -= OnPropertiesChanged;
            session.PlaybackInfoChanged -= OnPlaybackChanged;
            session.TimelinePropertiesChanged -= OnTimelineChanged;
        }
        session = next;
        SourceAppId = next?.SourceAppUserModelId;
        SourceName = FriendlyName(SourceAppId);

        if (next == null)
        {
            Status = "Nothing playing";
            SetTrack(null);
            SetPlaying(false);
            SetArtwork(null);
            return;
        }
        Status = $"Connected to {SourceName}";
        next.MediaPropertiesChanged += OnPropertiesChanged;
        next.PlaybackInfoChanged += OnPlaybackChanged;
        next.TimelinePropertiesChanged += OnTimelineChanged;
        UpdatePlayback();
        _ = UpdatePropertiesAsync();
    }

    private void OnPropertiesChanged(GSession s, MediaPropertiesChangedEventArgs e) => Post(() => _ = UpdatePropertiesAsync());
    private void OnPlaybackChanged(GSession s, PlaybackInfoChangedEventArgs e) => Post(() => { UpdatePlayback(); Refresh(); });
    private void OnTimelineChanged(GSession s, TimelinePropertiesChangedEventArgs e) => Post(() => UpdateTimeline(force: false));

    private async Task UpdatePropertiesAsync()
    {
        var s = session;
        if (s == null) return;
        int version = ++propertiesVersion;
        GlobalSystemMediaTransportControlsSessionMediaProperties props;
        try { props = await s.TryGetMediaPropertiesAsync(); }
        catch (Exception) { return; }
        if (version != propertiesVersion || s != session || props == null) return;

        double duration = 0;
        try
        {
            var tl = s.GetTimelineProperties();
            duration = Math.Max(0, (tl.EndTime - tl.StartTime).TotalSeconds);
        }
        catch (Exception) { }

        var title = props.Title ?? "";
        var artist = string.IsNullOrEmpty(props.Artist) ? props.AlbumArtist ?? "" : props.Artist;
        var track = string.IsNullOrWhiteSpace(title) ? null : new TrackInfo(title, artist, props.AlbumTitle ?? "", duration);
        bool changed = track?.Id != Track?.Id;
        SetTrack(track);
        UpdateTimeline(force: changed);

        // Artwork (may arrive a moment after the title).
        byte[]? bytes = null;
        if (props.Thumbnail != null)
        {
            try
            {
                using var stream = await props.Thumbnail.OpenReadAsync();
                using var input = stream.AsStreamForRead();
                using var memory = new MemoryStream();
                await input.CopyToAsync(memory);
                bytes = memory.ToArray();
            }
            catch (Exception) { }
        }
        if (version != propertiesVersion) return;
        if (bytes == null || bytes.Length == 0)
        {
            if (changed) SetArtwork(null);
            return;
        }
        if (artworkBytes != null && bytes.AsSpan().SequenceEqual(artworkBytes)) return;
        artworkBytes = bytes;
        var analysis = await Task.Run<(BitmapSource Image, ArtworkAnalysis Analysis)?>(() =>
        {
            var image = ArtworkAnalyzer.Decode(bytes);
            return image == null ? null : (image, ArtworkAnalyzer.Analyze(image));
        });
        if (version != propertiesVersion) return;
        if (analysis == null) SetArtwork(null);
        else SetArtwork(analysis.Value.Image, analysis.Value.Analysis);
    }

    private void UpdatePlayback()
    {
        if (session == null) return;
        SetPlaying(Playing(session));
    }

    private void UpdateTimeline(bool force)
    {
        if (session == null) return;
        GlobalSystemMediaTransportControlsSessionTimelineProperties tl;
        try { tl = session.GetTimelineProperties(); }
        catch (Exception) { return; }
        if (tl == null) return;

        double position = tl.Position.TotalSeconds;
        var signature = (position, tl.LastUpdatedTime.UtcTicks);
        if (!force && signature == lastTimelineSignature) return; // nothing new: keep extrapolating
        lastTimelineSignature = signature;

        double duration = Math.Max(0, (tl.EndTime - tl.StartTime).TotalSeconds);
        if (Track != null && duration > 0 && Math.Abs(duration - Track.Duration) > 0.5)
        {
            Track = Track with { Duration = duration };
        }

        var now = DateTime.UtcNow;
        double observed = position;
        if (IsPlaying && tl.LastUpdatedTime.Year > 2000)
        {
            double age = (now - tl.LastUpdatedTime.UtcDateTime).TotalSeconds;
            if (age > 0 && age < 3600) observed += age;
        }
        if (duration > 0) observed = Math.Min(observed, duration);
        if (force || Math.Abs(observed - Position(now)) > 0.3) Anchor(observed);
    }

    private void Anchor(double position)
    {
        anchorPosition = position;
        anchorTime = DateTime.UtcNow;
        TimelineChanged?.Invoke();
    }

    private void SetTrack(TrackInfo? track)
    {
        if (track?.Id == Track?.Id && track?.Duration == Track?.Duration) return;
        bool idChanged = track?.Id != Track?.Id;
        Track = track;
        if (idChanged)
        {
            Anchor(0);
            TrackChanged?.Invoke();
        }
    }

    private void SetPlaying(bool playing)
    {
        if (playing == IsPlaying) return;
        // Freeze / resume the estimate where it is.
        var position = Position();
        IsPlaying = playing;
        anchorPosition = position;
        anchorTime = DateTime.UtcNow;
        PlaybackChanged?.Invoke();
    }

    private void SetArtwork(BitmapSource? image, ArtworkAnalysis? analysis = null)
    {
        if (image == null && Artwork == null) return;
        if (image == null) artworkBytes = null;
        Artwork = image;
        BlurredArtwork = analysis?.Blurred;
        Palette = analysis?.Palette ?? ArtworkPalette.Fallback;
        ArtworkToken++;
        ArtworkChanged?.Invoke();
    }

    public static string? FriendlyName(string? appId)
    {
        if (string.IsNullOrEmpty(appId)) return null;
        var id = appId.ToLowerInvariant();
        if (id.Contains("spotify")) return "Spotify";
        if (id.Contains("applemusic") || id.Contains("appleinc.")) return "Apple Music";
        if (id.Contains("itunes")) return "iTunes";
        if (id.Contains("chrome")) return "Chrome";
        if (id.Contains("msedge")) return "Edge";
        if (id.Contains("firefox")) return "Firefox";
        if (id.Contains("opera")) return "Opera";
        if (id.Contains("zunemusic") || id.Contains("media player")) return "Media Player";
        if (id.Contains("tidal")) return "TIDAL";
        if (id.Contains("deezer")) return "Deezer";
        if (id.Contains("vlc")) return "VLC";
        var name = appId.Split('!')[0].Split('_')[0];
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return name.Split('.').Last();
    }

    // MARK: Testing / previews

    internal void SetTestState(TrackInfo track, BitmapSource artwork, bool playing, double position)
    {
        session = null;
        Track = track;
        IsPlaying = playing;
        SourceName = "Spotify";
        var analysis = ArtworkAnalyzer.Analyze(artwork);
        Artwork = artwork;
        BlurredArtwork = analysis.Blurred;
        Palette = analysis.Palette;
        ArtworkToken++;
        anchorPosition = position;
        anchorTime = DateTime.UtcNow;
        TrackChanged?.Invoke();
        ArtworkChanged?.Invoke();
        PlaybackChanged?.Invoke();
    }
}
