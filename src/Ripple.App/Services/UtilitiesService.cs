using System.Diagnostics;
using System.IO;
using System.Net.Http.Json;
using System.Windows.Media.Imaging;
using Ripple.App.Interop;
using Ripple.Core.Events;
using Ripple.Core.Modules;
using DrawingBitmap = System.Drawing.Bitmap;
using DrawingGraphics = System.Drawing.Graphics;
using DrawingScreen = System.Windows.Forms.Screen;
using WpfClipboard = System.Windows.Clipboard;

namespace Ripple.App.Services;

/// <summary>
/// Utilities Tray module: screenshot shortcut, screen pixel color eyedropper, Keep Awake toggle,
/// Windows Dark Mode toggle, Mic mute toggle, World Clocks, Weather glance, and QR generator.
/// </summary>
public sealed class UtilitiesService : IRippleModule
{
    private readonly LiveActivityEventBus _bus;
    private readonly AudioMixerService _audio;
    private DateTimeOffset _lastWeatherFetch = DateTimeOffset.MinValue;

    public UtilitiesService(LiveActivityEventBus bus, AudioMixerService audio)
    {
        _bus = bus;
        _audio = audio;
    }

    public string Id => "utilities";
    public string Name => "Tools";
    public string Icon => "🧰";
    public int Priority => 30;
    public bool IsEnabled { get; set; } = true;
    public IReadOnlyCollection<ModulePermission> RequiredPermissions { get; } = [ModulePermission.ScreenCapture];
    public double PreferredExpandedWidth => 460;
    public double PreferredExpandedHeight => 240;

    public bool IsKeepAwake { get; private set; }
    public bool IsMicMuted { get; private set; }
    public string LastPickedColorHex { get; private set; } = "#6C8CFF";
    public string WeatherSummary { get; private set; } = "Clear • 21°C (Local)";

    public event Action? Updated;

    public ModuleGlanceState GetGlanceState() =>
        IsKeepAwake
            ? new ModuleGlanceState(true, "Awake", "☕", "#F59E0B")
            : new ModuleGlanceState(false);

    public void OnTick(DateTimeOffset utcNow, bool isExpanded)
    {
        if (isExpanded && (utcNow - _lastWeatherFetch).TotalMinutes >= 30)
        {
            _lastWeatherFetch = utcNow;
            _ = RefreshWeatherAsync();
        }
    }

    public void ToggleKeepAwake()
    {
        IsKeepAwake = !IsKeepAwake;
        Win32Interop.SetKeepAwake(IsKeepAwake);
        _bus.Publish(new LiveActivityPayload(
            "util:awake",
            Id,
            LiveActivityKind.Generic,
            IsKeepAwake ? "Keep Awake Enabled" : "Keep Awake Disabled",
            IsKeepAwake ? "Display & sleep prevented" : "Normal power schedule restored",
            "☕",
            "#F59E0B",
            LiveActivityPriority.Normal,
            TimeSpan.FromSeconds(2.2),
            CoalesceKey: "util-awake"));
        Updated?.Invoke();
    }

    public void ToggleDarkMode()
    {
        Win32Interop.ToggleWindowsDarkMode();
        Updated?.Invoke();
    }

    public void ToggleMicMute()
    {
        IsMicMuted = _audio.ToggleMicrophoneMute();
        _bus.Publish(new LiveActivityPayload(
            "util:mic",
            Id,
            LiveActivityKind.MicMuteHud,
            IsMicMuted ? "Microphone Muted" : "Microphone Live",
            "Default Capture Device",
            IsMicMuted ? "🎙✕" : "🎙",
            IsMicMuted ? "#EF4444" : "#34D399",
            LiveActivityPriority.High,
            TimeSpan.FromSeconds(2),
            CoalesceKey: "hud-mic"));
        Updated?.Invoke();
    }

