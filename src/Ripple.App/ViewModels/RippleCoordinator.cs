using System.Diagnostics;
using System.IO;
using System.Windows.Media;
using Ripple.App.Interop;
using Ripple.App.Services;
using Ripple.Core.Events;
using Ripple.Core.Geometry;
using Ripple.Core.Modules;
using Ripple.Core.Motion;
using Ripple.Core.Services;
using Ripple.Core.StateMachine;

using WpfBrush = System.Windows.Media.Brush;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfColor = System.Windows.Media.Color;
using WpfColorConverter = System.Windows.Media.ColorConverter;
using WpfLinearGradientBrush = System.Windows.Media.LinearGradientBrush;
using WpfSolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace Ripple.App.ViewModels;

/// <summary>
/// Central application coordinator binding the state machine, spring motion engine,
/// live-activity bus, context-aware module morphing, local REST/WS API, and all 11 modules.
/// </summary>
public sealed class RippleCoordinator : IDisposable
{
    private readonly Dictionary<string, ModuleHealthInfo> _moduleHealth = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<double> _frameTimesMs = new(120);
    private string _activeModuleId = "music";
    private bool _hasUserManuallySelectedTab;

    public RippleCoordinator()
    {
        SettingsStore = new SettingsStore();
        Settings = SettingsStore.Load();

        EventBus = new LiveActivityEventBus();
        StateMachine = new NotchStateMachine(Settings.BuildTimingConfig());
        Motion = new NotchMotionState(Settings.RestWidth, Settings.RestHeight, Settings.RestHeight * 0.5);
        ContextMorph = new ContextMorphEngine();
        Plugins = new PluginHostService();

        AudioMixer = new AudioMixerService();
        Media = new MediaService(EventBus, AudioMixer);
        SystemMonitor = new SystemMonitorService(EventBus);
        Bluetooth = new BluetoothService(EventBus, AudioMixer);
        Clipboard = new ClipboardService(EventBus, Settings);
        Shelf = new ShelfService(EventBus);
        QuickApps = new QuickAppsModuleService(Settings);
        FocusTimer = new FocusProductivityService(EventBus);
        Mixer = new MixerModuleAdapter(AudioMixer, EventBus);
        Notifications = new NotificationService(EventBus);
        Notchy = new AgentAndDevService(EventBus, Settings);
        Utilities = new UtilitiesService(EventBus, AudioMixer);
        FullscreenAndCollision = new FullscreenAndCollisionService();

        Modules =
        [
            Media, SystemMonitor, Bluetooth, Clipboard, Shelf,
            QuickApps, FocusTimer, Mixer, Notifications, Notchy, Utilities
        ];

        ApplyModuleEnabledSettings();

        var pluginDir = Path.Combine(AppContext.BaseDirectory, "plugins");
        if (!Directory.Exists(pluginDir))
        {
            var repoPluginDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "plugins"));
            if (Directory.Exists(repoPluginDir)) pluginDir = repoPluginDir;
        }
        Plugins.DiscoverPlugins(pluginDir);

        LocalApi = new LocalApiServer(
            EventBus,
            () => Settings.LocalApiToken,
            onStartTimer: (mins, label) =>
            {
                FocusTimer.StartTimer(mins, label);
                ActiveModuleId = "timer";
            },
            onAgentEvent: req =>
            {
                Notchy.HandleAgentEvent(req);
                if (Settings.ContextAwareMorphing) ActiveModuleId = "notchy";
            },
            onProfileSwitch: ApplyProfile,
            onStatusQuery: () => new
            {
                ok = true,
                state = StateMachine.CurrentState.ToString(),
                activeModule = ActiveModuleId,
                profile = Settings.ActiveProfileName,
                liveStackCount = EventBus.GetSnapshot().TotalActiveCount,
                modulesHealthy = _moduleHealth.Values.All(h => h.IsHealthy)
            });

        if (Settings.LocalApiEnabled)
        {
            LocalApi.Start(Settings.LocalApiPort);
        }

        EventBus.StackUpdated += snap =>
        {
            if (snap.Primary is { } p)
            {
                var ttl = p.Payload.Duration.HasValue ? (int)p.Payload.Duration.Value.TotalMilliseconds : Settings.ActivityDurationMs;
                StateMachine.ShowLiveActivity(p.Payload.Id, ttl);
            }
            else if (StateMachine.CurrentState == NotchState.Activity)
            {
                StateMachine.DismissLiveActivity();
            }
            CoordinatorChanged?.Invoke();
        };

        StateMachine.StateChanged += (_, next) =>
        {
            if (next is NotchState.Expanded or NotchState.Pinned)
            {
                if (Settings.ContextAwareMorphing && !_hasUserManuallySelectedTab)
                {
                    SelectBestContextModule();
                }
                if (Settings.SubtleUiSounds)
                {
                    try { System.Media.SystemSounds.Asterisk.Play(); } catch { }
                }
            }
            else if (next == NotchState.Rest)
            {
                _hasUserManuallySelectedTab = false;
            }
            CoordinatorChanged?.Invoke();
        };

        Notchy.Updated += () =>
        {
            if ((Notchy.IsAnyAgentRunning || Notchy.IsAnyAgentWaitingApproval) &&
                Settings.ContextAwareMorphing &&
                StateMachine.CurrentState is NotchState.Rest or NotchState.Peek)
            {
                _hasUserManuallySelectedTab = false;
                ActiveModuleId = "notchy";
            }
        };
    }

    public SettingsStore SettingsStore { get; }
    public RippleSettings Settings { get; set; }
    public LiveActivityEventBus EventBus { get; }
    public NotchStateMachine StateMachine { get; }
    public NotchMotionState Motion { get; }
    public ContextMorphEngine ContextMorph { get; }
    public PluginHostService Plugins { get; }
    public LocalApiServer LocalApi { get; }

    public AudioMixerService AudioMixer { get; }
    public MediaService Media { get; }
    public SystemMonitorService SystemMonitor { get; }
    public BluetoothService Bluetooth { get; }
    public ClipboardService Clipboard { get; }
    public ShelfService Shelf { get; }
    public QuickAppsModuleService QuickApps { get; }
    public FocusProductivityService FocusTimer { get; }
    public MixerModuleAdapter Mixer { get; }
    public NotificationService Notifications { get; }
    public AgentAndDevService Notchy { get; }
    public UtilitiesService Utilities { get; }
    public FullscreenAndCollisionService FullscreenAndCollision { get; }

    public IReadOnlyList<IRippleModule> Modules { get; }
    public IReadOnlyDictionary<string, ModuleHealthInfo> ModuleHealth => _moduleHealth;
    public IReadOnlyList<double> FrameTimesMs => _frameTimesMs;
    public bool IsMotionTunerVisible { get; set; }
    public string ActiveScreenDeviceName { get; set; } = "";
    public string? ActiveFullscreenAppName { get; private set; }

    public string ActiveModuleId
    {
        get => _activeModuleId;
        set
        {
            if (_activeModuleId != value)
            {
                _activeModuleId = value;
                ActiveModuleChanged?.Invoke(value);
            }
        }
    }

    public event Action? CoordinatorChanged;
    public event Action<string>? ActiveModuleChanged;

    public void SelectModuleByUser(string id)
    {
        _hasUserManuallySelectedTab = true;
        ActiveModuleId = id;
    }

    public void CycleModule(int delta)
    {
        var enabled = GetOrderedEnabledModules().ToList();
        if (enabled.Count == 0) return;
        var idx = enabled.FindIndex(m => m.Id == ActiveModuleId);
        if (idx < 0) idx = 0;
        var next = (idx + delta + enabled.Count) % enabled.Count;
        SelectModuleByUser(enabled[next].Id);
    }

    public IEnumerable<IRippleModule> GetOrderedEnabledModules()
    {
        var orderMap = Settings.ModuleOrder
            .Select((id, idx) => (id, idx))
            .ToDictionary(x => x.id, x => x.idx, StringComparer.OrdinalIgnoreCase);

        return Modules
            .Where(m => m.IsEnabled && !Settings.DisabledModules.Contains(m.Id))
            .OrderBy(m => orderMap.TryGetValue(m.Id, out var i) ? i : 999);
    }

    public IRippleModule GetActiveModule() =>
        Modules.FirstOrDefault(m => m.Id == ActiveModuleId && m.IsEnabled)
        ?? GetOrderedEnabledModules().FirstOrDefault()
        ?? Media;

    public void ApplyModuleEnabledSettings()
    {
        foreach (var m in Modules)
        {
            m.IsEnabled = !Settings.DisabledModules.Contains(m.Id);
        }
    }

    public void ApplyProfile(string profileName)
    {
        var profile = Settings.Profiles.FirstOrDefault(p => string.Equals(p.Name, profileName, StringComparison.OrdinalIgnoreCase));
        if (profile is null) return;

        Settings.ActiveProfileName = profile.Name;
        Settings.Theme = profile.Theme;
        Settings.HideInFullscreen = profile.HideInFullscreen;
        Settings.AccentHex = profile.AccentHex;
        ActiveModuleId = profile.DefaultModuleId;
        EventBus.IsFocusAssistActive = profile.SuppressLiveActivities;
        StateMachine.Timing = Settings.BuildTimingConfig();
        SettingsStore.Save(Settings);

        EventBus.Publish(new LiveActivityPayload(
            $"profile:{profile.Name}",
            "core",
            LiveActivityKind.Generic,
            $"Profile: {profile.Name}",
            profile.Description,
            "◈",
            profile.AccentHex,
            LiveActivityPriority.Normal,
            TimeSpan.FromSeconds(2.5),
            CoalesceKey: "profile-switch"));
        CoordinatorChanged?.Invoke();
    }

    public void SelectBestContextModule()
    {
        var enabledIds = GetOrderedEnabledModules().Select(m => m.Id).ToList();
        var nextMeetingIn = FocusTimer.NextMeeting is { } mtg
            ? mtg.StartTime - DateTimeOffset.Now
            : (TimeSpan?)null;

        var signals = new ContextSignals(
            IsAgentWaitingApproval: Notchy.IsAnyAgentWaitingApproval,
            IsAgentRunning: Notchy.IsAnyAgentRunning,
            HasUnreadPriorityNotification: Notifications.HasUnreadPriority,
            NextMeetingIn: nextMeetingIn,
            IsTimerRunning: FocusTimer.IsRunning,
            IsMediaPlaying: Media.Snapshot.IsPlaying,
            IsDownloadActive: SystemMonitor.Snapshot.HasActiveDownload,
            IsDragDropActive: StateMachine.IsDragPayloadActive);

        var best = ContextMorph.SelectBestModuleId(signals, enabledIds, fallbackModuleId: ActiveModuleId);
        ActiveModuleId = best;
    }

    public void TickServices(DateTimeOffset utcNow, bool isOpen)
    {
        EventBus.IsFocusAssistActive =
            FocusTimer.FocusModeEnabled ||
            (Settings.FocusAssistAware && FullscreenAndCollisionService.IsFocusAssistActive());

        EventBus.Tick();
        StateMachine.Tick();

        var isFs = FullscreenAndCollision.IsFullscreenActive(Settings, out var fsApp);
        ActiveFullscreenAppName = fsApp;
        StateMachine.SetFullscreenState(isFs);

        foreach (var mod in Modules)
        {
            if (!mod.IsEnabled) continue;
            try
            {
                mod.OnTick(utcNow, isOpen && mod.Id == ActiveModuleId);
                _moduleHealth[mod.Id] = new ModuleHealthInfo(mod.Id, true, "OK");
            }
            catch (Exception ex)
            {
                _moduleHealth[mod.Id] = new ModuleHealthInfo(mod.Id, false, ex.Message, utcNow);
            }
        }
    }

    public (double TargetWidth, double TargetHeight, double TargetRadius, double TargetOffsetY, double PeekGlow, double ContentReveal, double BlurRadius, SpringConfig Spring) ComputeTargetMetrics()
    {
        var restW = Settings.RestWidth;
        var restH = Settings.RestHeight;
        if (Settings.LayoutMode == NotchLayoutMode.FullBar)
        {
            restW = Math.Max(restW, 640);
            restH = Math.Max(restH, 34);
        }

        var glance = GetPrimaryGlanceIndicator();
        if (glance.HasActiveIndicator && Settings.LayoutMode != NotchLayoutMode.FullBar)
        {
            restW += string.IsNullOrWhiteSpace(glance.StatusText) ? 38 : 132;
        }

        var activeMod = GetActiveModule();
        var expW = activeMod.PreferredExpandedWidth;
        var expH = activeMod.PreferredExpandedHeight;
        if (Settings.LargeTargetMode)
        {
            expW *= 1.12;
            expH *= 1.12;
        }

        var reduced = Settings.ReducedMotion || !Win32Interop.IsWindowsAnimationEnabled();

        return StateMachine.CurrentState switch
        {
            NotchState.HiddenFullscreen => (
                restW * 0.65,
                2.0,
                2.0,
                -4.0,
                0.0,
                0.0,
                12.0,
                reduced ? SpringConfig.ReducedMotion : Settings.BuildCollapseSpring()),

            NotchState.Dodging => (
                restW * 0.72,
                5.0,
                3.0,
                -2.0,
                0.0,
                0.0,
                12.0,
                reduced ? SpringConfig.ReducedMotion : SpringConfig.Dodge),

            NotchState.Peek => (
                restW * 1.08,
                restH * 1.09,
                (restH * 1.09) * 0.5,
                Settings.LayoutMode == NotchLayoutMode.FloatingIsland ? 10.0 : 0.0,
                1.0,
                0.0,
                12.0,
                reduced ? SpringConfig.ReducedMotion : SpringConfig.Peek),

            NotchState.Activity => (
                EventBus.GetSnapshot().Secondary is not null ? 360.0 : 300.0,
                50.0,
                24.0,
                Settings.LayoutMode == NotchLayoutMode.FloatingIsland ? 10.0 : 0.0,
                0.65,
                0.0,
                8.0,
                reduced ? SpringConfig.ReducedMotion : Settings.BuildExpandSpring()),

            NotchState.Expanded or NotchState.Pinned => (
                expW,
                expH,
                26.0,
                Settings.LayoutMode == NotchLayoutMode.FloatingIsland ? 10.0 : 0.0,
                0.35,
                1.0,
                0.0,
                reduced ? SpringConfig.ReducedMotion : Settings.BuildExpandSpring()),

            _ => (
                restW,
                restH,
                restH * 0.5,
                Settings.LayoutMode == NotchLayoutMode.FloatingIsland ? 10.0 : 0.0,
                0.0,
                0.0,
                12.0,
                reduced ? SpringConfig.ReducedMotion : Settings.BuildCollapseSpring())
        };
    }

    public ModuleGlanceState GetPrimaryGlanceIndicator()
    {
        var tick = Environment.TickCount64;
        if (tick - _glanceAt < 120) return _glanceCache;

        _glanceAt = tick;
        _glanceCache = new ModuleGlanceState(false);
        foreach (var mod in Modules.OrderByDescending(m => m.Priority))
        {
            if (!mod.IsEnabled) continue;
            try
            {
                var g = mod.GetGlanceState();
                if (g.HasActiveIndicator) { _glanceCache = g; break; }
            }
            catch { }
        }
        return _glanceCache;
    }

    private ModuleGlanceState _glanceCache = new(false);
    private long _glanceAt;
    private (RippleThemePreset Theme, string Accent, WpfColor Art, bool Has)? _themeKey;
    private (WpfBrush, WpfBrush, WpfColor) _themeCache;

    public void RecordFrameTime(double ms)
    {
        if (_frameTimesMs.Count >= 120) _frameTimesMs.RemoveAt(0);
        _frameTimesMs.Add(ms);
    }

    public (WpfBrush ShellBrush, WpfBrush BorderBrush, WpfColor GlowColor) ResolveThemeColors()
    {
        var key = (Settings.Theme, Settings.AccentHex, Media.Snapshot.PrimaryColor, Media.Snapshot.HasSession);
        if (_themeKey is { } k && k == key) return _themeCache;
        _themeKey = key;
        _themeCache = BuildThemeColors();
        return _themeCache;
    }

    private (WpfBrush ShellBrush, WpfBrush BorderBrush, WpfColor GlowColor) BuildThemeColors()
    {
        var r = BuildThemeColorsCore();
        if (r.ShellBrush.CanFreeze) r.ShellBrush.Freeze();
        if (r.BorderBrush.CanFreeze) r.BorderBrush.Freeze();
        return r;
    }

    private (WpfBrush ShellBrush, WpfBrush BorderBrush, WpfColor GlowColor) BuildThemeColorsCore()
    {
        var accent = ParseColorOrDefault(Settings.AccentHex, WpfColor.FromRgb(108, 140, 255));
        if (Settings.Theme == RippleThemePreset.AdaptiveArt && Media.Snapshot.HasSession)
        {
            accent = Media.Snapshot.PrimaryColor;
        }

        return Settings.Theme switch
        {
            RippleThemePreset.LiquidGlass => (
                new WpfSolidColorBrush(WpfColor.FromArgb(228, 16, 18, 26)),
                new WpfSolidColorBrush(WpfColor.FromArgb(95, 255, 255, 255)),
                accent),
            RippleThemePreset.AdaptiveArt => (
                new WpfLinearGradientBrush(
                    WpfColor.FromArgb(248, (byte)(accent.R * 0.16), (byte)(accent.G * 0.16), (byte)(accent.B * 0.22)),
                    WpfColor.FromArgb(250, 8, 9, 14),
                    90.0),
                new WpfSolidColorBrush(WpfColor.FromArgb(140, accent.R, accent.G, accent.B)),
                accent),
            RippleThemePreset.NeonAurora => (
                new WpfLinearGradientBrush(
                    WpfColor.FromRgb(12, 10, 26),
                    WpfColor.FromRgb(6, 14, 22),
                    45.0),
                new WpfLinearGradientBrush(accent, WpfColor.FromRgb(34, 211, 238), 0.0),
                accent),
            RippleThemePreset.HighContrast => (
                WpfBrushes.Black,
                WpfBrushes.White,
                System.Windows.Media.Colors.Yellow),
            _ => (
                new WpfSolidColorBrush(WpfColor.FromRgb(6, 6, 8)),
                new WpfSolidColorBrush(WpfColor.FromArgb(72, accent.R, accent.G, accent.B)),
                accent)
        };
    }

    public static WpfColor ParseColorOrDefault(string? hex, WpfColor fallback)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(hex) && WpfColorConverter.ConvertFromString(hex) is WpfColor c)
            {
                return c;
            }
        }
        catch { }
        return fallback;
    }

    public string BuildDiagnosticsReport()
    {
        var proc = Process.GetCurrentProcess();
        var avgMs = _frameTimesMs.Count > 0 ? _frameTimesMs.Average() : 0;
        var fps = avgMs > 0.1 ? 1000.0 / avgMs : 60.0;
        return $"""
            RIPPLE NOTCH DOCTOR DIAGNOSTICS
            -------------------------------
            Profile: {Settings.ActiveProfileName} | Theme: {Settings.Theme}
            State: {StateMachine.CurrentState} | Active Module: {ActiveModuleId}
            Display: {ActiveScreenDeviceName} | Fullscreen App: {ActiveFullscreenAppName ?? "None"}
            Local API: {(LocalApi.IsRunning ? $"Running (127.0.0.1:{LocalApi.Port})" : $"Stopped ({LocalApi.LastError})")}
            Memory Working Set: {proc.WorkingSet64 / (1024.0 * 1024.0):F1} MB
            Avg Frame Time: {avgMs:F2} ms (~{fps:F0} fps)
            Notifications Listener: {Notifications.ListenerStatus}
            Audio CoreAudio Endpoint: {(AudioMixer.IsAvailable ? "Available" : "Unavailable")}
            Loaded Plugins: {Plugins.Plugins.Count} ({Plugins.LoadErrors.Count} errors)
            Modules Health: {string.Join(", ", _moduleHealth.Values.Select(h => $"{h.ModuleId}:{(h.IsHealthy ? "OK" : "ERR")}"))}
            """;
    }

    public void Dispose()
    {
        SettingsStore.Save(Settings);
        _ = LocalApi.DisposeAsync().AsTask();
        Notchy.Dispose();
        SystemMonitor.Dispose();
        AudioMixer.Dispose();
    }
}
