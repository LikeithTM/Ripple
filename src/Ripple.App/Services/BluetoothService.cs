using Ripple.Core.Events;
using Ripple.Core.Modules;
using Windows.Devices.Enumeration;

namespace Ripple.App.Services;

public sealed record BluetoothDeviceItem(
    string Id,
    string Name,
    string Category,
    bool IsConnected,
    int? BatteryPercent,
    string Icon);

/// <summary>
/// Enumerates paired/connected Windows Bluetooth Classic &amp; LE peripherals, extracts battery %
/// when exposed by PnP/HID, publishes AirPods-style connect pop-up capsules, and lists audio outputs.
/// </summary>
public sealed class BluetoothService : IRippleModule
{
    private readonly LiveActivityEventBus _bus;
    private readonly AudioMixerService _audio;
    private readonly HashSet<string> _knownConnectedIds = [];
    private DateTimeOffset _lastPollAt = DateTimeOffset.MinValue;
    private bool _initialScanComplete;

    public BluetoothService(LiveActivityEventBus bus, AudioMixerService audio)
    {
        _bus = bus;
        _audio = audio;
        _ = RefreshDevicesAsync();
    }

    public string Id => "bluetooth";
    public string Name => "Devices";
    public string Icon => "ᛒ";
    public int Priority => 50;
    public bool IsEnabled { get; set; } = true;
    public IReadOnlyCollection<ModulePermission> RequiredPermissions { get; } = [ModulePermission.Bluetooth, ModulePermission.AudioDevices];
    public double PreferredExpandedWidth => 430;
    public double PreferredExpandedHeight => 240;

    public IReadOnlyList<BluetoothDeviceItem> Devices { get; private set; } = [];
    public IReadOnlyList<AudioDeviceInfo> OutputDevices { get; private set; } = [];

    public event Action? Updated;

    public ModuleGlanceState GetGlanceState()
    {
        var lowBattery = Devices.FirstOrDefault(d => d.IsConnected && d.BatteryPercent is <= 20);
        if (lowBattery is not null)
        {
            return new ModuleGlanceState(true, $"{lowBattery.BatteryPercent}%", "ᛒ", "#F59E0B");
        }

        var connected = Devices.FirstOrDefault(d => d.IsConnected && d.BatteryPercent is not null);
        return connected is not null
            ? new ModuleGlanceState(true, $"{connected.BatteryPercent}%", "ᛒ", "#38BDF8")
            : new ModuleGlanceState(false);
    }

    public void OnTick(DateTimeOffset utcNow, bool isExpanded)
    {
        var intervalSec = isExpanded ? 4 : 15;
        if ((utcNow - _lastPollAt).TotalSeconds >= intervalSec)
        {
            _lastPollAt = utcNow;
            _ = RefreshDevicesAsync();
        }
    }

    public async Task RefreshDevicesAsync()
    {
        try
        {
            OutputDevices = _audio.GetRenderDevices();

            const string aqs = "System.Devices.Aep.ProtocolId:=\"{e0cbf06c-cd8b-4647-bb8a-263b43f0f974}\" OR " +
                               "System.Devices.Aep.ProtocolId:=\"{bb7bb05e-5972-42b5-94fc-76eaa7084d49}\"";
            string[] props =
            [
                "System.Devices.Aep.IsConnected",
                "System.Devices.Aep.Category",
                "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2" // DEVPKEY_Device_BatteryLevel / Bluetooth battery %
            ];

            var infos = await DeviceInformation.FindAllAsync(aqs, props, DeviceInformationKind.AssociationEndpoint);
            var list = new List<BluetoothDeviceItem>();
            var newlyConnected = new List<BluetoothDeviceItem>();

            foreach (var d in infos)
            {
                if (string.IsNullOrWhiteSpace(d.Name)) continue;

                var isConnected = d.Properties.TryGetValue("System.Devices.Aep.IsConnected", out var connObj) && connObj is true;
                int? battery = null;
                if (d.Properties.TryGetValue("{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2", out var batObj) && batObj is byte b)
                {
                    battery = Math.Clamp((int)b, 0, 100);
                }

                var category = d.Properties.TryGetValue("System.Devices.Aep.Category", out var catObj)
                    ? catObj?.ToString() ?? "Device"
                    : "Device";
                var icon = GuessDeviceIcon(d.Name, category);

                var item = new BluetoothDeviceItem(d.Id, d.Name, category, isConnected, battery, icon);
                list.Add(item);

                if (isConnected)
                {
                    if (_initialScanComplete && !_knownConnectedIds.Contains(d.Id))
                    {
                        newlyConnected.Add(item);
                    }
                    _knownConnectedIds.Add(d.Id);
                }
                else
                {
                    _knownConnectedIds.Remove(d.Id);
                }
            }

            _initialScanComplete = true;
            Devices = list.OrderByDescending(x => x.IsConnected).ThenBy(x => x.Name).ToList();

            foreach (var conn in newlyConnected)
            {
                var batStr = conn.BatteryPercent is { } pct ? $" • {pct}% battery" : " • Connected";
                _bus.Publish(new LiveActivityPayload(
                    Id: $"bt:{conn.Id}",
                    SourceModuleId: Id,
                    Kind: LiveActivityKind.BluetoothConnect,
                    Title: conn.Name,
                    Subtitle: $"Bluetooth{batStr}",
                    Icon: conn.Icon,
                    AccentHex: "#38BDF8",
                    Priority: LiveActivityPriority.High,
                    Duration: TimeSpan.FromSeconds(4.2),
                    Progress: conn.BatteryPercent is { } p ? p / 100.0 : null,
                    CoalesceKey: $"bt-{conn.Id}"));
            }

            Updated?.Invoke();
        }
        catch
        {
            // Device enumeration unavailable on host VM/OS configuration
        }
    }

    private static string GuessDeviceIcon(string name, string category)
    {
        var lower = $"{name} {category}".ToLowerInvariant();
        if (lower.Contains("airpod") || lower.Contains("bud") || lower.Contains("head") || lower.Contains("audio")) return "🎧";
        if (lower.Contains("mouse")) return "🖱";
        if (lower.Contains("key")) return "⌨";
        if (lower.Contains("xbox") || lower.Contains("controller") || lower.Contains("dualsense")) return "🎮";
        return "ᛒ";
    }
}
