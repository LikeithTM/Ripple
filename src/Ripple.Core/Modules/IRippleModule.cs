namespace Ripple.Core.Modules;

public enum ModulePermission
{
    MediaControl,
    AudioDevices,
    Bluetooth,
    SystemStats,
    Clipboard,
    FileSystem,
    Notifications,
    ScreenCapture,
    CameraPreview,
    LocalNetworkApi
}

public sealed record ModuleGlanceState(
    bool HasActiveIndicator,
    string Label = "",
    string Icon = "",
    string AccentHex = "#6C8CFF",
    double? Progress = null,
    bool Pulse = false,
    string? StatusText = null);

public sealed record ModuleHealthInfo(
    string ModuleId,
    bool IsHealthy,
    string StatusMessage,
    DateTimeOffset? LastErrorAt = null);

/// <summary>
/// Contract implemented by every Ripple module (Music, System, Bluetooth, Clipboard, Shelf, Timer, Mixer, Notifications, Notchy, Utilities, Plugins).
/// </summary>
public interface IRippleModule
{
    string Id { get; }
    string Name { get; }
    string Icon { get; }
    int Priority { get; }
    bool IsEnabled { get; set; }
    IReadOnlyCollection<ModulePermission> RequiredPermissions { get; }
    double PreferredExpandedWidth { get; }
    double PreferredExpandedHeight { get; }
    ModuleGlanceState GetGlanceState();
    void OnTick(DateTimeOffset utcNow, bool isExpanded);
}
