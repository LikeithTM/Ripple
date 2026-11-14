using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Ripple.Core.Events;
using Ripple.Core.Modules;
using Ripple.Core.Services;
using Windows.Media.Control;

using WpfColor = System.Windows.Media.Color;
using WpfPixelFormats = System.Windows.Media.PixelFormats;

namespace Ripple.App.Services;

public sealed class MediaStateSnapshot
{
    public bool HasSession { get; init; }
    public string SourceApp { get; init; } = "No Media Playing";
    public string Title { get; init; } = "Play music in Spotify, Chrome, Apple Music or VLC";
    public string Artist { get; init; } = "Ready for SMTC media session";
    public string Album { get; init; } = "";
    public bool IsPlaying { get; init; }
    public bool IsShuffle { get; init; }
    public string RepeatMode { get; init; } = "None";
    public TimeSpan Position { get; init; }
    public TimeSpan Duration { get; init; } = TimeSpan.FromMinutes(3);
    public BitmapSource? Artwork { get; init; }
    public WpfColor PrimaryColor { get; init; } = WpfColor.FromRgb(108, 140, 255);
    public WpfColor SecondaryColor { get; init; } = WpfColor.FromRgb(167, 139, 250);
    public IReadOnlyList<string> AvailableSources { get; init; } = [];
    public string CurrentLyric { get; init; } = "";
    public string NextLyric { get; init; } = "";
    public double LyricProgress { get; init; }
}

/// <summary>
/// Windows GlobalSystemMediaTransportControls (SMTC) integration with dynamic album-art palette extraction,
/// multi-session switching, LRCLIB synced lyrics, and 24-band audio-reactive spectrum values.
/// </summary>
public sealed class MediaService : IRippleModule
{
    private readonly LiveActivityEventBus _bus;
    private readonly AudioMixerService _audio;
    private readonly string _lyricsCacheDir;
    private readonly double[] _eqBands = new double[24];
    private readonly Random _rng = new();

    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _currentSession;
    private string _lastTrackKey = string.Empty;
    private string? _preferredSourceId;
    private IReadOnlyList<LyricLine> _currentLyrics = [];
    private MediaStateSnapshot _snapshot = new();
    private DateTimeOffset _lastPollAt = DateTimeOffset.MinValue;
    private double _smoothPeak;

