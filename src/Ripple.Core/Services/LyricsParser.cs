using System.Globalization;
using System.Net.Http.Json;
using System.Text.RegularExpressions;

namespace Ripple.Core.Services;

public sealed record LyricLine(TimeSpan Timestamp, string Text);

public sealed record ActiveLyricResult(
    int ActiveIndex,
    LyricLine? Previous,
    LyricLine? Current,
    LyricLine? Next,
    double LineProgress);

/// <summary>
/// Parses timestamped LRC lyrics, computes active karaoke highlight progress,
/// and fetches/caches synced lyrics from LRCLIB.
/// </summary>
public static partial class LyricsParser
{
    private static readonly Regex LineTagRegex = MyLrcRegex();

    public static IReadOnlyList<LyricLine> Parse(string? lrcContent)
    {
        if (string.IsNullOrWhiteSpace(lrcContent))
        {
            return [];
        }

        var lines = new List<LyricLine>();
        foreach (var rawLine in lrcContent.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var matches = LineTagRegex.Matches(rawLine);
            if (matches.Count == 0)
            {
                continue;
            }

            var lyricText = LineTagRegex.Replace(rawLine, string.Empty).Trim();
            foreach (Match m in matches)
            {
                var mins = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                var secs = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
                lines.Add(new LyricLine(TimeSpan.FromMinutes(mins) + TimeSpan.FromSeconds(secs), lyricText));
            }
        }

        lines.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
        return lines;
    }

    public static ActiveLyricResult GetActiveLine(IReadOnlyList<LyricLine> lines, TimeSpan position)
    {
        if (lines.Count == 0)
        {
            return new ActiveLyricResult(-1, null, null, null, 0.0);
        }

        var activeIdx = -1;
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].Timestamp <= position)
            {
                activeIdx = i;
            }
            else
            {
                break;
            }
        }

        if (activeIdx < 0)
        {
            return new ActiveLyricResult(-1, null, null, lines[0], 0.0);
        }

        var current = lines[activeIdx];
        var prev = activeIdx > 0 ? lines[activeIdx - 1] : null;
        var next = activeIdx + 1 < lines.Count ? lines[activeIdx + 1] : null;

        var progress = 1.0;
        if (next is not null)
        {
            var span = (next.Timestamp - current.Timestamp).TotalSeconds;
            if (span > 0.05)
            {
                progress = Math.Clamp((position - current.Timestamp).TotalSeconds / span, 0.0, 1.0);
            }
        }

        return new ActiveLyricResult(activeIdx, prev, current, next, progress);
    }

    public static async Task<IReadOnlyList<LyricLine>> FetchOrLoadCachedAsync(
        string artist,
        string title,
        string cacheDirectory,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return [];
        }

        Directory.CreateDirectory(cacheDirectory);
        var safeKey = string.Concat($"{artist}_{title}".ToLowerInvariant()
            .Select(ch => char.IsLetterOrDigit(ch) ? ch : '_'));
        var cachePath = Path.Combine(cacheDirectory, $"{safeKey}.lrc");

        if (File.Exists(cachePath))
        {
            return Parse(await File.ReadAllTextAsync(cachePath, cancellationToken));
        }

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("RippleDesktop/1.0");
            var url = $"https://lrclib.net/api/get?artist_name={Uri.EscapeDataString(artist)}&track_name={Uri.EscapeDataString(title)}";
            var response = await client.GetFromJsonAsync<LrcLibResponse>(url, cancellationToken);
            if (!string.IsNullOrWhiteSpace(response?.SyncedLyrics))
            {
                await File.WriteAllTextAsync(cachePath, response.SyncedLyrics, cancellationToken);
                return Parse(response.SyncedLyrics);
            }
        }
        catch
        {
            // Network unavailable or track missing; graceful silent fallback
        }

        return [];
    }

    private sealed record LrcLibResponse(string? SyncedLyrics, string? PlainLyrics);

    [GeneratedRegex(@"\[(\d{1,3}):(\d{1,2}(?:\.\d{1,3})?)\]", RegexOptions.Compiled)]
    private static partial Regex MyLrcRegex();
}
