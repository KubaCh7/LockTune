using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace LockTune.Lyrics;

public sealed record LyricWord(string Text, double? Time);

public sealed class LyricLine
{
    public int Id { get; }
    /// Start time in seconds.
    public double Time { get; }
    /// When the next line starts (or a few seconds later for the last line).
    public double End { get; }
    /// Empty text marks an instrumental break (rendered as animated dots).
    public string Text { get; }
    public IReadOnlyList<LyricWord> Words { get; }

    public bool IsInstrumental => Text.Length == 0;
    public bool HasWordTiming => Words.Any(w => w.Time != null);

    public LyricLine(int id, double time, double end, string text, IReadOnlyList<LyricWord>? words = null)
    {
        Id = id;
        Time = time;
        End = end;
        Text = text;
        Words = words ?? Split(text);
    }

    /// Progress (0…1) of each word at `now`. Uses real word timing when present,
    /// otherwise spreads the line's sung duration over the words by length,
    /// which looks close to Apple Music's word-by-word fill.
    public double[] WordProgress(double now)
    {
        if (Words.Count == 0) return Array.Empty<double>();
        var starts = new double[Words.Count];
        if (HasWordTiming)
        {
            double last = Time;
            for (int i = 0; i < Words.Count; i++)
            {
                last = Math.Max(last, Words[i].Time ?? last);
                starts[i] = last;
            }
        }
        else
        {
            // People sing ~16 characters per second; don't stretch a short line
            // across a long pause before the next one.
            double characters = Words.Sum(w => Math.Max(w.Text.Length, 2));
            double sung = Math.Max(0.4, Math.Min((End - Time) * 0.85, characters / 16 + 0.3));
            double cursor = Time;
            for (int i = 0; i < Words.Count; i++)
            {
                starts[i] = cursor;
                cursor += sung * Math.Max(Words[i].Text.Length, 2) / characters;
            }
        }
        var result = new double[Words.Count];
        for (int i = 0; i < Words.Count; i++)
        {
            double start = starts[i];
            double stop = i + 1 < starts.Length ? starts[i + 1] : Math.Min(End, start + 1.2);
            result[i] = stop <= start ? (now >= start ? 1 : 0) : Math.Clamp((now - start) / (stop - start), 0, 1);
        }
        return result;
    }

    public static List<LyricWord> Split(string text)
    {
        var result = new List<LyricWord>();
        var current = new System.Text.StringBuilder();
        foreach (char c in text)
        {
            current.Append(c);
            if (c == ' ')
            {
                result.Add(new LyricWord(current.ToString(), null));
                current.Clear();
            }
        }
        if (current.Length > 0) result.Add(new LyricWord(current.ToString(), null));
        return result;
    }
}

public abstract record LyricsContent
{
    public sealed record Synced(IReadOnlyList<LyricLine> Lines) : LyricsContent;
    public sealed record Plain(IReadOnlyList<string> Lines) : LyricsContent;
    public sealed record Instrumental : LyricsContent;
}

/// What LRCLIB returns (subset).
public sealed class LrcLibRecord
{
    [JsonPropertyName("id")] public long? Id { get; set; }
    [JsonPropertyName("trackName")] public string? TrackName { get; set; }
    [JsonPropertyName("artistName")] public string? ArtistName { get; set; }
    [JsonPropertyName("albumName")] public string? AlbumName { get; set; }
    [JsonPropertyName("duration")] public double? Duration { get; set; }
    [JsonPropertyName("instrumental")] public bool? Instrumental { get; set; }
    [JsonPropertyName("plainLyrics")] public string? PlainLyrics { get; set; }
    [JsonPropertyName("syncedLyrics")] public string? SyncedLyrics { get; set; }
}

/// Parses `.lrc` text such as `[01:23.45]Line` (multiple timestamps per line,
/// the `[offset:±ms]` tag and enhanced `<mm:ss.xx>` word tags are supported).
public static class LrcParser
{
    private static readonly Regex Timestamp = new(@"\[(\d{1,3}):(\d{1,2}(?:[.:]\d{1,3})?)\]", RegexOptions.Compiled);
    private static readonly Regex WordTag = new(@"<(\d{1,3}):(\d{1,2}(?:[.:]\d{1,3})?)>", RegexOptions.Compiled);