    public MediaService(LiveActivityEventBus bus, AudioMixerService audio)
    {
        _bus = bus;
        _audio = audio;
        _lyricsCacheDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Ripple",
            "lyrics_cache");
        _ = InitializeAsync();
    }

    public string Id => "music";
    public string Name => "Music";
    public string Icon => "\u266B";
    public int Priority => 80;
    public bool IsEnabled { get; set; } = true;
    public IReadOnlyCollection<ModulePermission> RequiredPermissions { get; } = [ModulePermission.MediaControl];
    public double PreferredExpandedWidth => 456;
    public double PreferredExpandedHeight => 216;

    public MediaStateSnapshot Snapshot => _snapshot;
    public IReadOnlyList<double> EqualizerBands => _eqBands;
    public bool IsLiked { get; private set; }

    public event Action? Updated;

    public ModuleGlanceState GetGlanceState()
    {
        if (!_snapshot.HasSession && !_snapshot.IsPlaying)
        {
            return new ModuleGlanceState(false);
        }

        var hex = $"#{_snapshot.PrimaryColor.R:X2}{_snapshot.PrimaryColor.G:X2}{_snapshot.PrimaryColor.B:X2}";
        return new ModuleGlanceState(
            HasActiveIndicator: _snapshot.IsPlaying,
            Label: _snapshot.Title,
            Icon: "\u266B",
            AccentHex: hex,
            Progress: _snapshot.Duration.TotalSeconds > 0
                ? Math.Clamp(_snapshot.Position.TotalSeconds / _snapshot.Duration.TotalSeconds, 0, 1)
                : null,
            Pulse: _snapshot.IsPlaying);
    }

    public void OnTick(DateTimeOffset utcNow, bool isExpanded)
    {
        var pollMs = isExpanded ? 450 : 1200;
        if ((utcNow - _lastPollAt).TotalMilliseconds >= pollMs)
        {
            _lastPollAt = utcNow;
            _ = RefreshCurrentSessionAsync();
        }
        else if (_snapshot.IsPlaying && _snapshot.Duration.TotalSeconds > 0)
        {
            // Smoothly interpolate playback position & active lyric between SMTC refreshes
            var nextPos = _snapshot.Position + TimeSpan.FromMilliseconds(16);
            if (nextPos <= _snapshot.Duration)
            {
                var lyric = LyricsParser.GetActiveLine(_currentLyrics, nextPos);
                _snapshot = new MediaStateSnapshot
                {
                    HasSession = _snapshot.HasSession,
                    SourceApp = _snapshot.SourceApp,
                    Title = _snapshot.Title,
                    Artist = _snapshot.Artist,
                    Album = _snapshot.Album,
                    IsPlaying = _snapshot.IsPlaying,
                    IsShuffle = _snapshot.IsShuffle,
                    RepeatMode = _snapshot.RepeatMode,
                    Position = nextPos,
                    Duration = _snapshot.Duration,
                    Artwork = _snapshot.Artwork,
                    PrimaryColor = _snapshot.PrimaryColor,
                    SecondaryColor = _snapshot.SecondaryColor,
                    AvailableSources = _snapshot.AvailableSources,
                    CurrentLyric = lyric.Current?.Text ?? _snapshot.CurrentLyric,
                    NextLyric = lyric.Next?.Text ?? _snapshot.NextLyric,
                    LyricProgress = lyric.LineProgress
                };
            }
        }
    }

    private async Task InitializeAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            if (_manager is not null)
            {
                _manager.SessionsChanged += (_, _) => _ = RefreshCurrentSessionAsync();
                _manager.CurrentSessionChanged += (_, _) => _ = RefreshCurrentSessionAsync();
                await RefreshCurrentSessionAsync();
            }
        }
        catch
        {
            // Graceful fallback on unsupported environments
        }
    }

    public async Task RefreshCurrentSessionAsync()
    {
        if (_manager is null || _demo) return;

        try
        {
            var sessions = _manager.GetSessions();
            var sourceNames = sessions.Select(s => CleanAppId(s.SourceAppUserModelId)).Distinct().ToList();

            var chosen = !string.IsNullOrEmpty(_preferredSourceId)
                ? sessions.FirstOrDefault(s => CleanAppId(s.SourceAppUserModelId) == _preferredSourceId)
                : null;
            chosen ??= _manager.GetCurrentSession() ?? sessions.FirstOrDefault();

            _currentSession = chosen;
            if (chosen is null)
            {
                _snapshot = new MediaStateSnapshot { AvailableSources = sourceNames };
                Updated?.Invoke();
                return;
            }

            var props = await chosen.TryGetMediaPropertiesAsync();
            var playback = chosen.GetPlaybackInfo();
            var timeline = chosen.GetTimelineProperties();

            var isPlaying = playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            var title = string.IsNullOrWhiteSpace(props?.Title) ? "Unknown Track" : props.Title;
            var artist = string.IsNullOrWhiteSpace(props?.Artist) ? CleanAppId(chosen.SourceAppUserModelId) : props.Artist;
            var album = props?.AlbumTitle ?? "";
            var sourceApp = CleanAppId(chosen.SourceAppUserModelId);

            var trackKey = $"{sourceApp}|{artist}|{title}";
            BitmapSource? art = _snapshot.Artwork;
            WpfColor primary = _snapshot.PrimaryColor;
            WpfColor secondary = _snapshot.SecondaryColor;

            if (trackKey != _lastTrackKey)
            {
                _lastTrackKey = trackKey;
                IsLiked = false;
                if (props?.Thumbnail is not null)
                {
                    (art, primary, secondary) = await LoadArtworkAndColorsAsync(props.Thumbnail);
                }

                _currentLyrics = [];
                _ = LoadLyricsForTrackAsync(artist, title);

                if (isPlaying && !string.IsNullOrWhiteSpace(props?.Title))
                {
                    var hex = $"#{primary.R:X2}{primary.G:X2}{primary.B:X2}";
                    _bus.Publish(new LiveActivityPayload(
                        Id: $"music:{trackKey}",
                        SourceModuleId: Id,
                        Kind: LiveActivityKind.MediaTrack,
                        Title: title,
                        Subtitle: artist,
                        Icon: "â™«",
                        AccentHex: hex,
                        Priority: LiveActivityPriority.Normal,
                        Duration: TimeSpan.FromSeconds(3.5),
                        CoalesceKey: "music-track"));
                }
            }

            var pos = timeline.Position;
            var dur = timeline.EndTime > TimeSpan.Zero ? timeline.EndTime : TimeSpan.FromMinutes(3);
            var lyricState = LyricsParser.GetActiveLine(_currentLyrics, pos);

            _snapshot = new MediaStateSnapshot
            {
                HasSession = true,
                SourceApp = sourceApp,
                Title = title,
                Artist = artist,
                Album = album,
                IsPlaying = isPlaying,
                IsShuffle = playback.IsShuffleActive ?? false,
                RepeatMode = playback.AutoRepeatMode?.ToString() ?? "None",
                Position = pos,
                Duration = dur,
                Artwork = art,
                PrimaryColor = primary,
                SecondaryColor = secondary,
                AvailableSources = sourceNames,
                CurrentLyric = lyricState.Current?.Text ?? "",
                NextLyric = lyricState.Next?.Text ?? "",
                LyricProgress = lyricState.LineProgress
            };

            Updated?.Invoke();
        }
        catch
        {
            // Keep last valid snapshot if session disappears mid-query
        }
    }

    private async Task LoadLyricsForTrackAsync(string artist, string title)
    {
        var lines = await LyricsParser.FetchOrLoadCachedAsync(artist, title, _lyricsCacheDir);
        _currentLyrics = lines;
    }

    public void SelectSource(string sourceName)
    {
        _preferredSourceId = sourceName;
        _ = RefreshCurrentSessionAsync();
    }

    public async Task TogglePlayPauseAsync()
    {
        if (_currentSession is not null)
        {
            await _currentSession.TryTogglePlayPauseAsync();
            await RefreshCurrentSessionAsync();
        }
    }

    public async Task NextAsync()
    {
        if (_currentSession is not null)
        {
            await _currentSession.TrySkipNextAsync();
            await RefreshCurrentSessionAsync();
        }
    }

    public async Task PreviousAsync()
    {
        if (_currentSession is not null)
        {
            await _currentSession.TrySkipPreviousAsync();
            await RefreshCurrentSessionAsync();
        }
    }

    public async Task SeekAsync(TimeSpan position)
    {
        if (_currentSession is not null)
        {
            await _currentSession.TryChangePlaybackPositionAsync(position.Ticks);
            await RefreshCurrentSessionAsync();
        }
    }

    public async Task ToggleShuffleAsync()
    {
        if (_currentSession is not null)
        {
            await _currentSession.TryChangeShuffleActiveAsync(!_snapshot.IsShuffle);
            await RefreshCurrentSessionAsync();
        }
    }

    public void ToggleLike()
    {
        IsLiked = !IsLiked;
        Updated?.Invoke();
    }

    private bool _demo;

    /// <summary>Deterministic fake session used by the offscreen UI snapshot suite (--snapshot).</summary>
    public void LoadDemoSnapshot()
    {
        _demo = true;
        var primary = WpfColor.FromRgb(236, 96, 168);
        var secondary = WpfColor.FromRgb(120, 92, 255);

        var dv = new System.Windows.Media.DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            var bg = new System.Windows.Media.LinearGradientBrush(primary, secondary, 45);
            dc.DrawRectangle(bg, null, new System.Windows.Rect(0, 0, 160, 160));
            dc.DrawEllipse(new System.Windows.Media.SolidColorBrush(WpfColor.FromArgb(90, 255, 255, 255)), null, new System.Windows.Point(110, 50), 46, 46);
            dc.DrawEllipse(new System.Windows.Media.SolidColorBrush(WpfColor.FromArgb(70, 20, 10, 60)), null, new System.Windows.Point(48, 118), 58, 58);
        }
        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(160, 160, 96, 96, WpfPixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();

        _currentLyrics = LyricsParser.Parse("[00:00.00] Waiting in a car\n[01:20.00] Waiting for a ride in the dark\n[01:28.00] The night city grows");
        var lyric = LyricsParser.GetActiveLine(_currentLyrics, TimeSpan.FromSeconds(83));
        _snapshot = new MediaStateSnapshot
        {
            HasSession = true,
            SourceApp = "Spotify",
            Title = "Midnight City",
            Artist = "M83",
            Album = "Hurry Up, We're Dreaming",
            IsPlaying = true,
            Position = TimeSpan.FromSeconds(83),
            Duration = TimeSpan.FromSeconds(243),
            Artwork = rtb,
            PrimaryColor = primary,
            SecondaryColor = secondary,
            AvailableSources = ["Spotify", "Chrome"],
            CurrentLyric = lyric.Current?.Text ?? "",
            NextLyric = lyric.Next?.Text ?? "",
            LyricProgress = lyric.LineProgress
        };
        Updated?.Invoke();
    }

    /// <summary>True while the equalizer still has visible motion (so the UI keeps stepping it).</summary>
    public bool VisualizerActive => _snapshot.IsPlaying || _eqBands.Any(b => b > 0.09);

    /// <summary>Advance the spectrum one display frame. Called by the UI only while a visualizer is visible.</summary>
    public void StepVisualizer()
    {
        var playing = _snapshot.IsPlaying;
        var rawPeak = playing ? _audio.GetMasterPeakLevel() : 0.0;
        _smoothPeak = _smoothPeak * 0.8 + rawPeak * 0.2;
        var active = _demo || (playing && (rawPeak > 0.01 || _smoothPeak > 0.02));
        var energy = active ? Math.Max(_demo ? 0.7 : 0.22, _smoothPeak * 1.45) : 0.05;
        var t = Environment.TickCount64 * 0.008;

        for (var i = 0; i < _eqBands.Length; i++)
        {
            var harmonic = active
                ? 0.45 + 0.55 * Math.Abs(Math.Sin(t + i * 0.55) * Math.Cos(t * 0.65 - i * 0.25)) + _rng.NextDouble() * 0.12
                : 0.08;
            var target = Math.Clamp(energy * harmonic, 0.06, 1.0);
            _eqBands[i] = _eqBands[i] * 0.72 + target * 0.28;
        }
    }

    private static async Task<(BitmapSource? Art, WpfColor Primary, WpfColor Secondary)> LoadArtworkAndColorsAsync(
        Windows.Storage.Streams.IRandomAccessStreamReference thumbRef)
    {
        try
        {
            using var stream = await thumbRef.OpenReadAsync();
            using var netStream = stream.AsStreamForRead();
            var ms = new MemoryStream();
            await netStream.CopyToAsync(ms);
            ms.Position = 0;

            var decoder = BitmapDecoder.Create(ms, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            frame.Freeze();

            var (p, s) = ExtractVibrantColors(frame);
            return (frame, p, s);
        }
        catch
        {
            return (null, WpfColor.FromRgb(108, 140, 255), WpfColor.FromRgb(167, 139, 250));
        }
    }

    private static (WpfColor Primary, WpfColor Secondary) ExtractVibrantColors(BitmapSource bmp)
    {
        try
        {
            var scaled = new TransformedBitmap(bmp, new ScaleTransform(16.0 / bmp.PixelWidth, 16.0 / bmp.PixelHeight));
            var fmt = new FormatConvertedBitmap(scaled, WpfPixelFormats.Bgra32, null, 0);
            var pixels = new byte[16 * 16 * 4];
            fmt.CopyPixels(pixels, 16 * 4, 0);

            WpfColor best = WpfColor.FromRgb(108, 140, 255);
            WpfColor second = WpfColor.FromRgb(167, 139, 250);
            double bestScore = -1;
            double secondScore = -1;

            for (var i = 0; i < pixels.Length; i += 4)
            {
                var b = pixels[i];
                var g = pixels[i + 1];
                var r = pixels[i + 2];
                var max = Math.Max(r, Math.Max(g, b));
                var min = Math.Min(r, Math.Min(g, b));
                var sat = max == 0 ? 0 : (max - min) / (double)max;
                var luma = (0.299 * r + 0.587 * g + 0.114 * b) / 255.0;
                if (luma < 0.15 || luma > 0.90) continue;

                var score = sat * 2.0 + (1.0 - Math.Abs(luma - 0.55));
                if (score > bestScore)
                {
                    secondScore = bestScore;
                    second = best;
                    bestScore = score;
                    best = WpfColor.FromRgb(r, g, b);
                }
                else if (score > secondScore && ColorDistance(best, WpfColor.FromRgb(r, g, b)) > 60)
                {
                    secondScore = score;
                    second = WpfColor.FromRgb(r, g, b);
                }
            }

            return (best, second);
        }
        catch
        {
            return (WpfColor.FromRgb(108, 140, 255), WpfColor.FromRgb(167, 139, 250));
        }
    }

    private static double ColorDistance(WpfColor a, WpfColor b) =>
        Math.Sqrt(Math.Pow(a.R - b.R, 2) + Math.Pow(a.G - b.G, 2) + Math.Pow(a.B - b.B, 2));

    private static string CleanAppId(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "Media";
        var bang = raw.IndexOf('!');
        var clean = bang > 0 ? raw[..bang] : raw;
        clean = Path.GetFileNameWithoutExtension(clean);
        return clean.Replace("SpotifyAB.SpotifyMusic", "Spotify", StringComparison.OrdinalIgnoreCase)
                    .Replace("AppleInc.AppleMusicWin", "Apple Music", StringComparison.OrdinalIgnoreCase)
                    .Replace("msedge", "Edge", StringComparison.OrdinalIgnoreCase)
                    .Replace("chrome", "Chrome", StringComparison.OrdinalIgnoreCase);
    }
}
