using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using LockTune.Media;

namespace LockTune.Lyrics;

public sealed class LyricsNotFoundException : Exception { }

/// Fetches lyrics from LRCLIB (https://lrclib.net) and caches them on disk.
public sealed class LyricsService
{
    private sealed class CacheEntry
    {
        public LrcLibRecord? Record { get; set; }
        public DateTime FetchedAt { get; set; }
    }

    private static readonly HttpClient Http = CreateClient();
    private readonly string cacheDirectory;
    /// How long a "not found" answer is trusted before asking again.
    private static readonly TimeSpan NegativeCacheLifetime = TimeSpan.FromDays(3);

    public LyricsService()
    {
        cacheDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                      "LockTune", "Lyrics");
        Directory.CreateDirectory(cacheDirectory);
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        // LRCLIB asks clients to identify themselves.
        client.DefaultRequestHeaders.UserAgent.ParseAdd("LockTune/1.0 (Windows lock-screen player; https://github.com/KubaCh7/LockTune)");
        return client;
    }

    public async Task<LyricsContent> LyricsAsync(TrackInfo track, CancellationToken ct)
    {
        var key = CacheKey(track);
        var cached = ReadCache(key);
        if (cached != null)
        {
            if (cached.Record != null && Content(cached.Record) is { } content) return content;
            if (DateTime.UtcNow - cached.FetchedAt < NegativeCacheLifetime) throw new LyricsNotFoundException();
        }

        var record = await FetchAsync(track, ct).ConfigureAwait(false);
        WriteCache(key, new CacheEntry { Record = record, FetchedAt = DateTime.UtcNow });
        if (record != null && Content(record) is { } found) return found;
        throw new LyricsNotFoundException();
    }

    public void ClearCache()
    {
        try { Directory.Delete(cacheDirectory, true); } catch { /* already gone */ }
        Directory.CreateDirectory(cacheDirectory);
    }

    // MARK: Network

    private static string Query(params (string Key, string Value)[] items) =>
        string.Join("&", items.Select(i => i.Key + "=" + Uri.EscapeDataString(i.Value)));

    private async Task<LrcLibRecord?> FetchAsync(TrackInfo track, CancellationToken ct)
    {
        // 1. Exact signature lookup.
        var exact = await GetAsync<LrcLibRecord>("https://lrclib.net/api/get?" + Query(
            ("artist_name", track.Artist), ("track_name", track.Title), ("album_name", track.Album),
            ("duration", ((int)Math.Round(track.Duration)).ToString())), ct).ConfigureAwait(false);
        // Only stop here if it's *synced*: LRCLIB often has a plain-only entry
        // for the exact match while a synced version of the same song exists.
        if (exact != null && !string.IsNullOrEmpty(exact.SyncedLyrics)) return exact;

        // 2. Search (structured, then free text), and pick the synced version
        //    whose length matches the song best — a different edit (longer
        //    intro, radio edit…) is the usual reason lyrics run out of sync.
        var results = new List<LrcLibRecord>();
        var structured = await GetAsync<List<LrcLibRecord>>("https://lrclib.net/api/search?" + Query(
            ("track_name", CleanTitle(track.Title)), ("artist_name", PrimaryArtist(track.Artist))), ct).ConfigureAwait(false);
        if (structured != null) results.AddRange(structured);

        double Delta(LrcLibRecord r) => track.Duration > 0 && r.Duration is { } d ? Math.Abs(d - track.Duration) : 0;
        static bool IsSynced(LrcLibRecord r) => !string.IsNullOrEmpty(r.SyncedLyrics);

        if (!results.Any(r => IsSynced(r) && Delta(r) <= 2))
        {
            try
            {
                var free = await GetAsync<List<LrcLibRecord>>("https://lrclib.net/api/search?" + Query(
                    ("q", $"{PrimaryArtist(track.Artist)} {CleanTitle(track.Title)}")), ct).ConfigureAwait(false);
                if (free != null)
                {
                    var known = results.Where(r => r.Id != null).Select(r => r.Id!.Value).ToHashSet();
                    results.AddRange(free.Where(r => r.Id == null || !known.Contains(r.Id.Value)));
                }
            }
            catch (HttpRequestException) { }
        }

        var synced = results.Where(IsSynced).OrderBy(Delta).ToList();
        if (synced.Count > 0 && Delta(synced[0]) <= 2) return synced[0];
        if (synced.Count > 0 && Delta(synced[0]) <= 5) return synced[0];
        // No well-matching synced version: prefer the exact (plain) match.
        if (exact != null && Content(exact) != null) return exact;
        if (synced.Count > 0 && Delta(synced[0]) <= 10) return synced[0];
        return results.OrderBy(Delta).FirstOrDefault();
    }

    /// GET + decode; null on 404.
    private static async Task<T?> GetAsync<T>(string url, CancellationToken ct) where T : class
    {
        using var response = await Http.GetAsync(url, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        try { return JsonSerializer.Deserialize<T>(json); }
        catch (JsonException) { return null; }
    }

    // MARK: Helpers

    public static LyricsContent? Content(LrcLibRecord record)
    {
        if (!string.IsNullOrEmpty(record.SyncedLyrics))
        {
            var lines = LrcParser.Parse(record.SyncedLyrics);
            if (lines.Any(l => !l.IsInstrumental)) return new LyricsContent.Synced(lines);
        }
        if (!string.IsNullOrEmpty(record.PlainLyrics))
            return new LyricsContent.Plain(record.PlainLyrics.Replace("\r", "").Split('\n'));
        if (record.Instrumental == true) return new LyricsContent.Instrumental();
        return null;
    }

    /// "Song (feat. X) - Remastered 2011" → "Song"
    public static string CleanTitle(string title)
    {
        var t = title;
        foreach (var pattern in new[] { @"\s*[\(\[](feat\.?|ft\.?|with)\s[^\)\]]*[\)\]]",
                                        @"\s+-\s+.*(remaster|version|edit|live|mono|stereo|mix).*$" })
            t = Regex.Replace(t, pattern, "", RegexOptions.IgnoreCase);
        return t.Trim();
    }

    public static string PrimaryArtist(string artist)
    {
        var result = artist;
        foreach (var s in new[] { ", ", " & ", " feat. ", " ft. ", " x " })
        {
            int i = result.IndexOf(s, StringComparison.OrdinalIgnoreCase);
            if (i >= 0) result = result[..i];
        }
        return result;
    }

    // MARK: Cache

    private static string CacheKey(TrackInfo track)
    {
        var raw = string.Join('\u001f', "v3", track.Artist, track.Title, track.Album,
                              ((int)Math.Round(track.Duration)).ToString()).ToLowerInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
    }

    private CacheEntry? ReadCache(string key)
    {
        try
        {
            var path = Path.Combine(cacheDirectory, key + ".json");
            return File.Exists(path) ? JsonSerializer.Deserialize<CacheEntry>(File.ReadAllText(path)) : null;
        }
        catch { return null; }
    }

    private void WriteCache(string key, CacheEntry entry)
    {
        try { File.WriteAllText(Path.Combine(cacheDirectory, key + ".json"), JsonSerializer.Serialize(entry)); }
        catch { /* cache is optional */ }
    }
}
