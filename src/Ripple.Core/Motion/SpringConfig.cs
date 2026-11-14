namespace Ripple.Core.Motion;

/// <summary>
/// Physical parameters for a damped harmonic spring transition.
/// </summary>
public sealed record SpringConfig(
    double Stiffness,
    double Damping,
    double Mass = 1.0,
    double PositionEpsilon = 0.0005,
    double VelocityEpsilon = 0.005)
{
    /// <summary>
    /// Smoothly expands the notch (~350 ms settle with ~1.9% subtle overshoot).
    /// </summary>
    public static SpringConfig Expand { get; } = new(Stiffness: 320.0, Damping: 28.0, Mass: 1.0);

    /// <summary>
    /// Snappy collapse (~250 ms settle with zero overshoot, critically damped).
    /// </summary>
    public static SpringConfig Collapse { get; } = new(Stiffness: 420.0, Damping: 41.0, Mass: 1.0);

    /// <summary>
    /// Subtle proximity peek swell (~6-10% scale + glow).
    /// </summary>
    public static SpringConfig Peek { get; } = new(Stiffness: 380.0, Damping: 30.0, Mass: 0.8);

    /// <summary>
    /// Tactile button press and drop-settle spring.
    /// </summary>
    public static SpringConfig Press { get; } = new(Stiffness: 520.0, Damping: 28.0, Mass: 0.7);

    /// <summary>
    /// Collision-avoidance upward dodge spring.
    /// </summary>
    public static SpringConfig Dodge { get; } = new(Stiffness: 360.0, Damping: 34.0, Mass: 0.9);

    /// <summary>
    /// Reduced-motion critically damped quick crossfade preset.
    /// </summary>
    public static SpringConfig ReducedMotion { get; } = new(Stiffness: 900.0, Damping: 60.0, Mass: 1.0);

    /// <summary>
    /// Computes the damping ratio zeta = c / (2 * sqrt(k * m)).
    /// Values &lt; 1 are under-damped (subtle bounce), = 1 critically damped, &gt; 1 over-damped.
    /// </summary>
    public double DampingRatio =>
        Damping / (2.0 * Math.Sqrt(Math.Max(1e-6, Stiffness * Mass)));

    /// <summary>
    /// Undamped angular frequency omega0 = sqrt(k / m) in rad/s.
    /// </summary>
    public double NaturalFrequency =>
        Math.Sqrt(Math.Max(1e-6, Stiffness / Math.Max(1e-6, Mass)));
}
