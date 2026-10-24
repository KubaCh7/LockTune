using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Threading;
using LockTune.Core;
using LockTune.Media;

namespace LockTune.Lyrics;

public enum LyricsState { Idle, Loading, Loaded, NotFound, Failed }

/// Loads lyrics for whatever `MediaController` is playing.
public sealed class LyricsController : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public LyricsState State { get; private set; } = LyricsState.Idle;
    public LyricsContent? Content { get; private set; }
    /// Incremented whenever new lyrics arrive (drives the "lines animate in" effect).
    public int Revision { get; private set; }
    /// Per-song timing correction in seconds (+ = lyrics earlier), remembered
    /// for every song you adjust.
    public double SongOffset { get; private set; }
    public const double OffsetStep = 0.25;

    public IReadOnlyList<LyricLine>? SyncedLines => (Content as LyricsContent.Synced)?.Lines;

    private readonly LyricsService service = new();
    private readonly AppSettings settings;
    private TrackInfo? currentTrack;
    private CancellationTokenSource? loading;
    private Dictionary<string, double> offsets;
    private static string OffsetsPath => Path.Combine(AppSettings.Folder, "song-offsets.json");

    public LyricsController(MediaController media, AppSettings settings)
    {
        this.settings = settings;
        offsets = LoadOffsets();
        media.TrackChanged += () => Load(media.Track);
        settings.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(AppSettings.LyricsEnabled)) Load(currentTrack);
        };
    }

    /// Retries after a network failure, e.g. when the lock screen opens.
    public void ReloadIfNeeded()
    {
        if (State == LyricsState.Failed) Load(currentTrack);
    }

    public void ClearCache()
    {
        service.ClearCache();
        Load(currentTrack);
    }

    /// Nudges the current song's lyrics earlier (+) or later (−).
    public void Nudge(double delta)
    {
        if (currentTrack == null) return;
        SongOffset = Math.Clamp(SongOffset + delta, -10, 10);
        if (Math.Abs(SongOffset) < 0.001) offsets.Remove(currentTrack.Id);
        else offsets[currentTrack.Id] = SongOffset;
        SaveOffsets();
        Changed(nameof(SongOffset));
    }

    public void ResetAllSongOffsets()
    {
        offsets.Clear();
        SaveOffsets();
        SongOffset = 0;
        Changed(nameof(SongOffset));
    }

    /// For the self-test and previews.
    internal void SetContentForTesting(LyricsContent content)
    {
        Content = content;
        State = LyricsState.Loaded;
        Revision++;
        Changed(null);
    }

    private async void Load(TrackInfo? track)
    {
        loading?.Cancel();
        currentTrack = track;
        SongOffset = track != null && offsets.TryGetValue(track.Id, out var o) ? o : 0;
        Changed(nameof(SongOffset));

        if (track == null || !settings.LyricsEnabled || string.IsNullOrWhiteSpace(track.Title))
        {
            SetState(LyricsState.Idle, null);
            return;
        }
        SetState(LyricsState.Loading, null);
        var cts = loading = new CancellationTokenSource();
        try
        {
            var content = await service.LyricsAsync(track, cts.Token);
            if (cts.IsCancellationRequested) return;
            Revision++;
            SetState(LyricsState.Loaded, content);
        }
        catch (LyricsNotFoundException)
        {
            if (!cts.IsCancellationRequested) SetState(LyricsState.NotFound, null);
        }
        catch (Exception)
        {
            if (!cts.IsCancellationRequested) SetState(LyricsState.Failed, null);
        }
    }

    private void SetState(LyricsState state, LyricsContent? content)
    {
        State = state;
        Content = content;
        Changed(null);
    }

    private void Changed(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private static Dictionary<string, double> LoadOffsets()
    {
        try
        {
            if (File.Exists(OffsetsPath))
                return JsonSerializer.Deserialize<Dictionary<string, double>>(File.ReadAllText(OffsetsPath)) ?? new();
        }
        catch { }
        return new();
    }

    private void SaveOffsets()
    {
        try
        {
            Directory.CreateDirectory(AppSettings.Folder);
            File.WriteAllText(OffsetsPath, JsonSerializer.Serialize(offsets));
        }
        catch { }
    }
}
