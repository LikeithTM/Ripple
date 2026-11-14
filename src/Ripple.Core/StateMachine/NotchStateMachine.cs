using Ripple.Core.Geometry;

namespace Ripple.Core.StateMachine;

/// <summary>
/// Explicit, interruptible, unit-tested state machine governing Rest, Peek, Expanded, Pinned,
/// Live Activity, Collision Dodging, and Fullscreen Hide states.
/// </summary>
public sealed class NotchStateMachine
{
    private readonly ITimeProvider _clock;
    private DateTimeOffset? _hoverEnterStartedAt;
    private DateTimeOffset? _hoverLeaveStartedAt;
    private DateTimeOffset? _activityExpiresAt;
    private DateTimeOffset? _dodgeResumeAt;
    private Point2D? _hoverEntryPoint;

    public NotchStateMachine(
        NotchStateTimingConfig? timing = null,
        ITimeProvider? clock = null)
    {
        Timing = timing ?? new NotchStateTimingConfig();
        _clock = clock ?? SystemTimeProvider.Instance;
    }

    public NotchStateTimingConfig Timing { get; set; }
    public NotchState CurrentState { get; private set; } = NotchState.Rest;
    public bool IsPointerOver { get; private set; }
    public bool IsInProximity { get; private set; }
    public bool IsFullscreenBlocked { get; private set; }
    public bool IsCollisionBlocking { get; private set; }
    public bool IsDragPayloadActive { get; private set; }
    public string? ActiveActivityId { get; private set; }
    public Point2D? HoverEntryPoint => _hoverEntryPoint;

    public event Action<NotchState, NotchState>? StateChanged;

    public void UpdatePointer(
        bool isInsideHitZone,
        bool isInsideProximityHalo,
        Point2D pointerLocation,
        bool allowHoverTrigger = true)
    {
        var now = _clock.UtcNow;
        IsPointerOver = isInsideHitZone;
        IsInProximity = isInsideProximityHalo;

        if (IsFullscreenBlocked && Timing.HideInFullscreen)
        {
            TransitionTo(NotchState.HiddenFullscreen);
            return;
        }

        if (IsCollisionBlocking && Timing.CollisionDodgeEnabled &&
            CurrentState is NotchState.Rest or NotchState.Peek or NotchState.Dodging)
        {
            _hoverEnterStartedAt = null;
            _hoverLeaveStartedAt = null;
            TransitionTo(NotchState.Dodging);
            return;
        }

        if (CurrentState == NotchState.Dodging && !IsCollisionBlocking)
        {
            TransitionTo(NotchState.Rest);
        }

        if (CurrentState == NotchState.Pinned)
        {
            _hoverLeaveStartedAt = null;
            return;
        }

        if (isInsideHitZone)
        {
            _hoverLeaveStartedAt = null;
            _hoverEntryPoint ??= pointerLocation;

            if (CurrentState == NotchState.Expanded)
            {
                return;
            }

            if (IsDragPayloadActive || !allowHoverTrigger)
            {
                if (IsDragPayloadActive)
                {
                    TransitionTo(NotchState.Expanded);
                }
                return;
            }

            // Expand immediately if already in Activity capsule and user moves into it
            if (CurrentState == NotchState.Activity)
            {
                TransitionTo(NotchState.Expanded);
                return;
            }

            _hoverEnterStartedAt ??= now;
            var elapsedMs = (now - _hoverEnterStartedAt.Value).TotalMilliseconds;
            if (elapsedMs >= Math.Clamp(Timing.HoverIntentDelayMs, 0, 400))
            {
                _hoverEnterStartedAt = null;
                TransitionTo(NotchState.Expanded);
            }
            else if (CurrentState == NotchState.Rest)
            {
                TransitionTo(NotchState.Peek);
            }
        }
        else
        {
            _hoverEnterStartedAt = null;

            if (CurrentState == NotchState.Expanded && !IsDragPayloadActive)
            {
                _hoverLeaveStartedAt ??= now;
                var leaveMs = (now - _hoverLeaveStartedAt.Value).TotalMilliseconds;
                if (leaveMs >= Math.Max(0, Timing.CollapseGraceDelayMs))
                {
                    _hoverLeaveStartedAt = null;
                    _hoverEntryPoint = null;
                    CollapseToAppropriateIdleState(now);
                }
                return;
            }

            if (CurrentState == NotchState.Peek && !isInsideProximityHalo)
            {
                _hoverEntryPoint = null;
                TransitionTo(NotchState.Rest);
            }
            else if (CurrentState == NotchState.Rest && isInsideProximityHalo && !IsCollisionBlocking)
            {
                TransitionTo(NotchState.Peek);
            }
        }
    }

