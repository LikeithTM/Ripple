namespace Ripple.Core.StateMachine;

/// <summary>
/// Clock abstraction so state-machine delays and 24-hour soak tests are 100% deterministic.
/// </summary>
public interface ITimeProvider
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemTimeProvider : ITimeProvider
{
    public static SystemTimeProvider Instance { get; } = new();
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
