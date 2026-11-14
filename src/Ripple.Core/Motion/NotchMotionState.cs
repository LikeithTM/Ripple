namespace Ripple.Core.Motion;

/// <summary>
/// Coordinates all animated channels of the notch shell and staggered content choreography.
/// </summary>
public sealed class NotchMotionState
{
    public SpringValue Width { get; }
    public SpringValue Height { get; }
    public SpringValue CornerRadius { get; }
    public SpringValue OffsetX { get; }
    public SpringValue OffsetY { get; }
    public SpringValue PeekGlow { get; }
    public SpringValue ContentReveal { get; }
    public SpringValue BlurRadius { get; }
    public SpringValue PressScale { get; }

    public NotchMotionState(double restWidth = 176, double restHeight = 32, double restRadius = 16)
    {
        Width = new SpringValue(restWidth, SpringConfig.Expand);
        Height = new SpringValue(restHeight, SpringConfig.Expand);
        CornerRadius = new SpringValue(restRadius, SpringConfig.Expand);
        OffsetX = new SpringValue(0, SpringConfig.Expand);
        OffsetY = new SpringValue(0, SpringConfig.Dodge);
        PeekGlow = new SpringValue(0, SpringConfig.Peek);
        ContentReveal = new SpringValue(0, SpringConfig.Expand);
        BlurRadius = new SpringValue(12, SpringConfig.Expand);
        PressScale = new SpringValue(1.0, SpringConfig.Press);
    }

    public bool IsAnimating =>
        !Width.IsSettled ||
        !Height.IsSettled ||
        !CornerRadius.IsSettled ||
        !OffsetX.IsSettled ||
        !OffsetY.IsSettled ||
        !PeekGlow.IsSettled ||
        !ContentReveal.IsSettled ||
        !BlurRadius.IsSettled ||
        !PressScale.IsSettled;

    public void SetShellTarget(
        double width,
        double height,
        double cornerRadius,
        double offsetY,
        double peekGlow,
        double contentReveal,
        double blurRadius,
        SpringConfig shellConfig,
        bool reducedMotion = false)
    {
        var cfg = reducedMotion ? SpringConfig.ReducedMotion : shellConfig;
        Width.SetTarget(width, cfg);
        Height.SetTarget(height, cfg);
        CornerRadius.SetTarget(cornerRadius, cfg);
        OffsetY.SetTarget(offsetY, reducedMotion ? SpringConfig.ReducedMotion : SpringConfig.Dodge);
        PeekGlow.SetTarget(peekGlow, reducedMotion ? SpringConfig.ReducedMotion : SpringConfig.Peek);

        // Content fades out *faster* than the shell shrinks so nothing clips awkwardly.
        var contentCfg = contentReveal < ContentReveal.Value
            ? (reducedMotion ? SpringConfig.ReducedMotion : new SpringConfig(680, 52, 1.0))
            : cfg;

        ContentReveal.SetTarget(contentReveal, contentCfg);
        BlurRadius.SetTarget(blurRadius, contentCfg);
    }

    public bool Step(double deltaTimeSeconds)
    {
        var active = false;
        active |= Width.Update(deltaTimeSeconds);
        active |= Height.Update(deltaTimeSeconds);
        active |= CornerRadius.Update(deltaTimeSeconds);
        active |= OffsetX.Update(deltaTimeSeconds);
        active |= OffsetY.Update(deltaTimeSeconds);
        active |= PeekGlow.Update(deltaTimeSeconds);
        active |= ContentReveal.Update(deltaTimeSeconds);
        active |= BlurRadius.Update(deltaTimeSeconds);
        active |= PressScale.Update(deltaTimeSeconds);
        return active;
    }

    /// <summary>
    /// Computes staggered element reveal progress [0..1] for element index with 25 ms cascade.
    /// </summary>
    public double GetStaggeredProgress(int elementIndex, int totalElements = 6)
    {
        var raw = Math.Clamp(ContentReveal.Value, 0.0, 1.0);
        if (totalElements <= 1)
        {
            return raw;
        }

        // When collapsing, all elements exit swiftly together without stagger lag.
        if (ContentReveal.Target < ContentReveal.Value)
        {
            return Math.Clamp(raw * 1.15, 0.0, 1.0);
        }

        var delayFraction = Math.Clamp(elementIndex * 0.075, 0.0, 0.45);
        return Math.Clamp((raw - delayFraction) / (1.0 - delayFraction), 0.0, 1.0);
    }
}
