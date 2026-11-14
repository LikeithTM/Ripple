namespace Ripple.Core.Events;

public enum LiveActivityKind
{
    Generic = 0,
    MediaTrack = 1,
    TimerAlert = 2,
    VolumeHud = 3,
    BrightnessHud = 4,
    MicMuteHud = 5,
    CapsLockHud = 6,
    BatteryCharging = 7,
    BluetoothConnect = 8,
    Notification = 9,
    AgentEvent = 10,
    DownloadProgress = 11,
    SystemAlert = 12
}

public enum LiveActivityPriority
{
    Low = 0,
    Normal = 50,
    High = 100,
    Critical = 200
}

public sealed record LiveActivityPayload(
    string Id,
    string SourceModuleId,
    LiveActivityKind Kind,
    string Title,
    string? Subtitle = null,
    string Icon = "✦",
    string AccentHex = "#6C8CFF",
    LiveActivityPriority Priority = LiveActivityPriority.Normal,
    TimeSpan? Duration = null,
    double? Progress = null,
    string? CoalesceKey = null,
    bool BypassFocusAssist = false,
    bool RequiresAction = false,
    string? PrimaryActionLabel = null,
    string? SecondaryActionLabel = null,
    IReadOnlyDictionary<string, string>? Metadata = null);

public sealed record LiveActivityEntry(
    LiveActivityPayload Payload,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ExpiresAt);

/// <summary>
/// Represents the active split-capsule view when 1 or 2 live activities are displayed concurrently.
/// </summary>
public sealed record LiveActivityStackSnapshot(
    LiveActivityEntry? Primary,
    LiveActivityEntry? Secondary,
    int TotalActiveCount);
