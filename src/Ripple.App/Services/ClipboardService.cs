using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ripple.App.Interop;
using Ripple.Core.Events;
using Ripple.Core.Modules;
using Ripple.Core.Services;
using WpfClipboard = System.Windows.Clipboard;

namespace Ripple.App.Services;

/// <summary>
/// Monitors the Windows clipboard via WM_CLIPBOARDUPDATE, ignores configured password managers,
/// detects smart content (URLs, colors, math, JSON, OTPs, code), and persists history encrypted with AES-256-GCM.
/// </summary>
public sealed class ClipboardService : IRippleModule
{
    private readonly LiveActivityEventBus _bus;
    private readonly RippleSettings _settings;
    private readonly string _storagePath;
    private readonly byte[] _encryptionKey;
    private readonly List<ClipboardEntry> _entries = [];

    private string _lastCapturedText = string.Empty;
    private DateTimeOffset _lastCleanupAt = DateTimeOffset.MinValue;

    public ClipboardService(LiveActivityEventBus bus, RippleSettings settings)
    {
        _bus = bus;
        _settings = settings;
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ripple");
        Directory.CreateDirectory(dir);
        _storagePath = Path.Combine(dir, "clipboard.enc");
        _encryptionKey = DeriveUserScopedKey(Path.Combine(dir, "clipboard.key"));
        LoadEncryptedHistory();
    }

    public string Id => "clipboard";
    public string Name => "Clipboard";
    public string Icon => "📋";
    public int Priority => 45;
    public bool IsEnabled { get; set; } = true;
    public IReadOnlyCollection<ModulePermission> RequiredPermissions { get; } = [ModulePermission.Clipboard];
    public double PreferredExpandedWidth => 460;
    public double PreferredExpandedHeight => 260;

    public IReadOnlyList<ClipboardEntry> Entries => _entries;
    public bool IsIncognito
    {
        get => _settings.ClipboardIncognito;
        set => _settings.ClipboardIncognito = value;
    }

    public event Action? Updated;

    public ModuleGlanceState GetGlanceState()
    {
        if (IsIncognito)
        {
            return new ModuleGlanceState(true, "Incognito", "🕵", "#A78BFA");
        }
        return new ModuleGlanceState(false);
    }

    public void OnTick(DateTimeOffset utcNow, bool isExpanded)
    {
        if ((utcNow - _lastCleanupAt).TotalSeconds < 10) return;
        _lastCleanupAt = utcNow;

        var removed = _entries.RemoveAll(e => !e.IsPinned && e.IsExpired(utcNow));
        if (_settings.ClipboardAutoClearMinutes > 0)
        {
            var cutoff = utcNow.AddMinutes(-_settings.ClipboardAutoClearMinutes);
            removed += _entries.RemoveAll(e => !e.IsPinned && e.CapturedAt < cutoff);
        }

        if (removed > 0)
        {
            SaveEncryptedHistory();
            Updated?.Invoke();
        }
    }

