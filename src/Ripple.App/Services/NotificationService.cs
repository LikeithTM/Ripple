using Ripple.Core.Events;
using Ripple.Core.Modules;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace Ripple.App.Services;

public sealed record MirroredNotification(
    string Id,
    string AppName,
    string Title,
    string Body,
    DateTimeOffset ReceivedAt,
    bool IsPriority,
    bool IsCallOrMessage);

/// <summary>
/// Mirrors Windows Toast Notifications via UserNotificationListener when permitted (MSIX/Store identity),
/// and provides graceful status explanations + local notification mirroring in unpackaged desktop mode.
/// </summary>
public sealed class NotificationService : IRippleModule
{
    private readonly LiveActivityEventBus _bus;
    private readonly List<MirroredNotification> _history = [];
    private readonly HashSet<string> _mutedApps = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<uint> _seenListenerIds = [];
    private UserNotificationListener? _listener;
    private DateTimeOffset _lastPollAt = DateTimeOffset.MinValue;

    public NotificationService(LiveActivityEventBus bus)
    {
        _bus = bus;
        _ = TryInitListenerAsync();
    }

    public string Id => "notifications";
    public string Name => "Alerts";
    public string Icon => "🔔";
    public int Priority => 85;
    public bool IsEnabled { get; set; } = true;
    public IReadOnlyCollection<ModulePermission> RequiredPermissions { get; } = [ModulePermission.Notifications];
    public double PreferredExpandedWidth => 450;
    public double PreferredExpandedHeight => 240;

    public string ListenerStatus { get; private set; } = "Checking Windows Notification Listener…";
    public bool HasUnreadPriority => _history.Count > 0 && _history.Any(h => h.IsPriority);
    public IReadOnlyList<MirroredNotification> History => _history;

    public event Action? Updated;

    public ModuleGlanceState GetGlanceState() =>
        _history.Count > 0
            ? new ModuleGlanceState(true, $"{_history.Count}", "🔔", "#F472B6", Pulse: HasUnreadPriority)
            : new ModuleGlanceState(false);

    public void OnTick(DateTimeOffset utcNow, bool isExpanded)
    {
        if (_listener is null) return;
        var interval = isExpanded ? 2 : 5;
        if ((utcNow - _lastPollAt).TotalSeconds >= interval)
        {
            _lastPollAt = utcNow;
            _ = PollNotificationsAsync();
        }
    }

    private async Task TryInitListenerAsync()
    {
        try
        {
            _listener = UserNotificationListener.Current;
            var access = await _listener.RequestAccessAsync();
            ListenerStatus = access switch
            {
                UserNotificationListenerAccessStatus.Allowed => "Connected (Live Windows Toasts)",
                UserNotificationListenerAccessStatus.Denied => "Permission denied in Windows Settings > Notifications",
                _ => "Packaged app identity required for OS toast mirror; Local API & Agent alerts active"
            };

            if (access != UserNotificationListenerAccessStatus.Allowed)
            {
                _listener = null;
            }
            else
            {
                await PollNotificationsAsync();
            }
        }
        catch
        {
            _listener = null;
            ListenerStatus = "OS Toast Mirror unavailable (unpackaged run); Local API & Agent alerts active";
        }
        Updated?.Invoke();
    }

    private async Task PollNotificationsAsync()
    {
        if (_listener is null) return;
        try
        {
            var notifs = await _listener.GetNotificationsAsync(NotificationKinds.Toast);
            foreach (var n in notifs)
            {
                if (!_seenListenerIds.Add(n.Id)) continue;
                var app = n.AppInfo?.DisplayInfo?.DisplayName ?? "Windows";
                if (_mutedApps.Contains(app)) continue;

                var texts = n.Notification.Visual.GetBinding(KnownNotificationBindings.ToastGeneric)?
                    .GetTextElements().Select(t => t.Text).ToList() ?? [];
                var title = texts.FirstOrDefault() ?? app;
                var body = texts.Count > 1 ? string.Join(" — ", texts.Skip(1)) : "";
                PushNotification(app, title, body, isPriority: false);
            }
        }
        catch { }
    }

    public void PushNotification(string appName, string title, string body, bool isPriority = false)
    {
        if (_mutedApps.Contains(appName)) return;

        var item = new MirroredNotification(
            Guid.NewGuid().ToString("N")[..10],
            appName,
            title,
            body,
            DateTimeOffset.Now,
            isPriority,
            appName.Contains("Teams", StringComparison.OrdinalIgnoreCase) ||
            appName.Contains("Slack", StringComparison.OrdinalIgnoreCase) ||
            appName.Contains("Discord", StringComparison.OrdinalIgnoreCase));

        _history.Insert(0, item);
        if (_history.Count > 40) _history.RemoveRange(40, _history.Count - 40);

        _bus.Publish(new LiveActivityPayload(
            Id: $"notif:{item.Id}",
            SourceModuleId: Id,
            Kind: LiveActivityKind.Notification,
            Title: $"{appName}: {title}",
            Subtitle: body,
            Icon: "🔔",
            AccentHex: isPriority ? "#F472B6" : "#6C8CFF",
            Priority: isPriority ? LiveActivityPriority.High : LiveActivityPriority.Normal,
            Duration: TimeSpan.FromSeconds(4)));

        Updated?.Invoke();
    }

    public void MuteApp(string appName)
    {
        _mutedApps.Add(appName);
        _history.RemoveAll(h => string.Equals(h.AppName, appName, StringComparison.OrdinalIgnoreCase));
        Updated?.Invoke();
    }

    public void Dismiss(string id)
    {
        _history.RemoveAll(h => h.Id == id);
        Updated?.Invoke();
    }

    public void ClearAll()
    {
        _history.Clear();
        Updated?.Invoke();
    }
}