    public void Tick()
    {
        var now = _clock.UtcNow;

        if (IsFullscreenBlocked && Timing.HideInFullscreen)
        {
            TransitionTo(NotchState.HiddenFullscreen);
            return;
        }

        if (CurrentState == NotchState.HiddenFullscreen && !IsFullscreenBlocked)
        {
            CollapseToAppropriateIdleState(now);
            return;
        }

        if (CurrentState == NotchState.Dodging &&
            !IsCollisionBlocking &&
            (_dodgeResumeAt is null || now >= _dodgeResumeAt.Value))
        {
            CollapseToAppropriateIdleState(now);
            return;
        }

        if (CurrentState == NotchState.Expanded &&
            !IsPointerOver &&
            !IsDragPayloadActive &&
            _hoverLeaveStartedAt is { } leftAt &&
            (now - leftAt).TotalMilliseconds >= Math.Max(0, Timing.CollapseGraceDelayMs))
        {
            _hoverLeaveStartedAt = null;
            _hoverEntryPoint = null;
            CollapseToAppropriateIdleState(now);
            return;
        }

        if (IsPointerOver &&
            CurrentState is NotchState.Rest or NotchState.Peek &&
            _hoverEnterStartedAt is { } enteredAt &&
            (now - enteredAt).TotalMilliseconds >= Math.Clamp(Timing.HoverIntentDelayMs, 0, 400))
        {
            _hoverEnterStartedAt = null;
            TransitionTo(NotchState.Expanded);
            return;
        }

        if (CurrentState == NotchState.Activity &&
            _activityExpiresAt is { } expires &&
            now >= expires &&
            !IsPointerOver)
        {
            ActiveActivityId = null;
            _activityExpiresAt = null;
            TransitionTo(IsInProximity ? NotchState.Peek : NotchState.Rest);
        }
    }

    public void HandleTrigger(NotchInteractionTrigger trigger)
    {
        var now = _clock.UtcNow;
        if (IsFullscreenBlocked && Timing.HideInFullscreen && trigger != NotchInteractionTrigger.Hotkey)
        {
            return;
        }

        switch (trigger)
        {
            case NotchInteractionTrigger.Hotkey:
                if (CurrentState is NotchState.Expanded or NotchState.Pinned)
                {
                    Collapse();
                }
                else
                {
                    TransitionTo(NotchState.Pinned);
                }
                break;

            case NotchInteractionTrigger.PinToggle:
                if (CurrentState == NotchState.Pinned)
                {
                    TransitionTo(IsPointerOver ? NotchState.Expanded : NotchState.Rest);
                }
                else
                {
                    TransitionTo(NotchState.Pinned);
                }
                break;

            case NotchInteractionTrigger.Click:
            case NotchInteractionTrigger.SwipeDown:
            case NotchInteractionTrigger.TouchTap:
                if (CurrentState is NotchState.Rest or NotchState.Peek or NotchState.Activity or NotchState.Dodging)
                {
                    TransitionTo(NotchState.Expanded);
                }
                else if (trigger == NotchInteractionTrigger.TouchTap && CurrentState == NotchState.Expanded)
                {
                    Collapse();
                }
                break;

            case NotchInteractionTrigger.DragDrop:
                IsDragPayloadActive = true;
                TransitionTo(NotchState.Expanded);
                break;

            case NotchInteractionTrigger.Hover:
                _hoverEnterStartedAt ??= now;
                break;
        }
    }