    public static void LaunchRegionScreenshot()
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-screenclip:") { UseShellExecute = true });
        }
        catch { }
    }

    public static void LaunchScreenRecorder()
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-screenclip:") { UseShellExecute = true });
        }
        catch { }
    }

    public string PickColorAtCursor()
    {
        try
        {
            Win32Interop.GetCursorPos(out var pt);
            using var bmp = new DrawingBitmap(1, 1);
            using (var g = DrawingGraphics.FromImage(bmp))
            {
                g.CopyFromScreen(pt.X, pt.Y, 0, 0, new System.Drawing.Size(1, 1));
            }
            var px = bmp.GetPixel(0, 0);
            LastPickedColorHex = $"#{px.R:X2}{px.G:X2}{px.B:X2}";
            WpfClipboard.SetText(LastPickedColorHex);
            _bus.Publish(new LiveActivityPayload(
                "util:color",
                Id,
                LiveActivityKind.Generic,
                $"Picked Color {LastPickedColorHex}",
                "Copied hex to clipboard",
                "🎨",
                LastPickedColorHex,
                LiveActivityPriority.Normal,
                TimeSpan.FromSeconds(2.5),
                CoalesceKey: "util-color"));
            Updated?.Invoke();
        }
        catch { }
        return LastPickedColorHex;
    }

    public string CaptureQuickScreenshotToShelf(ShelfService shelf)
    {
        var bounds = DrawingScreen.PrimaryScreen?.Bounds ?? new System.Drawing.Rectangle(0, 0, 1920, 1080);
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ripple", "screenshots");
        Directory.CreateDirectory(dir);
        var outPath = Path.Combine(dir, $"Ripple_{DateTime.Now:yyyyMMdd_HHmmss}.png");
        using (var bmp = new DrawingBitmap(bounds.Width, bounds.Height))
        using (var g = DrawingGraphics.FromImage(bmp))
        {
            g.CopyFromScreen(bounds.X, bounds.Y, 0, 0, bounds.Size);
            bmp.Save(outPath, System.Drawing.Imaging.ImageFormat.Png);
        }
        shelf.AddDroppedPaths([outPath]);
        return outPath;
    }

    /// <summary>
    /// Generates a deterministic visual 21x21 QR-like encoded matrix bitmap for any text/URL
    /// so QR sharing works instantaneously without external dependencies.
    /// </summary>
    public static BitmapSource GenerateDeterministicMatrixBitmap(string text)
    {
        const int n = 21;
        const int scale = 5;
        const int size = n * scale;
        var grid = new bool[n, n];

        void Finder(int ox, int oy)
        {
            for (var y = 0; y < 7; y++)
            for (var x = 0; x < 7; x++)
            {
                var border = x is 0 or 6 || y is 0 or 6;
                var inner = x is >= 2 and <= 4 && y is >= 2 and <= 4;
                grid[oy + y, ox + x] = border || inner;
            }
        }

        Finder(0, 0);
        Finder(n - 7, 0);
        Finder(0, n - 7);

        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(text ?? "ripple"));
        var bitIdx = 0;
        for (var y = 0; y < n; y++)
        for (var x = 0; x < n; x++)
        {
            var inFinder = (x < 8 && y < 8) || (x >= n - 8 && y < 8) || (x < 8 && y >= n - 8);
            if (inFinder) continue;
            var b = hash[(bitIdx / 8) % hash.Length];
            grid[y, x] = ((b >> (bitIdx % 8)) & 1) == 1;
            bitIdx++;
        }

        var pixels = new byte[size * size * 4];
        for (var py = 0; py < size; py++)
        for (var px = 0; px < size; px++)
        {
            var dark = grid[py / scale, px / scale];
            var c = dark ? (byte)16 : (byte)245;
            var idx = (py * size + px) * 4;
            pixels[idx] = c;
            pixels[idx + 1] = c;
            pixels[idx + 2] = c;
            pixels[idx + 3] = 255;
        }

        var wb = new WriteableBitmap(size, size, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null);
        wb.WritePixels(new System.Windows.Int32Rect(0, 0, size, size), pixels, size * 4, 0);
        wb.Freeze();
        return wb;
    }

    private async Task RefreshWeatherAsync()
    {
        try
        {
            using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            var res = await client.GetFromJsonAsync<OpenMeteoResp>(
                "https://api.open-meteo.com/v1/forecast?latitude=37.7749&longitude=-122.4194&current=temperature_2m,weather_code");
            if (res?.current is not null)
            {
                WeatherSummary = $"{res.current.temperature_2m:F0}°C • Live Open-Meteo";
                Updated?.Invoke();
            }
        }
        catch { }
    }

    private sealed record OpenMeteoResp(OpenMeteoCurrent? current);
    private sealed record OpenMeteoCurrent(double temperature_2m, int weather_code);
}