    public void OnClipboardChanged()
    {
        if (IsIncognito) return;

        var sourceApp = GetForegroundProcessName();
        if (_settings.ClipboardIgnoredApps.Any(ignored =>
                sourceApp.Contains(ignored, StringComparison.OrdinalIgnoreCase)))
        {
            return; // Protect passwords from KeePass / 1Password / Bitwarden
        }

        try
        {
            if (WpfClipboard.ContainsText())
            {
                var text = WpfClipboard.GetText();
                if (string.IsNullOrWhiteSpace(text) || text == _lastCapturedText) return;
                _lastCapturedText = text;

                var analysis = ClipboardSmartDetector.Analyze(text);
                var entry = new ClipboardEntry(
                    Id: Guid.NewGuid().ToString("N")[..12],
                    Text: text,
                    Analysis: analysis,
                    CapturedAt: DateTimeOffset.UtcNow,
                    IsPinned: false,
                    SourceApp: sourceApp);

                _entries.RemoveAll(e => !e.IsPinned && e.Text == text);
                _entries.Insert(0, entry);
                if (_entries.Count > 60)
                {
                    var idx = _entries.FindLastIndex(e => !e.IsPinned);
                    if (idx >= 0) _entries.RemoveAt(idx);
                }

                SaveEncryptedHistory();
                Updated?.Invoke();
            }
            else if (WpfClipboard.ContainsImage())
            {
                var img = WpfClipboard.GetImage();
                if (img is null) return;
                var imgDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ripple", "clip_images");
                Directory.CreateDirectory(imgDir);
                var imgPath = Path.Combine(imgDir, $"clip_{DateTimeOffset.UtcNow:yyyyMMdd_HHmmss}.png");

                using (var fs = File.Create(imgPath))
                {
                    var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(img));
                    enc.Save(fs);
                }

                var analysis = new ClipboardSmartAnalysis(ClipboardSmartKind.Image, "IMAGE", "Copy / Save Image", imgPath);
                _entries.Insert(0, new ClipboardEntry(
                    Guid.NewGuid().ToString("N")[..12],
                    $"[Image] {Path.GetFileName(imgPath)}",
                    analysis,
                    DateTimeOffset.UtcNow,
                    false,
                    sourceApp,
                    ImagePath: imgPath));

                SaveEncryptedHistory();
                Updated?.Invoke();
            }
        }
        catch
        {
            // Clipboard locked momentarily by another process
        }
    }

    public void CopyEntry(ClipboardEntry entry, bool plainText = false)
    {
        try
        {
            var value = plainText ? entry.Text : (entry.Analysis.NormalizedValue ?? entry.Text);
            _lastCapturedText = value;
            WpfClipboard.SetText(value);
            _bus.Publish(new LiveActivityPayload(
                "clip:copied",
                Id,
                LiveActivityKind.Generic,
                "Copied from Clipboard Pro",
                value.Length > 42 ? value[..42] + "…" : value,
                "📋",
                "#34D399",
                LiveActivityPriority.Low,
                TimeSpan.FromSeconds(1.8),
                CoalesceKey: "clipboard-copy"));
        }
        catch { }
    }

    public void TogglePin(string id)
    {
        var idx = _entries.FindIndex(e => e.Id == id);
        if (idx >= 0)
        {
            _entries[idx] = _entries[idx] with { IsPinned = !_entries[idx].IsPinned };
            SaveEncryptedHistory();
            Updated?.Invoke();
        }
    }

    public void DeleteEntry(string id)
    {
        _entries.RemoveAll(e => e.Id == id);
        SaveEncryptedHistory();
        Updated?.Invoke();
    }

    public void ClearUnpinned()
    {
        _entries.RemoveAll(e => !e.IsPinned);
        SaveEncryptedHistory();
        Updated?.Invoke();
    }

    private void LoadEncryptedHistory()
    {
        try
        {
            if (!File.Exists(_storagePath)) return;
            var encrypted = File.ReadAllBytes(_storagePath);
            var json = ClipboardSmartDetector.DecryptPayload(encrypted, _encryptionKey);
            var loaded = JsonSerializer.Deserialize<List<ClipboardEntry>>(json);
            if (loaded is not null)
            {
                _entries.Clear();
                _entries.AddRange(loaded.Where(e => !e.IsExpired(DateTimeOffset.UtcNow)));
            }
        }
        catch
        {
            // Corrupted or rotated key; start clean
        }
    }

    private void SaveEncryptedHistory()
    {
        try
        {
            var json = JsonSerializer.Serialize(_entries);
            var encrypted = ClipboardSmartDetector.EncryptPayload(json, _encryptionKey);
            File.WriteAllBytes(_storagePath, encrypted);
        }
        catch { }
    }

    private static byte[] DeriveUserScopedKey(string keyPath)
    {
        if (File.Exists(keyPath))
        {
            try
            {
                var protectedBytes = File.ReadAllBytes(keyPath);
                return ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser);
            }
            catch { }
        }

        var rawKey = RandomNumberGenerator.GetBytes(32);
        try
        {
            var protectedKey = ProtectedData.Protect(rawKey, null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(keyPath, protectedKey);
        }
        catch { }
        return rawKey;
    }

    private static string GetForegroundProcessName()
    {
        try
        {
            var hwnd = Win32Interop.GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return "Unknown";
            Win32Interop.GetWindowThreadProcessId(hwnd, out var pid);
            using var proc = Process.GetProcessById((int)pid);
            return proc.ProcessName;
        }
        catch
        {
            return "Unknown";
        }
    }
}