    public void EndDragDrop(bool keepOpenBriefly = false)
    {
        IsDragPayloadActive = false;
        if (!keepOpenBriefly && !IsPointerOver && CurrentState == NotchState.Expanded)
        {
            _hoverLeaveStartedAt = _clock.UtcNow;
        }
    }

    public void ShowLiveActivity(string activityId, int? durationMs = null)
    {
        if (IsFullscreenBlocked && Timing.HideInFullscreen)
        {
            return;
        }

        ActiveActivityId = activityId;
        var ttl = durationMs ?? Timing.DefaultActivityDurationMs;
        _activityExpiresAt = ttl > 0 ? _clock.UtcNow.AddMilliseconds(ttl) : null;

        // Don't interrupt user if already actively browsing expanded or pinned panel
        if (CurrentState is NotchState.Rest or NotchState.Peek or NotchState.Activity or NotchState.Dodging)
        {
            TransitionTo(NotchState.Activity);
        }
    }

    public void DismissLiveActivity()
    {
        ActiveActivityId = null;
        _activityExpiresAt = null;
        if (CurrentState == NotchState.Activity)
        {
            TransitionTo(IsInProximity ? NotchState.Peek : NotchState.Rest);
        }
    }

    public void SetFullscreenState(bool isFullscreenAppActive)
    {
        IsFullscreenBlocked = isFullscreenAppActive;
        if (isFullscreenAppActive && Timing.HideInFullscreen)
        {
            _hoverEnterStartedAt = null;
            _hoverLeaveStartedAt = null;
            IsDragPayloadActive = false;
            TransitionTo(NotchState.HiddenFullscreen);
        }
        else if (!isFullscreenAppActive && CurrentState == NotchState.HiddenFullscreen)
        {
            CollapseToAppropriateIdleState(_clock.UtcNow);
        }
    }

    public void SetCollisionState(bool cursorThreatensUnderlyingControls)
    {
        IsCollisionBlocking = cursorThreatensUnderlyingControls;
        if (!Timing.CollisionDodgeEnabled)
        {
            return;
        }

        if (cursorThreatensUnderlyingControls && CurrentState is NotchState.Rest or NotchState.Peek)
        {
            _dodgeResumeAt = null;
            TransitionTo(NotchState.Dodging);
        }
        else if (!cursorThreatensUnderlyingControls && CurrentState == NotchState.Dodging)
        {
            _dodgeResumeAt = _clock.UtcNow.AddMilliseconds(180);
        }
    }

    public void Collapse()
    {
        _hoverEnterStartedAt = null;
        _hoverLeaveStartedAt = null;
        _hoverEntryPoint = null;
        IsDragPayloadActive = false;
        CollapseToAppropriateIdleState(_clock.UtcNow);
    }

    private void CollapseToAppropriateIdleState(DateTimeOffset now)
    {
        if (IsFullscreenBlocked && Timing.HideInFullscreen)
        {
            TransitionTo(NotchState.HiddenFullscreen);
            return;
        }

        if (ActiveActivityId is not null && (_activityExpiresAt is null || now < _activityExpiresAt.Value))
        {
            TransitionTo(NotchState.Activity);
            return;
        }

        if (IsCollisionBlocking && Timing.CollisionDodgeEnabled)
        {
            TransitionTo(NotchState.Dodging);
            return;
        }

        TransitionTo(IsInProximity ? NotchState.Peek : NotchState.Rest);
    }

    private void TransitionTo(NotchState next)
    {
        if (CurrentState == next)
        {
            return;
        }

        var prev = CurrentState;
        CurrentState = next;
        StateChanged?.Invoke(prev, next);
    }
}
