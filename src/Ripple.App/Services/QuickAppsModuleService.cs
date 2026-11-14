using System.Diagnostics;
using Ripple.Core.Modules;
using Ripple.Core.Services;

namespace Ripple.App.Services;

public sealed class QuickAppsModuleService : IRippleModule
{
    private readonly RippleSettings _settings;
    private HashSet<string> _runningProcessNames = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _lastPollAt = DateTimeOffset.MinValue;

    public QuickAppsModuleService(RippleSettings settings)
    {
        _settings = settings;
    }

    public string Id => "apps";
    public string Name => "Apps";
    public string Icon => "⊞";
    public int Priority => 40;
    public bool IsEnabled { get; set; } = true;
    public IReadOnlyCollection<ModulePermission> RequiredPermissions { get; } = [];
    public double PreferredExpandedWidth => 450;
    public double PreferredExpandedHeight => 210;

    public IReadOnlyList<QuickAppEntry> Apps => _settings.QuickApps;

    public event Action? Updated;

    public ModuleGlanceState GetGlanceState() => new(false);

    public void OnTick(DateTimeOffset utcNow, bool isExpanded)
    {
        if (isExpanded && (utcNow - _lastPollAt).TotalSeconds >= 3)
        {
            _lastPollAt = utcNow;
            RefreshRunningApps();
        }
    }

    public bool IsAppRunning(QuickAppEntry entry)
    {
        var exeName = System.IO.Path.GetFileNameWithoutExtension(entry.TargetPath);
        return !string.IsNullOrEmpty(exeName) && _runningProcessNames.Contains(exeName);
    }

    public void Launch(QuickAppEntry entry)
    {
        try
        {
            Process.Start(new ProcessStartInfo(entry.TargetPath) { UseShellExecute = true });
        }
        catch { }
    }

    public void AddApp(string name, string targetPath, string glyph = "◈")
    {
        _settings.QuickApps.Add(new QuickAppEntry(
            Guid.NewGuid().ToString("N")[..8],
            name,
            targetPath,
            glyph));
        Updated?.Invoke();
    }

    public void RemoveApp(string id)
    {
        _settings.QuickApps.RemoveAll(a => a.Id == id);
        Updated?.Invoke();
    }

    private void RefreshRunningApps()
    {
        try
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in Process.GetProcesses())
            {
                try { names.Add(p.ProcessName); } catch { } finally { p.Dispose(); }
            }
            _runningProcessNames = names;
            Updated?.Invoke();
        }
        catch { }
    }
}
