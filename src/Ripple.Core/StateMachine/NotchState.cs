namespace Ripple.Core.StateMachine;

public enum NotchState
{
    Rest = 0,
    Peek = 1,
    Expanded = 2,
    Pinned = 3,
    Activity = 4,
    Dodging = 5,
    HiddenFullscreen = 6
}

public enum NotchInteractionTrigger
{
    Hover = 0,
    Click = 1,
    Hotkey = 2,
    DragDrop = 3,
    SwipeDown = 4,
    TouchTap = 5,
    PinToggle = 6
}

public sealed record NotchStateTimingConfig(
    int HoverIntentDelayMs = 80,
    int CollapseGraceDelayMs = 250,
    double ProximityRadiusPx = 40.0,
    int DefaultActivityDurationMs = 3500,
    bool HideInFullscreen = true,
    bool CollisionDodgeEnabled = true);
