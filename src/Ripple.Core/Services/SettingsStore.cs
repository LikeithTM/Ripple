using System.Security.Cryptography;
using System.Text.Json;
using Ripple.Core.Geometry;
using Ripple.Core.Motion;
using Ripple.Core.StateMachine;

namespace Ripple.Core.Services;

public enum RippleThemePreset
{
    TrueBlackOled = 0,
    LiquidGlass = 1,
    AdaptiveArt = 2,
    NeonAurora = 3,
    HighContrast = 4
}

public sealed class RippleProfile
{
    public string Name { get; set; } = "Default";
    public string Description { get; set; } = "Balanced daily desktop profile";
    public RippleThemePreset Theme { get; set; } = RippleThemePreset.TrueBlackOled;
    public bool SuppressLiveActivities { get; set; }
    public bool HideInFullscreen { get; set; } = true;
    public string? AutoActivateProcessName { get; set; }
    public string DefaultModuleId { get; set; } = "music";
    public string AccentHex { get; set; } = "#6C8CFF";
}

public sealed class RippleSettings
{
    public int SchemaVersion { get; set; } = 2;

    // Geometry & Placement
    public NotchLayoutMode LayoutMode { get; set; } = NotchLayoutMode.Notch;
    public NotchDockEdge DockEdge { get; set; } = NotchDockEdge.Top;
    public NotchHorizontalAnchor HorizontalAnchor { get; set; } = NotchHorizontalAnchor.Center;
    public double RestWidth { get; set; } = 188.0;
    public double RestHeight { get; set; } = 32.0;
    public double OffsetX { get; set; }
    public double OffsetY { get; set; }
    public double EarRadius { get; set; } = 12.0;
    public bool AutoDetectWebcamNotch { get; set; } = true;
    public bool FollowCursorMonitor { get; set; } = true;
    public bool HideOnUnfocusedMonitors { get; set; }

    // Behaviour & Triggers
    public int HoverIntentDelayMs { get; set; } = 80;
    public int CollapseGraceDelayMs { get; set; } = 250;
    public double ProximityRadiusPx { get; set; } = 40.0;
    public int ActivityDurationMs { get; set; } = 3500;
    public bool HoverToExpand { get; set; } = true;
    public bool ClickToExpand { get; set; } = true;
    public bool HideInFullscreen { get; set; } = true;
    public bool CollisionAvoidance { get; set; } = true;
    public bool FocusAssistAware { get; set; } = true;
    public bool ContextAwareMorphing { get; set; } = true;
    public bool ReducedMotion { get; set; }
    public bool SubtleUiSounds { get; set; }
    public bool StartWithWindows { get; set; }
    public bool BatterySaver30Fps { get; set; } = true;
    public bool LargeTargetMode { get; set; }
    public string ToggleHotkey { get; set; } = "Ctrl+Alt+N";
    public string PaletteHotkey { get; set; } = "Ctrl+Alt+Space";
    public List<string> FullscreenAllowList { get; set; } = [];
    public List<string> FullscreenDenyList { get; set; } = [];

    // Motion Tuning
    public double ExpandStiffness { get; set; } = 320.0;
    public double ExpandDamping { get; set; } = 28.0;
    public double ExpandMass { get; set; } = 1.0;
    public double CollapseStiffness { get; set; } = 420.0;
    public double CollapseDamping { get; set; } = 41.0;

    // Appearance
    public RippleThemePreset Theme { get; set; } = RippleThemePreset.TrueBlackOled;
    public string AccentHex { get; set; } = "#6C8CFF";
    public string FontFamily { get; set; } = "Segoe UI Variable";
    public bool EnableGlowEdge { get; set; } = true;
    public bool EnableAuroraEffect { get; set; } = true;
    public bool AutoThemeBySchedule { get; set; }

