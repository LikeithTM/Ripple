namespace Ripple.Core.Modules;

public sealed record ContextSignals(
    bool IsAgentWaitingApproval,
    bool IsAgentRunning,
    bool HasUnreadPriorityNotification,
    TimeSpan? NextMeetingIn,
    bool IsTimerRunning,
    bool IsMediaPlaying,
    bool IsDownloadActive,
    bool IsDragDropActive);

/// <summary>
/// Context-aware morphing engine: picks the most relevant module automatically according to live context
/// and user-editable module priority rules.
/// </summary>
public sealed class ContextMorphEngine
{
    public IReadOnlyList<string> PriorityOrder { get; set; } =
    [
        "shelf",
        "notchy",
        "notifications",
        "timer",
        "music",
        "system",
        "clipboard",
        "apps",
        "mixer",
        "bluetooth",
        "utilities"
    ];

    public string SelectBestModuleId(
        ContextSignals signals,
        IReadOnlyCollection<string> enabledModuleIds,
        string fallbackModuleId = "music")
    {
        bool Enabled(string id) => enabledModuleIds.Contains(id, StringComparer.OrdinalIgnoreCase);

        if (signals.IsDragDropActive && Enabled("shelf"))
            return "shelf";

        if (signals.IsAgentWaitingApproval && Enabled("notchy"))
            return "notchy";

        if (signals.IsAgentRunning && Enabled("notchy"))
            return "notchy";

        if (signals.HasUnreadPriorityNotification && Enabled("notifications"))
            return "notifications";

        if (signals.NextMeetingIn is { } meetingIn && meetingIn <= TimeSpan.FromMinutes(5) && meetingIn > TimeSpan.FromMinutes(-5) && Enabled("timer"))
            return "timer";

        if (signals.IsTimerRunning && Enabled("timer"))
            return "timer";

        if (signals.IsMediaPlaying && Enabled("music"))
            return "music";

        if (signals.IsDownloadActive && Enabled("system"))
            return "system";

        foreach (var candidate in PriorityOrder)
        {
            if (string.Equals(candidate, fallbackModuleId, StringComparison.OrdinalIgnoreCase) && Enabled(candidate))
            {
                return candidate;
            }
        }

        return Enabled(fallbackModuleId) ? fallbackModuleId : (enabledModuleIds.FirstOrDefault() ?? "music");
    }
}
