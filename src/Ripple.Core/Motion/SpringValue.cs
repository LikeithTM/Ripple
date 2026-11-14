namespace Ripple.Core.Motion;

/// <summary>
/// A scalar value animated via spring physics that retains momentum across target changes.
/// </summary>
public sealed class SpringValue
{
    public SpringValue(double initialValue, SpringConfig? defaultConfig = null)
    {
        Value = initialValue;
        Target = initialValue;
        Config = defaultConfig ?? SpringConfig.Expand;
    }

    public double Value { get; private set; }
    public double Velocity { get; private set; }
    public double Target { get; private set; }
    public SpringConfig Config { get; set; }
    public bool IsSettled => SpringSolver.IsAtRest(Value, Velocity, Target, Config);

    public void SetTarget(double target, SpringConfig? overrideConfig = null)
    {
        Target = target;
        if (overrideConfig is not null)
        {
            Config = overrideConfig;
        }
    }

    public void SnapTo(double value)
    {
        Value = value;
        Target = value;
        Velocity = 0.0;
    }

    public void Impulse(double deltaVelocity)
    {
        Velocity += deltaVelocity;
    }

    public bool Update(double deltaTimeSeconds)
    {
        if (IsSettled)
        {
            Value = Target;
            Velocity = 0.0;
            return false;
        }

        var (pos, vel, settled) = SpringSolver.Step(Value, Velocity, Target, deltaTimeSeconds, Config);
        Value = pos;
        Velocity = vel;
        return !settled;
    }
}