    // Modules & Clipboard Privacy
    public List<string> ModuleOrder { get; set; } =
    [
        "music", "system", "bluetooth", "clipboard", "shelf", "apps", "timer", "mixer", "notifications", "notchy", "utilities"
    ];
    public HashSet<string> DisabledModules { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> ClipboardIgnoredApps { get; set; } = ["1password", "bitwarden", "keepass", "lastpass", "dashlane"];
    public bool ClipboardIncognito { get; set; }
    public int ClipboardAutoClearMinutes { get; set; }

    // Quick Apps
    public List<QuickAppEntry> QuickApps { get; set; } =
    [
        new("term", "Terminal", "wt.exe", "❯_", "Dev"),
        new("explorer", "Explorer", "explorer.exe", "📁", "System"),
        new("calc", "Calculator", "calc.exe", "⊞", "Tools"),
        new("notepad", "Notepad", "notepad.exe", "✎", "Tools"),
        new("browser", "Browser", "https://www.google.com", "🌐", "Web")
    ];

    // Local API & Security
    public bool LocalApiEnabled { get; set; } = true;
    public int LocalApiPort { get; set; } = 49152;
    public string LocalApiToken { get; set; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
    public string GitWatchRepoPath { get; set; } = string.Empty;

    // Profiles & Onboarding
    public string ActiveProfileName { get; set; } = "Default";
    public List<RippleProfile> Profiles { get; set; } = CreateDefaultProfiles();
    public bool OnboardingCompleted { get; set; }

    public SpringConfig BuildExpandSpring() => new(ExpandStiffness, ExpandDamping, ExpandMass);
    public SpringConfig BuildCollapseSpring() => new(CollapseStiffness, CollapseDamping, 1.0);
    public NotchStateTimingConfig BuildTimingConfig() => new(
        HoverIntentDelayMs,
        CollapseGraceDelayMs,
        ProximityRadiusPx,
        ActivityDurationMs,
        HideInFullscreen,
        CollisionAvoidance);

    public static List<RippleProfile> CreateDefaultProfiles() =>
    [
        new() { Name = "Work", Description = "Productivity, calendar & agent hooks prioritized", Theme = RippleThemePreset.LiquidGlass, DefaultModuleId = "notchy", AccentHex = "#6C8CFF" },
        new() { Name = "Gaming", Description = "Hidden during fullscreen games, low CPU", Theme = RippleThemePreset.TrueBlackOled, HideInFullscreen = true, SuppressLiveActivities = true, DefaultModuleId = "system", AccentHex = "#22D3EE" },
        new() { Name = "Presentation", Description = "Mutes notifications & hides popups", Theme = RippleThemePreset.TrueBlackOled, SuppressLiveActivities = true, DefaultModuleId = "timer", AccentHex = "#A78BFA" },
        new() { Name = "Study", Description = "Focus Pomodoro + lyrics & quiet HUDs", Theme = RippleThemePreset.AdaptiveArt, DefaultModuleId = "timer", AccentHex = "#34D399" },
        new() { Name = "Music Party", Description = "Aurora effects & dynamic album glow", Theme = RippleThemePreset.NeonAurora, DefaultModuleId = "music", AccentHex = "#F472B6" }
    ];
}

/// <summary>
/// Versioned JSON settings persistence with schema migration and single-file backup export/import.
/// </summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public string FilePath { get; }

    public SettingsStore(string? customPath = null)
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        FilePath = customPath ?? Path.Combine(appData, "Ripple", "settings.json");
    }

    public RippleSettings Load()
    {
        if (!File.Exists(FilePath))
        {
            var fresh = new RippleSettings();
            Save(fresh);
            return fresh;
        }

        try
        {
            var json = File.ReadAllText(FilePath);
            var settings = JsonSerializer.Deserialize<RippleSettings>(json, JsonOptions) ?? new RippleSettings();
            MigrateIfNeeded(settings);
            return settings;
        }
        catch
        {
            return new RippleSettings();
        }
    }

    public void Save(RippleSettings settings)
    {
        var dir = Path.GetDirectoryName(FilePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var tmpPath = FilePath + ".tmp";
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(tmpPath, json);
        File.Move(tmpPath, FilePath, overwrite: true);
    }

    public void ExportBundle(RippleSettings settings, string targetFilePath)
    {
        var json = JsonSerializer.Serialize(settings, JsonOptions);
        File.WriteAllText(targetFilePath, json);
    }

    public RippleSettings ImportBundle(string sourceFilePath)
    {
        var json = File.ReadAllText(sourceFilePath);
        var imported = JsonSerializer.Deserialize<RippleSettings>(json, JsonOptions)
            ?? throw new InvalidDataException("Invalid Ripple settings bundle.");
        MigrateIfNeeded(imported);
        Save(imported);
        return imported;
    }

    private static void MigrateIfNeeded(RippleSettings settings)
    {
        if (settings.SchemaVersion < 2)
        {
            if (settings.Profiles.Count == 0)
            {
                settings.Profiles = RippleSettings.CreateDefaultProfiles();
            }
            if (string.IsNullOrWhiteSpace(settings.LocalApiToken))
            {
                settings.LocalApiToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            }
            settings.SchemaVersion = 2;
        }
    }
}
