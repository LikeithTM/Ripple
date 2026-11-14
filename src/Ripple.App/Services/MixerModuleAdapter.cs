using Ripple.App.Interop;
using Ripple.Core.Events;
using Ripple.Core.Modules;

namespace Ripple.App.Services;

/// <summary>
/// Wraps AudioMixerService as an IRippleModule and detects master volume &amp; CapsLock changes
/// to publish crisp coalesced Live Activity HUD capsules.
/// </summary>
public sealed class MixerModuleAdapter : IRippleModule
{
    private readonly LiveActivityEventBus _bus;
    private double _lastVolume;
    private bool _lastMuted;
    private bool _lastCapsLock;
    private bool _initialized;
    private DateTimeOffset _lastSessionPollAt = DateTimeOffset.MinValue;

    public MixerModuleAdapter(AudioMixerService audio, LiveActivityEventBus bus)
    {
        Audio = audio;
        _bus = bus;
        RefreshNow(forceUpdateEvent: false);
    }

    public AudioMixerService Audio { get; }
    public string Id => "mixer";
    public string Name => "Mixer";
    public string Icon => "🔊";
    public int Priority => 55;
    public bool IsEnabled { get; set; } = true;
    public IReadOnlyCollection<ModulePermission> RequiredPermissions { get; } = [ModulePermission.AudioDevices];
    public double PreferredExpandedWidth => 450;
    public double PreferredExpandedHeight => 240;

    public IReadOnlyList<AudioAppSessionInfo> Sessions { get; private set; } = [];
    public double MasterVolume { get; private set; } = 0.5;
    public bool IsMasterMuted { get; private set; }

    public event Action? Updated;

    public void RefreshNow(bool forceUpdateEvent = true)
    {
        var (vol, muted) = Audio.GetMasterVolume();
        var nextSessions = Audio.GetAppSessions();
        var changed = Math.Abs(vol - MasterVolume) > 0.015
            || muted != IsMasterMuted
            || SessionsDiffer(Sessions, nextSessions);

        MasterVolume = vol;
        IsMasterMuted = muted;
        Sessions = nextSessions;
        _lastSessionPollAt = DateTimeOffset.UtcNow;

        if (changed || forceUpdateEvent)
        {
            Updated?.Invoke();
        }
    }

    private static bool SessionsDiffer(IReadOnlyList<AudioAppSessionInfo> a, IReadOnlyList<AudioAppSessionInfo> b)
    {
        if (a.Count != b.Count) return true;
        for (var i = 0; i < a.Count; i++)
        {
            var x = a[i];
            var y = b[i];
            if (x.ProcessId != y.ProcessId
                || x.IsMuted != y.IsMuted
                || x.IsActive != y.IsActive
                || !string.Equals(x.AppName, y.AppName, StringComparison.Ordinal)
                || Math.Abs(x.Volume - y.Volume) > 0.015)
            {
                return true;
            }
        }
        return false;
    }

    public ModuleGlanceState GetGlanceState() =>
        IsMasterMuted
            ? new ModuleGlanceState(true, "Muted", "🔇", "#EF4444")
            : new ModuleGlanceState(false);

    public void OnTick(DateTimeOffset utcNow, bool isExpanded)
    {
        var (vol, muted) = Audio.GetMasterVolume();
        var caps = (Win32Interop.GetKeyState(0x14 /* VK_CAPITAL */) & 1) == 1;

        if (!_initialized)
        {
            _lastVolume = vol;
            _lastMuted = muted;
            _lastCapsLock = caps;
            _initialized = true;
        }
        else
        {
            if (Math.Abs(vol - _lastVolume) > 0.015 || muted != _lastMuted)
            {
                _lastVolume = vol;
                _lastMuted = muted;
                var pct = (int)Math.Round(vol * 100);
                _bus.Publish(new LiveActivityPayload(
                    Id: "hud:volume",
                    SourceModuleId: Id,
                    Kind: LiveActivityKind.VolumeHud,
                    Title: muted ? "Volume Muted" : $"Volume {pct}%",
                    Subtitle: "Master Output",
                    Icon: muted ? "🔇" : (pct > 50 ? "🔊" : "🔉"),
                    AccentHex: muted ? "#EF4444" : "#6C8CFF",
                    Priority: LiveActivityPriority.High,
                    Duration: TimeSpan.FromSeconds(1.8),
                    Progress: muted ? 0.0 : vol,
                    CoalesceKey: "hud-volume"));
            }

            if (caps != _lastCapsLock)
            {
                _lastCapsLock = caps;
                _bus.Publish(new LiveActivityPayload(
                    Id: "hud:capslock",
                    SourceModuleId: Id,
                    Kind: LiveActivityKind.CapsLockHud,
                    Title: caps ? "Caps Lock ON" : "Caps Lock OFF",
                    Subtitle: "Keyboard Indicator",
                    Icon: caps ? "⇪" : "⇩",
                    AccentHex: caps ? "#F59E0B" : "#94A3B8",
                    Priority: LiveActivityPriority.Normal,
                    Duration: TimeSpan.FromSeconds(1.5),
                    CoalesceKey: "hud-capslock"));
            }
        }

        var masterChanged = Math.Abs(vol - MasterVolume) > 0.015 || muted != IsMasterMuted;
        MasterVolume = vol;
        IsMasterMuted = muted;

        var pollInterval = isExpanded ? TimeSpan.FromMilliseconds(500) : TimeSpan.FromSeconds(4);
        if ((utcNow - _lastSessionPollAt) >= pollInterval)
        {
            _lastSessionPollAt = utcNow;
            var nextSessions = Audio.GetAppSessions();
            var sessionsChanged = SessionsDiffer(Sessions, nextSessions);
            Sessions = nextSessions;
            if (isExpanded && (sessionsChanged || masterChanged))
            {
                Updated?.Invoke();
            }
        }
        else if (isExpanded && masterChanged)
        {
            Updated?.Invoke();
        }
    }
}
