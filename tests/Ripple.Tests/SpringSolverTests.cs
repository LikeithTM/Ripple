using Ripple.Core.Motion;
using Xunit;

namespace Ripple.Tests;

public sealed class SpringSolverTests
{
    [Fact]
    public void ExpandSpring_SettlesSmoothlyWithSubtleOvershoot()
    {
        var spring = new SpringValue(180.0, SpringConfig.Expand);
        spring.SetTarget(460.0);

        var maxSeen = 180.0;
        var steps = 0;
        while (!spring.IsSettled && steps < 300)
        {
            spring.Update(1.0 / 120.0);
            maxSeen = Math.Max(maxSeen, spring.Value);
            steps++;
        }

        Assert.True(spring.IsSettled);
        Assert.Equal(460.0, spring.Value, precision: 3);
        var overshootFraction = (maxSeen - 460.0) / (460.0 - 180.0);
        Assert.InRange(overshootFraction, 0.005, 0.045);
    }

    [Fact]
    public void CollapseSpring_CriticallyDampedZeroOvershoot()
    {
        var spring = new SpringValue(460.0, SpringConfig.Collapse);
        spring.SetTarget(180.0);

        var minSeen = 460.0;
        for (var i = 0; i < 240; i++)
        {
            spring.Update(1.0 / 120.0);
            minSeen = Math.Min(minSeen, spring.Value);
        }

        Assert.True(spring.IsSettled);
        Assert.True(minSeen >= 180.0 - 0.01, $"Expected zero undershoot below 180, got {minSeen}");
    }

    [Fact]
    public void InterruptibleReversal_PreservesPositionAndMomentumWithoutSnap()
    {
        var spring = new SpringValue(180.0, SpringConfig.Expand);
        spring.SetTarget(460.0);

        for (var i = 0; i < 12; i++)
        {
            spring.Update(1.0 / 120.0);
        }

        var posBeforeReverse = spring.Value;
        var velBeforeReverse = spring.Velocity;
        Assert.InRange(posBeforeReverse, 190.0, 440.0);
        Assert.True(velBeforeReverse > 0);

        // Pointer leaves mid-animation: reverse target cleanly from current position & velocity
        spring.SetTarget(180.0, SpringConfig.Collapse);
        Assert.Equal(posBeforeReverse, spring.Value);
        Assert.Equal(velBeforeReverse, spring.Velocity);

        for (var i = 0; i < 240; i++)
        {
            spring.Update(1.0 / 120.0);
        }
        Assert.True(spring.IsSettled);
        Assert.Equal(180.0, spring.Value, precision: 3);
    }

    [Theory]
    [InlineData(60)]
    [InlineData(120)]
    [InlineData(144)]
    [InlineData(240)]
    public void RefreshRateInvariance_ProducesConsistentTrajectoriesAcrossHz(int hz)
    {
        var spring = new SpringValue(0.0, SpringConfig.Expand);
        spring.SetTarget(100.0);

        var dt = 1.0 / hz;
        var steps = hz / 10; // exact integer frames at ~100ms
        var totalTime = steps * dt;
        for (var i = 0; i < steps; i++)
        {
            spring.Update(dt);
        }

        // Exact closed-form analytical evaluation at the same elapsed time must match multi-frame stepping
        var (expectedPos, _, _) = SpringSolver.Step(0.0, 0.0, 100.0, totalTime, SpringConfig.Expand);
        Assert.Equal(expectedPos, spring.Value, precision: 5);
    }

    [Fact]
    public void MotionState_ContentFadesOutFasterThanShellShrinks()
    {
        var motion = new NotchMotionState();
        motion.SetShellTarget(460, 220, 26, 0, 0.35, 1.0, 0.0, SpringConfig.Expand);
        for (var i = 0; i < 120; i++) motion.Step(1.0 / 120.0);

        motion.SetShellTarget(180, 32, 16, 0, 0.0, 0.0, 12.0, SpringConfig.Collapse);
        for (var i = 0; i < 15; i++) motion.Step(1.0 / 120.0); // 125ms into collapse

        var normalizedWidth = (motion.Width.Value - 180) / (460 - 180);
        Assert.True(motion.ContentReveal.Value < normalizedWidth);
    }
}
