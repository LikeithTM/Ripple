using Ripple.Core.Geometry;
using Ripple.Core.StateMachine;
using Xunit;

namespace Ripple.Tests;

internal sealed class FakeClock : ITimeProvider
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);
    public void AdvanceMs(double ms) => UtcNow = UtcNow.AddMilliseconds(ms);
}

public sealed class StateMachineAndHitTestTests
{
    [Fact]
    public void HoverIntentAndCollapseGraceDelays_PreventFlicker()
    {
        var clock = new FakeClock();
        var sm = new NotchStateMachine(new NotchStateTimingConfig(HoverIntentDelayMs: 80, CollapseGraceDelayMs: 250), clock);

        // Pointer enters: first peeks, does not expand before 80ms
        sm.UpdatePointer(true, true, new Point2D(370, 16));
        Assert.Equal(NotchState.Peek, sm.CurrentState);

        clock.AdvanceMs(50);
        sm.UpdatePointer(true, true, new Point2D(370, 16));
        Assert.Equal(NotchState.Peek, sm.CurrentState);

        // Past 80ms -> expands smoothly
        clock.AdvanceMs(40);
        sm.UpdatePointer(true, true, new Point2D(370, 16));
        Assert.Equal(NotchState.Expanded, sm.CurrentState);

        // Cursor wanders briefly outside for 150ms -> remains Expanded due to 250ms grace delay
        sm.UpdatePointer(false, true, new Point2D(370, 260));
        clock.AdvanceMs(150);
        sm.Tick();
        Assert.Equal(NotchState.Expanded, sm.CurrentState);

        // Cursor returns before grace expires -> stays Expanded cleanly
        sm.UpdatePointer(true, true, new Point2D(370, 200));
        clock.AdvanceMs(200);
        sm.Tick();
        Assert.Equal(NotchState.Expanded, sm.CurrentState);

        // Cursor leaves permanently past 250ms -> collapses cleanly
        sm.UpdatePointer(false, false, new Point2D(370, 400));
        clock.AdvanceMs(260);
        sm.Tick();
        Assert.Equal(NotchState.Rest, sm.CurrentState);
    }

    [Fact]
    public void SafeCorridorHitTester_AllowsDiagonalCursorMoveTowardExpandedCorners()
    {
        var rest = new Rect2D(280, 0, 180, 32);
        var current = new Rect2D(230, 0, 280, 110);
        var expanded = new Rect2D(140, 0, 460, 220);
        var entry = new Point2D(370, 14);

        // Cursor sweeps diagonally toward bottom-left corner (x=185, y=140) while shell is mid-morph
        var diagPoint = new Point2D(185, 140);
        var inside = SafeCorridorHitTester.IsInsideInteractiveZone(
            diagPoint, rest, current, expanded, isOpenOrOpening: true, entryPoint: entry);

        Assert.True(inside);
    }

    [Fact]
    public void FullscreenAndCollisionDodge_OverrideAndRestoreState()
    {
        var clock = new FakeClock();
        var sm = new NotchStateMachine(new NotchStateTimingConfig(), clock);

        sm.SetCollisionState(true);
        Assert.Equal(NotchState.Dodging, sm.CurrentState);

        sm.SetCollisionState(false);
        clock.AdvanceMs(200);
        sm.Tick();
        Assert.Equal(NotchState.Rest, sm.CurrentState);

        sm.SetFullscreenState(true);
        Assert.Equal(NotchState.HiddenFullscreen, sm.CurrentState);

        sm.SetFullscreenState(false);
        Assert.Equal(NotchState.Rest, sm.CurrentState);
    }

    [Fact]
    public void LiveActivity_AutoExpiresBackToRest()
    {
        var clock = new FakeClock();
        var sm = new NotchStateMachine(new NotchStateTimingConfig(), clock);

        sm.ShowLiveActivity("track-1", durationMs: 3000);
        Assert.Equal(NotchState.Activity, sm.CurrentState);

        clock.AdvanceMs(2500);
        sm.Tick();
        Assert.Equal(NotchState.Activity, sm.CurrentState);

        clock.AdvanceMs(600);
        sm.Tick();
        Assert.Equal(NotchState.Rest, sm.CurrentState);
    }
}
