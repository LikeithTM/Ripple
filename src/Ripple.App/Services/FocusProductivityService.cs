using System.Diagnostics;
using Ripple.Core.Events;
using Ripple.Core.Modules;

namespace Ripple.App.Services;

public sealed record TodoTaskItem(string Id, string Title, bool IsDone);

public sealed record CalendarMeetingItem(
    string Title,
    DateTimeOffset StartTime,
    string JoinUrl,
    string Provider);

/// <summary>
/// Focus Timer (Pomodoro / Countdown / Stopwatch), Daily To-Do list, Sticky Note,
/// Break/Water reminders, Focus Mode distraction blocker, and Next-Meeting Calendar Glance.
/// </summary>
public sealed class FocusProductivityService : IRippleModule
{
    private readonly LiveActivityEventBus _bus;
    private readonly List<TodoTaskItem> _todos =
    [
        new("t1", "Review Ripple notch motion curves", false),
        new("t2", "Ship production release package", false)
    ];

    private TimeSpan _totalDuration = TimeSpan.FromMinutes(25);
    private TimeSpan _remaining = TimeSpan.FromMinutes(25);
    private DateTimeOffset _lastTick = DateTimeOffset.UtcNow;
    private DateTimeOffset _nextBreakReminderAt = DateTimeOffset.UtcNow.AddMinutes(45);

    public FocusProductivityService(LiveActivityEventBus bus)
    {
        _bus = bus;
        NextMeeting = new CalendarMeetingItem(
            "Design & Engineering Sync",
            DateTimeOffset.Now.AddMinutes(32),
            "https://meet.google.com/",
            "Google Meet");
    }

    public string Id => "timer";
    public string Name => "Focus";
    public string Icon => "⏱";
    public int Priority => 75;
    public bool IsEnabled { get; set; } = true;
    public IReadOnlyCollection<ModulePermission> RequiredPermissions { get; } = [];
    public double PreferredExpandedWidth => 460;
    public double PreferredExpandedHeight => 250;

    public bool IsRunning { get; private set; }
    public bool IsStopwatch { get; private set; }
    public bool FocusModeEnabled { get; set; }
    public bool BreakRemindersEnabled { get; set; } = true;
    public string Label { get; private set; } = "Deep Focus";
    public string StickyNote { get; set; } = "Quick note: Ctrl+Alt+N toggles Ripple, Ctrl+Alt+Space opens Command Palette.";
    public TimeSpan Remaining => _remaining;
    public TimeSpan TotalDuration => _totalDuration;
    public IReadOnlyList<TodoTaskItem> Todos => _todos;
    public CalendarMeetingItem? NextMeeting { get; private set; }

    public event Action? Updated;

    public ModuleGlanceState GetGlanceState()
    {
        if (IsRunning)
        {
            var progress = !IsStopwatch && _totalDuration.TotalSeconds > 0
                ? Math.Clamp(1.0 - _remaining.TotalSeconds / _totalDuration.TotalSeconds, 0, 1)
                : (double?)null;

            var label = $"{(int)_remaining.TotalMinutes:D2}:{_remaining.Seconds:D2}";
            return new ModuleGlanceState(true, label, "⏱", "#A78BFA", progress, Pulse: false);
        }

        if (NextMeeting is { } m)
        {
            var minsUntil = (m.StartTime - DateTimeOffset.Now).TotalMinutes;
            if (minsUntil is > -2 and <= 5)
            {
                return new ModuleGlanceState(true, $"In {Math.Max(0, (int)Math.Ceiling(minsUntil))}m", "📅", "#F59E0B", Pulse: true);
            }
        }

        return new ModuleGlanceState(false);
    }

    public void OnTick(DateTimeOffset utcNow, bool isExpanded)
    {
        var dt = utcNow - _lastTick;
        _lastTick = utcNow;
        if (dt.TotalSeconds <= 0 || dt.TotalSeconds > 5) return;

        if (IsRunning)
        {
            if (IsStopwatch)
            {
                _remaining += dt;
            }
            else
            {
                _remaining -= dt;
                if (_remaining <= TimeSpan.Zero)
                {
                    _remaining = TimeSpan.Zero;
                    IsRunning = false;
                    FocusModeEnabled = false;
                    _bus.Publish(new LiveActivityPayload(
                        Id: "timer:done",
                        SourceModuleId: Id,
                        Kind: LiveActivityKind.TimerAlert,
                        Title: $"{Label} Complete!",
                        Subtitle: "Take a 5 min break or snooze +5m",
                        Icon: "⏱",
                        AccentHex: "#A78BFA",
                        Priority: LiveActivityPriority.Critical,
                        Duration: TimeSpan.FromSeconds(8),
                        BypassFocusAssist: true,
                        RequiresAction: true,
                        PrimaryActionLabel: "Snooze 5m",
                        SecondaryActionLabel: "Dismiss",
                        CoalesceKey: "timer-alert"));
                }
            }
            Updated?.Invoke();
        }

        if (BreakRemindersEnabled && utcNow >= _nextBreakReminderAt)
        {
            _nextBreakReminderAt = utcNow.AddMinutes(45);
            _bus.Publish(new LiveActivityPayload(
                Id: "focus:hydrate",
                SourceModuleId: Id,
                Kind: LiveActivityKind.TimerAlert,
                Title: "Hydration & Posture Check",
                Subtitle: "Look 20 ft away for 20s and sip some water",
                Icon: "💧",
                AccentHex: "#38BDF8",
                Priority: LiveActivityPriority.Low,
                Duration: TimeSpan.FromSeconds(5),
                CoalesceKey: "break-reminder"));
        }
    }

    public void StartTimer(int minutes, string? label = null)
    {
        IsStopwatch = false;
        _totalDuration = TimeSpan.FromMinutes(Math.Max(1, minutes));
        _remaining = _totalDuration;
        Label = string.IsNullOrWhiteSpace(label) ? $"{minutes}m Focus" : label;
        IsRunning = true;
        _lastTick = DateTimeOffset.UtcNow;
        Updated?.Invoke();
    }

    public void TogglePause()
    {
        IsRunning = !IsRunning;
        _lastTick = DateTimeOffset.UtcNow;
        Updated?.Invoke();
    }

    public void ResetTimer()
    {
        IsRunning = false;
        IsStopwatch = false;
        _remaining = _totalDuration;
        Updated?.Invoke();
    }

    public void StartStopwatch()
    {
        IsStopwatch = true;
        _remaining = TimeSpan.Zero;
        Label = "Stopwatch";
        IsRunning = true;
        _lastTick = DateTimeOffset.UtcNow;
        Updated?.Invoke();
    }

    public void AddTodo(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return;
        _todos.Insert(0, new TodoTaskItem(Guid.NewGuid().ToString("N")[..8], title.Trim(), false));
        Updated?.Invoke();
    }

    public void ToggleTodo(string id)
    {
        var idx = _todos.FindIndex(t => t.Id == id);
        if (idx >= 0)
        {
            _todos[idx] = _todos[idx] with { IsDone = !_todos[idx].IsDone };
            Updated?.Invoke();
        }
    }

    public void DeleteTodo(string id)
    {
        _todos.RemoveAll(t => t.Id == id);
        Updated?.Invoke();
    }

    public void JoinNextMeeting()
    {
        if (NextMeeting is null || string.IsNullOrWhiteSpace(NextMeeting.JoinUrl)) return;
        try
        {
            Process.Start(new ProcessStartInfo(NextMeeting.JoinUrl) { UseShellExecute = true });
        }
        catch { }
    }
}