    private static double Seconds(Match m) =>
        double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * 60 +
        double.Parse(m.Groups[2].Value.Replace(':', '.'), CultureInfo.InvariantCulture);

    public static List<LyricLine> Parse(string text)
    {
        double offset = 0;
        var entries = new List<(double Time, string Text, List<LyricWord>? Words)>();

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (!line.StartsWith('[')) continue;

            if (line.StartsWith("[offset:", StringComparison.OrdinalIgnoreCase))
            {
                var value = line[8..].TrimEnd(']').Trim();
                if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var ms)) offset = ms / 1000;
                continue;
            }

            int cursor = 0;
            var times = new List<double>();
            foreach (Match match in Timestamp.Matches(line))
            {
                // Only leading, consecutive timestamps belong to this line.
                if (match.Index != cursor) break;
                times.Add(Seconds(match));
                cursor = match.Index + match.Length;
            }
            if (times.Count == 0) continue;

            var (lyric, words) = ParseWords(line[cursor..], offset);
            foreach (var t in times)
                // A positive offset means the lyrics should appear earlier.
                entries.Add((Math.Max(0, t - offset), lyric, words));
        }

        entries.Sort((a, b) => a.Time.CompareTo(b.Time));

        // Keep empty lines only when they mark a real instrumental gap (≥ 4 s).
        var cleaned = new List<(double Time, string Text, List<LyricWord>? Words)>();
        for (int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            if (entry.Text.Length == 0)
            {
                double? next = null;
                for (int j = i + 1; j < entries.Count; j++)
                    if (entries[j].Text.Length > 0) { next = entries[j].Time; break; }
                if (next == null || next - entry.Time < 4) continue;
                if (cleaned.Count > 0 && cleaned[^1].Text.Length == 0) continue;
            }
            cleaned.Add(entry);
        }

        // Instrumental intro before the first line.
        if (cleaned.Count > 0 && cleaned[0].Text.Length > 0 && cleaned[0].Time >= 4)
            cleaned.Insert(0, (0, "", null));
        // Trailing empty marker is useless.
        while (cleaned.Count > 0 && cleaned[^1].Text.Length == 0) cleaned.RemoveAt(cleaned.Count - 1);

        var lines = new List<LyricLine>(cleaned.Count);
        for (int i = 0; i < cleaned.Count; i++)
        {
            var e = cleaned[i];
            double end = i + 1 < cleaned.Count ? cleaned[i + 1].Time : e.Time + 6;
            lines.Add(new LyricLine(i, e.Time, end, e.Text, e.Words));
        }
        return lines;
    }

    /// Strips enhanced-LRC word tags, returning the plain line and its words
    /// (with times) — or null words when the line has no tags.
    private static (string, List<LyricWord>?) ParseWords(string raw, double offset)
    {
        var matches = WordTag.Matches(raw);
        if (matches.Count == 0) return (raw.Trim(), null);

        var words = new List<LyricWord>();
        // Text before the first tag (rare) has no time of its own.
        var leading = raw[..matches[0].Index];
        if (leading.Trim().Length > 0) words.Add(new LyricWord(leading, null));
        for (int i = 0; i < matches.Count; i++)
        {
            var match = matches[i];
            int start = match.Index + match.Length;
            int stop = i + 1 < matches.Count ? matches[i + 1].Index : raw.Length;
            var text = raw[start..stop];
            if (text.Length == 0) continue;
            words.Add(new LyricWord(text, Math.Max(0, Seconds(match) - offset)));
        }
        var plain = string.Concat(words.Select(w => w.Text)).Trim();
        return (plain, words);
    }

    /// Index of the line active at `time`, or -1 before the first line.
    public static int ActiveIndex(IReadOnlyList<LyricLine> lines, double time)
    {
        int low = 0, high = lines.Count - 1, result = -1;
        while (low <= high)
        {
            int mid = (low + high) / 2;
            if (lines[mid].Time <= time) { result = mid; low = mid + 1; }
            else high = mid - 1;
        }
        return result;
    }
}
