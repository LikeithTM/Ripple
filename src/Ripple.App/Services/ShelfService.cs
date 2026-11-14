using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Windows.Media.Imaging;
using Ripple.Core.Events;
using Ripple.Core.Modules;

namespace Ripple.App.Services;

public enum ShelfItemKind
{
    File,
    Folder,
    Image,
    Url,
    Text
}

public sealed record ShelfItem(
    string Id,
    string DisplayName,
    string Payload,
    ShelfItemKind Kind,
    DateTimeOffset AddedAt,
    long SizeBytes = 0,
    bool IsSelected = false);

/// <summary>
/// Temporary Drop Shelf hub supporting drag-in &amp; drag-out, zipping/unzipping,
/// image format conversion, Copy Path, and timed auto-cleanup.
/// </summary>
public sealed class ShelfService : IRippleModule
{
    private readonly LiveActivityEventBus _bus;
    private readonly List<ShelfItem> _items = [];
    private DateTimeOffset _lastCleanupAt = DateTimeOffset.MinValue;

    public ShelfService(LiveActivityEventBus bus)
    {
        _bus = bus;
    }

    public string Id => "shelf";
    public string Name => "Shelf";
    public string Icon => "📥";
    public int Priority => 70;
    public bool IsEnabled { get; set; } = true;
    public IReadOnlyCollection<ModulePermission> RequiredPermissions { get; } = [ModulePermission.FileSystem];
    public double PreferredExpandedWidth => 460;
    public double PreferredExpandedHeight => 240;

    public IReadOnlyList<ShelfItem> Items => _items;
    public int AutoExpireHours { get; set; } = 12;

    public event Action? Updated;

    public ModuleGlanceState GetGlanceState() =>
        _items.Count > 0
            ? new ModuleGlanceState(true, $"{_items.Count}", "📥", "#38BDF8")
            : new ModuleGlanceState(false);

    public void OnTick(DateTimeOffset utcNow, bool isExpanded)
    {
        if (AutoExpireHours <= 0 || (utcNow - _lastCleanupAt).TotalMinutes < 5) return;
        _lastCleanupAt = utcNow;
        var cutoff = utcNow.AddHours(-AutoExpireHours);
        if (_items.RemoveAll(i => i.AddedAt < cutoff) > 0)
        {
            Updated?.Invoke();
        }
    }

    public void AddDroppedPaths(IEnumerable<string> paths)
    {
        var added = 0;
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path)) continue;
            if (_items.Any(i => string.Equals(i.Payload, path, StringComparison.OrdinalIgnoreCase))) continue;

            var isDir = Directory.Exists(path);
            var ext = Path.GetExtension(path).ToLowerInvariant();
            var isImg = ext is ".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp";
            var kind = isDir ? ShelfItemKind.Folder : (isImg ? ShelfItemKind.Image : ShelfItemKind.File);
            long size = 0;
            try
            {
                if (!isDir && File.Exists(path)) size = new FileInfo(path).Length;
            }
            catch { }

            _items.Insert(0, new ShelfItem(
                Guid.NewGuid().ToString("N")[..10],
                Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } n ? n : path,
                path,
                kind,
                DateTimeOffset.UtcNow,
                size));
            added++;
        }

        if (added > 0)
        {
            _bus.Publish(new LiveActivityPayload(
                "shelf:drop",
                Id,
                LiveActivityKind.Generic,
                $"Added {added} item(s) to Drop Shelf",
                $"{_items.Count} total held",
                "📥",
                "#38BDF8",
                LiveActivityPriority.Normal,
                TimeSpan.FromSeconds(2.5),
                CoalesceKey: "shelf-drop"));
            Updated?.Invoke();
        }
    }

    public void AddDroppedTextOrUrl(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var trimmed = text.Trim();
        var isUrl = Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && uri.Scheme.StartsWith("http");
        _items.Insert(0, new ShelfItem(
            Guid.NewGuid().ToString("N")[..10],
            trimmed.Length > 34 ? trimmed[..34] + "…" : trimmed,
            trimmed,
            isUrl ? ShelfItemKind.Url : ShelfItemKind.Text,
            DateTimeOffset.UtcNow,
            trimmed.Length));
        Updated?.Invoke();
    }

    public string? ZipItems(IEnumerable<ShelfItem> targetItems)
    {
        var fileItems = targetItems.Where(i => i.Kind is ShelfItemKind.File or ShelfItemKind.Image or ShelfItemKind.Folder).ToList();
        if (fileItems.Count == 0) return null;

        var firstParent = Path.GetDirectoryName(fileItems[0].Payload) ?? Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        var zipPath = Path.Combine(firstParent, $"Ripple_Bundle_{DateTime.Now:yyyyMMdd_HHmmss}.zip");

        using var archive = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        foreach (var item in fileItems)
        {
            if (File.Exists(item.Payload))
            {
                archive.CreateEntryFromFile(item.Payload, Path.GetFileName(item.Payload));
            }
            else if (Directory.Exists(item.Payload))
            {
                var rootName = Path.GetFileName(item.Payload);
                foreach (var file in Directory.GetFiles(item.Payload, "*", SearchOption.AllDirectories))
                {
                    var rel = Path.Combine(rootName, Path.GetRelativePath(item.Payload, file));
                    archive.CreateEntryFromFile(file, rel);
                }
            }
        }

        AddDroppedPaths([zipPath]);
        return zipPath;
    }

    public string? ConvertImageToPng(ShelfItem item)
    {
        if (item.Kind != ShelfItemKind.Image || !File.Exists(item.Payload)) return null;
        try
        {
            var decoder = BitmapDecoder.Create(new Uri(item.Payload), BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            var outPath = Path.ChangeExtension(item.Payload, ".converted.png");
            using var fs = File.Create(outPath);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(decoder.Frames[0]);
            encoder.Save(fs);
            AddDroppedPaths([outPath]);
            return outPath;
        }
        catch
        {
            return null;
        }
    }

    public void OpenItem(ShelfItem item)
    {
        try
        {
            Process.Start(new ProcessStartInfo(item.Payload) { UseShellExecute = true });
        }
        catch { }
    }

    public void RemoveItem(string id)
    {
        _items.RemoveAll(i => i.Id == id);
        Updated?.Invoke();
    }

    public void ClearAll()
    {
        _items.Clear();
        Updated?.Invoke();
    }
}
