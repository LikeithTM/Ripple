using Ripple.Core.StateMachine;

namespace Ripple.Core.Events;

/// <summary>
/// Typed publish/subscribe Live Activity event bus with priority ordering, TTL eviction,
/// coalescing by key (for HUD sliders), rate limiting, Focus Assist filtering, and split-capsule stacking.
/// </summary>
public sealed class LiveActivityEventBus
{
    private readonly ITimeProvider _clock;
    private readonly object _sync = new();
    private readonly List<LiveActivityEntry> _entries = [];
    private readonly Dictionary<string, DateTimeOffset> _lastPublishBySource = [];
    private readonly int _minIntervalMs;

    public LiveActivityEventBus(ITimeProvider? clock = null, int rateLimitMs = 50)
    {
        _clock = clock ?? SystemTimeProvider.Instance;
        _minIntervalMs = Math.Max(0, rateLimitMs);
    }

    public bool IsFocusAssistActive { get; set; }

    public event Action<LiveActivityStackSnapshot>? StackUpdated;

    public bool Publish(LiveActivityPayload payload)
    {
        var now = _clock.UtcNow;

        lock (_sync)
        {
            PruneExpiredInternal(now);

            if (IsFocusAssistActive &&
                !payload.BypassFocusAssist &&
                payload.Priority < LiveActivityPriority.Critical &&
                payload.Kind is not (LiveActivityKind.VolumeHud or LiveActivityKind.BrightnessHud or LiveActivityKind.MicMuteHud or LiveActivityKind.CapsLockHud))
            {
                return false;
            }

            // Rate-limit noisy sources unless updating an existing coalesce key
            var rateKey = payload.CoalesceKey ?? payload.SourceModuleId;
            if (payload.CoalesceKey is null &&
                _minIntervalMs > 0 &&
                _lastPublishBySource.TryGetValue(rateKey, out var last) &&
                (now - last).TotalMilliseconds < _minIntervalMs)
            {
                return false;
            }

            _lastPublishBySource[rateKey] = now;

            var expiresAt = payload.Duration is { } d && d > TimeSpan.Zero
                ? now.Add(d)
                : (payload.Kind is LiveActivityKind.VolumeHud or LiveActivityKind.BrightnessHud or LiveActivityKind.MicMuteHud or LiveActivityKind.CapsLockHud
                    ? now.AddMilliseconds(1800)
                    : now.AddSeconds(4));

            var newEntry = new LiveActivityEntry(payload, now, expiresAt);

            if (!string.IsNullOrEmpty(payload.CoalesceKey))
            {
                var idx = _entries.FindIndex(e => string.Equals(e.Payload.CoalesceKey, payload.CoalesceKey, StringComparison.Ordinal));
                if (idx >= 0)
                {
                    _entries[idx] = newEntry;
                }
                else
                {
                    _entries.Add(newEntry);
                }
            }
            else
            {
                var existingIdx = _entries.FindIndex(e => string.Equals(e.Payload.Id, payload.Id, StringComparison.Ordinal));
                if (existingIdx >= 0)
                {
                    _entries[existingIdx] = newEntry;
                }
                else
                {
                    _entries.Add(newEntry);
                }
            }

            // Keep bounded queue so long-running sessions never leak memory
            if (_entries.Count > 24)
            {
                _entries.Sort((a, b) => ComparePriority(b, a));
                _entries.RemoveRange(24, _entries.Count - 24);
            }
        }

        RaiseChanged();
        return true;
    }

    public bool Dismiss(string idOrCoalesceKey)
    {
        bool removed;
        lock (_sync)
        {
            removed = _entries.RemoveAll(e =>
                string.Equals(e.Payload.Id, idOrCoalesceKey, StringComparison.Ordinal) ||
                string.Equals(e.Payload.CoalesceKey, idOrCoalesceKey, StringComparison.Ordinal)) > 0;
        }

        if (removed)
        {
            RaiseChanged();
        }
        return removed;
    }

    public void Tick()
    {
        bool removed;
        var now = _clock.UtcNow;
        lock (_sync)
        {
            removed = PruneExpiredInternal(now);
            // Clean stale rate-limit keys older than 1 minute
            if (_lastPublishBySource.Count > 64)
            {
                var cutoff = now.AddMinutes(-1);
                var stale = _lastPublishBySource.Where(kv => kv.Value < cutoff).Select(kv => kv.Key).ToList();
                foreach (var k in stale)
                {
                    _lastPublishBySource.Remove(k);
                }
            }
        }

        if (removed)
        {
            RaiseChanged();
        }
    }

    public LiveActivityStackSnapshot GetSnapshot()
    {
        lock (_sync)
        {
            PruneExpiredInternal(_clock.UtcNow);
            if (_entries.Count == 0)
            {
                return new LiveActivityStackSnapshot(null, null, 0);
            }

            var ordered = _entries.OrderByDescending(e => (int)e.Payload.Priority)
                                  .ThenByDescending(e => e.CreatedAt)
                                  .ToList();

            var primary = ordered[0];
            var secondary = ordered.Count > 1 && ordered[1].Payload.SourceModuleId != primary.Payload.SourceModuleId
                ? ordered[1]
                : (ordered.Count > 1 ? ordered[1] : null);

            return new LiveActivityStackSnapshot(primary, secondary, ordered.Count);
        }
    }

    private bool PruneExpiredInternal(DateTimeOffset now)
    {
        return _entries.RemoveAll(e => e.ExpiresAt is { } exp && now >= exp) > 0;
    }

    private static int ComparePriority(LiveActivityEntry a, LiveActivityEntry b)
    {
        var p = ((int)a.Payload.Priority).CompareTo((int)b.Payload.Priority);
        return p != 0 ? p : a.CreatedAt.CompareTo(b.CreatedAt);
    }

    private void RaiseChanged()
    {
        StackUpdated?.Invoke(GetSnapshot());
    }
}
