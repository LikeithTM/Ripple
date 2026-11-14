namespace Ripple.Core.Motion;

/// <summary>
/// Closed-form + sub-stepped harmonic oscillator solver that is invariant to display refresh rate
/// (60 / 120 / 144 / 240 Hz) and supports seamless mid-flight target reversals.
/// </summary>
public static class SpringSolver
{
    private const double MaxStepSeconds = 1.0 / 240.0;

    /// <summary>
    /// Advances a 1D spring from (position, velocity) toward target over deltaTimeSeconds.
    /// Uses analytical closed-form integration per clamped step for frame-rate independence.
    /// </summary>
    public static (double Position, double Velocity, bool IsSettled) Step(
        double position,
        double velocity,
        double target,
        double deltaTimeSeconds,
        SpringConfig config)
    {
        if (deltaTimeSeconds <= 0)
        {
            var settled = IsAtRest(position, velocity, target, config);
            return (position, velocity, settled);
        }

        // Clamp absurd frame stalls (e.g., sleep/wake or debugger pause) to avoid instability.
        var remaining = Math.Min(deltaTimeSeconds, 0.1);
        var x = position;
        var v = velocity;

        while (remaining > 1e-6)
        {
            var dt = Math.Min(remaining, MaxStepSeconds);
            (x, v) = StepAnalytical(x, v, target, dt, config);
            remaining -= dt;
        }

        if (IsAtRest(x, v, target, config))
        {
            return (target, 0.0, true);
        }

        return (x, v, false);
    }

    private static (double Position, double Velocity) StepAnalytical(
        double position,
        double velocity,
        double target,
        double dt,
        SpringConfig config)
    {
        var displacement = position - target;
        var m = Math.Max(1e-4, config.Mass);
        var k = Math.Max(1e-4, config.Stiffness);
        var c = Math.Max(0.0, config.Damping);

        var omega0 = Math.Sqrt(k / m);
        var zeta = c / (2.0 * Math.Sqrt(k * m));

        double newDisp;
        double newVel;

        if (zeta < 1.0 - 1e-6)
        {
            // Under-damped: smooth organic curve with controlled overshoot
            var omegaD = omega0 * Math.Sqrt(1.0 - zeta * zeta);
            var exp = Math.Exp(-zeta * omega0 * dt);
            var a = displacement;
            var b = (velocity + zeta * omega0 * displacement) / omegaD;

            var cos = Math.Cos(omegaD * dt);
            var sin = Math.Sin(omegaD * dt);

            newDisp = exp * (a * cos + b * sin);
            newVel = exp * (
                (-zeta * omega0 * a + omegaD * b) * cos +
                (-zeta * omega0 * b - omegaD * a) * sin);
        }
        else if (Math.Abs(zeta - 1.0) <= 1e-6)
        {
            // Critically damped: fastest return without oscillation
            var exp = Math.Exp(-omega0 * dt);
            var a = displacement;
            var b = velocity + omega0 * displacement;

            newDisp = exp * (a + b * dt);
            newVel = exp * (b - omega0 * (a + b * dt));
        }
        else
        {
            // Over-damped
            var sqrtTerm = Math.Sqrt(zeta * zeta - 1.0);
            var r1 = -omega0 * (zeta - sqrtTerm);
            var r2 = -omega0 * (zeta + sqrtTerm);
            var c2 = (velocity - r1 * displacement) / (r2 - r1);
            var c1 = displacement - c2;

            var e1 = Math.Exp(r1 * dt);
            var e2 = Math.Exp(r2 * dt);

            newDisp = c1 * e1 + c2 * e2;
            newVel = c1 * r1 * e1 + c2 * r2 * e2;
        }

        return (target + newDisp, newVel);
    }

    public static bool IsAtRest(double position, double velocity, double target, SpringConfig config) =>
        Math.Abs(position - target) <= config.PositionEpsilon &&
        Math.Abs(velocity) <= config.VelocityEpsilon;
}
