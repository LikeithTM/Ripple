using Ripple.Core.Events;
using Ripple.Core.Geometry;
using Xunit;

namespace Ripple.Tests;

public sealed class GeometryAndBusTests
{
    [Theory]
    [InlineData(NotchLayoutMode.Notch)]
    [InlineData(NotchLayoutMode.FloatingIsland)]
    [InlineData(NotchLayoutMode.FullBar)]
    public void GeometryBuilder_ProducesValidClosedPathWithConcaveEarsAndSquircleBounds(NotchLayoutMode mode)
    {
        var res = NotchGeometryBuilder.Build(new NotchGeometrySpec(
            CanvasWidth: 740,
            CanvasHeight: 360,
            BodyWidth: 220,
            BodyHeight: 36,
            CornerRadius: 18,
            EarRadius: 12,
            OffsetY: mode == NotchLayoutMode.FloatingIsland ? 10 : 0,
            LayoutMode: mode));

        Assert.NotEmpty(res.Segments);
        Assert.StartsWith("M ", res.SvgPathData);
        Assert.EndsWith(" Z", res.SvgPathData);
        Assert.True(res.BodyBounds.Width >= 219.9);
        Assert.True(res.TotalVisualBounds.Width >= res.BodyBounds.Width);
    }

    [Fact]
    public void LiveActivityEventBus_SupportsCoalescingPriorityAndDualSplitStack()
    {
        var clock = new FakeClock();
        var bus = new LiveActivityEventBus(clock, rateLimitMs: 10);

        // Coalesce rapid volume HUD updates into a single entry
        Assert.True(bus.Publish(new LiveActivityPayload("v1", "mixer", LiveActivityKind.VolumeHud, "Volume 40%", CoalesceKey: "vol")));
        Assert.True(bus.Publish(new LiveActivityPayload("v2", "mixer", LiveActivityKind.VolumeHud, "Volume 65%", CoalesceKey: "vol")));

        // Add higher-priority Agent prompt from another module -> creates dual split capsule stack
        Assert.True(bus.Publish(new LiveActivityPayload("a1", "notchy", LiveActivityKind.AgentEvent, "Approve edit?", Priority: LiveActivityPriority.Critical, RequiresAction: true)));

        var snap = bus.GetSnapshot();
        Assert.Equal(2, snap.TotalActiveCount);
        Assert.NotNull(snap.Primary);
        Assert.NotNull(snap.Secondary);
        Assert.Equal("Approve edit?", snap.Primary!.Payload.Title);
        Assert.Equal("Volume 65%", snap.Secondary!.Payload.Title);
    }

    [Fact]
    public void LiveActivityEventBus_SuppressesNonCriticalWhenFocusAssistActive()
    {
        var clock = new FakeClock();
        var bus = new LiveActivityEventBus(clock) { IsFocusAssistActive = true };

        Assert.False(bus.Publish(new LiveActivityPayload("n1", "notifications", LiveActivityKind.Notification, "Chat ping")));
        // HUD volume adjustments still work during Focus Assist
        Assert.True(bus.Publish(new LiveActivityPayload("v1", "mixer", LiveActivityKind.VolumeHud, "Volume 50%", CoalesceKey: "vol")));
    }
}
