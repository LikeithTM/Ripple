using System.Diagnostics;
using System.IO;
using System.Text;
using Ripple.Core.Events;
using Ripple.Core.Modules;
using Ripple.Core.Services;

namespace Ripple.App.Services;

public enum AgentRunStatus
{
    Idle = 0,
    Running = 1,
    PermissionNeeded = 2,
    Completed = 3,
    Error = 4
}

public sealed record AgentSessionRecord(
    string AgentName,
    AgentRunStatus Status,
    string Headline,
    string Detail,
    DateTimeOffset UpdatedAt,
    string? PendingActionId = null,
    int TokensUsed = 0);

/// <summary>
/// "Notchy" — Tracks Antigravity live transcript logs automatically and supports Claude Code,
/// Codex CLI, Gemini CLI, and Copilot agent hooks, surfacing interactive Approve/Deny permission
/// pulses directly inside the notch alongside configured Git repository branch/status.
/// </summary>
public sealed class AgentAndDevService : IRippleModule, IDisposable
{
    private readonly LiveActivityEventBus _bus;
    private readonly RippleSettings _settings;
    private readonly List<AgentSessionRecord> _sessions = [];
    private readonly object _sessionSync = new();
    private readonly object _agySync = new();
    private readonly string _antigravityBrainDir;

    private static readonly byte[] _completionChimeWav = BuildCompletionChimeWav();

    private volatile IReadOnlyList<AgentSessionRecord> _sessionsSnapshot = [];
    private FileSystemWatcher? _agyWatcher;
    private string? _activeTranscriptPath;
    private long _activeTranscriptOffset;
    private int _lastSeenStepIndex = -1;
    private DateTimeOffset _lastAgyPollAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastDirScanAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastGitPollAt = DateTimeOffset.MinValue;

    public AgentAndDevService(LiveActivityEventBus bus, RippleSettings settings)
    {
        _bus = bus;
        _settings = settings;
        _sessions.Add(new AgentSessionRecord(
            "Antigravity",
            AgentRunStatus.Idle,
            "Ready",
            "Watching live workspace activity",
            DateTimeOffset.Now));
        _sessionsSnapshot = _sessions.ToArray();

        _antigravityBrainDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".gemini",
            "antigravity",
            "brain");
        InitAntigravityWatcher();
    }

    public string Id => "notchy";
    public string Name => "Notchy";
    public string Icon => "🤖";
    public int Priority => 100;
    public bool IsEnabled { get; set; } = true;
    public IReadOnlyCollection<ModulePermission> RequiredPermissions { get; } = [ModulePermission.LocalNetworkApi];
    public double PreferredExpandedWidth => 468;
    public double PreferredExpandedHeight => 222;

    public IReadOnlyList<AgentSessionRecord> Sessions => _sessionsSnapshot;
    public string GitRepoSummary { get; private set; } = "";
    public string LiveWatcherStatus { get; private set; } = "Standby";
    public bool IsAnyAgentWaitingApproval => _sessionsSnapshot.Any(s => s.Status == AgentRunStatus.PermissionNeeded);
    public bool IsAnyAgentRunning => _sessionsSnapshot.Any(s => s.Status == AgentRunStatus.Running);

    public event Action? Updated;
    public event Action<string, bool>? AgentDecisionMade;

    public static string CleanHeadline(AgentSessionRecord s)
    {
        var h = s.Headline?.Trim() ?? "";
        var bulletPrefix = $"{s.AgentName} • ";
        if (h.StartsWith(bulletPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return h[bulletPrefix.Length..].Trim();
        }

        var colonPrefix = $"{s.AgentName}: ";
        if (h.StartsWith(colonPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return h[colonPrefix.Length..].Trim();
        }

        return h;
    }

    public static string FormatCompactStatus(AgentSessionRecord s)
    {
        var head = CleanHeadline(s);
        return s.Status switch
        {
            AgentRunStatus.PermissionNeeded => head,
            AgentRunStatus.Completed => "Turn Complete ✓",
            AgentRunStatus.Error => string.IsNullOrWhiteSpace(head) ? "Step Error" : head,
            AgentRunStatus.Running => head switch
            {
                "Editing File" when !string.IsNullOrWhiteSpace(s.Detail) => $"Editing {s.Detail}",
                "Reading File" when !string.IsNullOrWhiteSpace(s.Detail) => $"Reading {s.Detail}",
                "Running Command" when !string.IsNullOrWhiteSpace(s.Detail) => $"Running: {s.Detail}",
                _ => string.IsNullOrWhiteSpace(head) ? "Working…" : head
            },
            _ => head
        };
    }

    public ModuleGlanceState GetGlanceState()
    {
        var snapshot = _sessionsSnapshot;
        var waiting = snapshot.FirstOrDefault(s => s.Status == AgentRunStatus.PermissionNeeded);
        if (waiting is not null)
        {
            return new ModuleGlanceState(true, waiting.AgentName, "⚠", "#F59E0B", Pulse: true, StatusText: FormatCompactStatus(waiting));
        }

        var running = snapshot.FirstOrDefault(s => s.Status == AgentRunStatus.Running);
        if (running is not null)
        {
            return new ModuleGlanceState(true, running.AgentName, "✦", "#22D3EE", Pulse: true, StatusText: FormatCompactStatus(running));
        }

        var completed = snapshot.FirstOrDefault(s =>
            s.Status == AgentRunStatus.Completed &&
            (DateTimeOffset.Now - s.UpdatedAt).TotalSeconds <= 6.0);
        if (completed is not null)
        {
            return new ModuleGlanceState(true, completed.AgentName, "✓", "#34D399", Pulse: false, StatusText: "Turn Complete ✓");
        }

        return new ModuleGlanceState(false);
    }

    public void OnTick(DateTimeOffset utcNow, bool isExpanded)
    {
        if ((utcNow - _lastAgyPollAt).TotalSeconds >= 0.5)
        {
            _lastAgyPollAt = utcNow;
            PollActiveTranscript(utcNow);
        }

        if (!string.IsNullOrWhiteSpace(_settings.GitWatchRepoPath) &&
            (utcNow - _lastGitPollAt).TotalSeconds >= (isExpanded ? 5 : 20))
        {
            _lastGitPollAt = utcNow;
            RefreshGitStatus();
        }
    }

    public void HandleAgentEvent(ApiAgentEventRequest req, bool publishLiveActivity = true)
    {
        var status = req.Status.Trim().ToLowerInvariant() switch
        {
            "running" or "working" => AgentRunStatus.Running,
            "permission" or "approval" or "permission_needed" => AgentRunStatus.PermissionNeeded,
            "completed" or "done" or "finished" => AgentRunStatus.Completed,
            "error" or "failed" => AgentRunStatus.Error,
            _ => AgentRunStatus.Idle
        };

        var record = new AgentSessionRecord(
            req.AgentName,
            status,
            req.Title ?? $"{req.AgentName}: {status}",
            req.Detail ?? "",
            DateTimeOffset.Now,
            req.ActionId ?? (status == AgentRunStatus.PermissionNeeded ? Guid.NewGuid().ToString("N")[..8] : null),
            req.TokensUsed ?? 0);

        AgentRunStatus? previousStatus;
        lock (_sessionSync)
        {
            previousStatus = _sessions
                .FirstOrDefault(s => string.Equals(s.AgentName, req.AgentName, StringComparison.OrdinalIgnoreCase))
                ?.Status;

            _sessions.RemoveAll(s =>
                string.Equals(s.AgentName, req.AgentName, StringComparison.OrdinalIgnoreCase) ||
                s.Status == AgentRunStatus.Idle);
            _sessions.Insert(0, record);
            _sessionsSnapshot = _sessions.ToArray();
        }

        if (publishLiveActivity)
        {
            if (status == AgentRunStatus.Completed && previousStatus != AgentRunStatus.Completed)
            {
                PlayCompletionChime();
            }

            if (status is AgentRunStatus.PermissionNeeded or AgentRunStatus.Completed or AgentRunStatus.Error)
            {
                var (accent, prio, ttl) = status switch
                {
                    AgentRunStatus.PermissionNeeded => ("#F59E0B", LiveActivityPriority.Critical, 15),
                    AgentRunStatus.Completed => ("#34D399", LiveActivityPriority.High, 5),
                    _ => ("#EF4444", LiveActivityPriority.High, 6)
                };

                _bus.Publish(new LiveActivityPayload(
                    Id: $"agent:{req.AgentName}",
                    SourceModuleId: Id,
                    Kind: LiveActivityKind.AgentEvent,
                    Title: CleanHeadline(record),
                    Subtitle: string.IsNullOrEmpty(record.Detail) ? req.AgentName : $"{req.AgentName} · {record.Detail}",
                    Icon: status == AgentRunStatus.PermissionNeeded ? "⚠" : (status == AgentRunStatus.Completed ? "✓" : "🤖"),
                    AccentHex: accent,
                    Priority: prio,
                    Duration: TimeSpan.FromSeconds(ttl),
                    RequiresAction: status == AgentRunStatus.PermissionNeeded,
                    PrimaryActionLabel: status == AgentRunStatus.PermissionNeeded ? "Approve" : null,
                    SecondaryActionLabel: status == AgentRunStatus.PermissionNeeded ? "Deny" : null,
                    CoalesceKey: $"agent-{req.AgentName}"));
            }
            else if (status == AgentRunStatus.Running)
            {
                _bus.Dismiss($"agent-{req.AgentName}");
            }
        }

        Updated?.Invoke();
    }

    public void ResolveApproval(string agentName, bool approved)
    {
        AgentSessionRecord? cur = null;
        string? pendingId = null;

        lock (_sessionSync)
        {
            var idx = _sessions.FindIndex(s => string.Equals(s.AgentName, agentName, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0)
            {
                cur = _sessions[idx];
                pendingId = cur.PendingActionId;
                _sessions[idx] = cur with
                {
                    Status = approved ? AgentRunStatus.Running : AgentRunStatus.Idle,
                    Headline = approved ? $"Approved {cur.AgentName}" : $"Denied {cur.AgentName}",
                    PendingActionId = null,
                    UpdatedAt = DateTimeOffset.Now
                };
                _sessionsSnapshot = _sessions.ToArray();
            }
        }

        if (cur is not null)
        {
            var decisionFile = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Ripple",
                "agent_decisions",
                $"{pendingId ?? "latest"}.json");
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(decisionFile)!);
                File.WriteAllText(decisionFile, $"{{\"approved\":{(approved ? "true" : "false")},\"agent\":\"{cur.AgentName}\",\"at\":\"{DateTimeOffset.UtcNow:O}\"}}");
            }
            catch { }

            _bus.Dismiss($"agent-{cur.AgentName}");
            AgentDecisionMade?.Invoke(pendingId ?? cur.AgentName, approved);
            Updated?.Invoke();
        }
    }

    public string InstallClaudeHookScript()
    {
        var hookDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ripple", "hooks");
        Directory.CreateDirectory(hookDir);
        var scriptPath = Path.Combine(hookDir, "ripple-agent-hook.ps1");
        var script = $$"""
param([string]$Agent = "Claude Code", [string]$Status = "completed", [string]$Title = "Task Update", [string]$Detail = "")
$body = @{ agentName = $Agent; status = $Status; title = $Title; detail = $Detail } | ConvertTo-Json
Invoke-RestMethod -Uri "http://127.0.0.1:{{_settings.LocalApiPort}}/api/v1/agent/event" `
  -Method Post -Headers @{ "X-Ripple-Token" = "{{_settings.LocalApiToken}}" } `
  -ContentType "application/json" -Body $body | Out-Null
""";
        File.WriteAllText(scriptPath, script);
        return scriptPath;
    }

    private void RefreshGitStatus()
    {
        try
        {
            var path = _settings.GitWatchRepoPath;
            if (!Directory.Exists(path)) return;
            var psi = new ProcessStartInfo("git", $"-C \"{path}\" status -sb")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = Process.Start(psi);
            if (proc is null) return;
            var output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(1500);
            var lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            var branch = lines.FirstOrDefault()?.Replace("## ", "") ?? "git";
            var dirty = Math.Max(0, lines.Length - 1);
            GitRepoSummary = dirty == 0 ? $"{branch} (clean)" : $"{branch} • {dirty} changed";
            Updated?.Invoke();
        }
        catch { }
    }

    private void InitAntigravityWatcher()
    {
        lock (_agySync)
        {
            if (_agyWatcher is not null) return;
            if (!Directory.Exists(_antigravityBrainDir))
            {
                LiveWatcherStatus = "Standby";
                return;
            }

            try
            {
                var latest = FindLatestTranscriptFile();
                if (latest is not null)
                {
                    SeedSessionFromTailLocked(latest);
                }

                _agyWatcher = new FileSystemWatcher(_antigravityBrainDir, "transcript.jsonl")
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                    EnableRaisingEvents = true
                };
                _agyWatcher.Changed += OnTranscriptFsEvent;
                _agyWatcher.Created += OnTranscriptFsEvent;
                LiveWatcherStatus = "Live";
            }
            catch
            {
                LiveWatcherStatus = "Standby";
            }
        }
    }

    private string? FindLatestTranscriptFile()
    {
        try
        {
            if (!Directory.Exists(_antigravityBrainDir)) return null;
            return Directory
                .EnumerateFiles(_antigravityBrainDir, "transcript.jsonl", SearchOption.AllDirectories)
                .Select(p => new FileInfo(p))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault()?.FullName;
        }
        catch
        {
            return null;
        }
    }

    private void SeedSessionFromTailLocked(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) return;

            _activeTranscriptPath = path;
            var startOffset = Math.Max(0L, fi.Length - 32_768);
            _activeTranscriptOffset = startOffset;

            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            fs.Seek(startOffset, SeekOrigin.Begin);
            using var sr = new StreamReader(fs, Encoding.UTF8);
            if (startOffset > 0)
            {
                // Skip partial UTF-8 line at window boundary
                sr.ReadLine();
            }

            ApiAgentEventRequest? lastEvt = null;
            var lastStep = -1;
            string? line;
            while ((line = sr.ReadLine()) is not null)
            {
                if (AntigravityTranscriptParser.TryParseStepLine(line, out var evt, out var stepIdx) && evt is not null)
                {
                    lastEvt = evt;
                    if (stepIdx > lastStep) lastStep = stepIdx;
                }
            }

            _activeTranscriptOffset = fs.Length;
            _lastSeenStepIndex = lastStep;
            if (lastEvt is not null)
            {
                HandleAgentEvent(lastEvt, publishLiveActivity: false);
            }
        }
        catch { }
    }

    private void OnTranscriptFsEvent(object sender, FileSystemEventArgs e)
    {
        if (string.IsNullOrEmpty(e.FullPath)) return;

        List<ApiAgentEventRequest>? eventsToDispatch = null;
        lock (_agySync)
        {
            try
            {
                if (!File.Exists(e.FullPath)) return;
                if (!string.Equals(_activeTranscriptPath, e.FullPath, StringComparison.OrdinalIgnoreCase))
                {
                    var len = new FileInfo(e.FullPath).Length;
                    _activeTranscriptPath = e.FullPath;
                    _activeTranscriptOffset = e.ChangeType == WatcherChangeTypes.Created || len <= 8192
                        ? 0
                        : Math.Max(0, len - 8192);
                    _lastSeenStepIndex = -1;
                }

                eventsToDispatch = ReadAppendedLinesLocked(e.FullPath);
            }
            catch { }
        }

        if (eventsToDispatch is { Count: > 0 })
        {
            foreach (var evt in eventsToDispatch)
            {
                HandleAgentEvent(evt, publishLiveActivity: true);
            }
        }
    }

    private void PollActiveTranscript(DateTimeOffset utcNow)
    {
        if (_agyWatcher is null)
        {
            if ((utcNow - _lastDirScanAt).TotalSeconds >= 10)
            {
                _lastDirScanAt = utcNow;
                InitAntigravityWatcher();
            }
            return;
        }

        List<ApiAgentEventRequest>? eventsToDispatch = null;
        lock (_agySync)
        {
            try
            {
                if ((utcNow - _lastDirScanAt).TotalSeconds >= 10)
                {
                    _lastDirScanAt = utcNow;
                    var latest = FindLatestTranscriptFile();
                    if (latest is not null && !string.Equals(latest, _activeTranscriptPath, StringComparison.OrdinalIgnoreCase))
                    {
                        var len = new FileInfo(latest).Length;
                        _activeTranscriptPath = latest;
                        _activeTranscriptOffset = len <= 8192 ? 0 : Math.Max(0, len - 8192);
                        _lastSeenStepIndex = -1;
                    }
                }

                if (_activeTranscriptPath is not null && File.Exists(_activeTranscriptPath))
                {
                    var len = new FileInfo(_activeTranscriptPath).Length;
                    if (len != _activeTranscriptOffset)
                    {
                        eventsToDispatch = ReadAppendedLinesLocked(_activeTranscriptPath);
                    }
                }
            }
            catch { }
        }

        if (eventsToDispatch is { Count: > 0 })
        {
            foreach (var evt in eventsToDispatch)
            {
                HandleAgentEvent(evt, publishLiveActivity: true);
            }
        }
    }

    private List<ApiAgentEventRequest>? ReadAppendedLinesLocked(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (fs.Length < _activeTranscriptOffset)
        {
            _activeTranscriptOffset = 0;
            _lastSeenStepIndex = -1;
        }
        if (fs.Length == _activeTranscriptOffset) return null;

        fs.Seek(_activeTranscriptOffset, SeekOrigin.Begin);
        var bytesToRead = (int)Math.Min(fs.Length - _activeTranscriptOffset, 1024 * 1024);
        var buffer = new byte[bytesToRead];
        var totalRead = 0;
        while (totalRead < bytesToRead)
        {
            var n = fs.Read(buffer, totalRead, bytesToRead - totalRead);
            if (n == 0) break;
            totalRead += n;
        }
        if (totalRead == 0) return null;

        var lastNewline = Array.LastIndexOf(buffer, (byte)'\n', totalRead - 1);
        if (lastNewline < 0)
        {
            // Incomplete line still being flushed; retry on next event/poll.
            return null;
        }

        _activeTranscriptOffset += lastNewline + 1;
        var chunk = Encoding.UTF8.GetString(buffer, 0, lastNewline + 1);
        List<ApiAgentEventRequest>? parsedEvents = null;

        foreach (var line in chunk.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (AntigravityTranscriptParser.TryParseStepLine(line, out var evt, out var stepIdx) &&
                evt is not null &&
                stepIdx > _lastSeenStepIndex)
            {
                _lastSeenStepIndex = stepIdx;
                parsedEvents ??= [];
                parsedEvents.Add(evt);
            }
        }

        return parsedEvents;
    }

    public void Dispose()
    {
        lock (_agySync)
        {
            if (_agyWatcher is not null)
            {
                _agyWatcher.EnableRaisingEvents = false;
                _agyWatcher.Changed -= OnTranscriptFsEvent;
                _agyWatcher.Created -= OnTranscriptFsEvent;
                _agyWatcher.Dispose();
                _agyWatcher = null;
            }
        }
    }

    private static void PlayCompletionChime()
    {
        _ = Task.Run(() =>
        {
            try
            {
                using var ms = new MemoryStream(_completionChimeWav, writable: false);
                using var player = new System.Media.SoundPlayer(ms);
                player.PlaySync();
            }
            catch { }
        });
    }

    private static byte[] BuildCompletionChimeWav()
    {
        const int sampleRate = 44100;
        const double durationSec = 0.35;
        var sampleCount = (int)(sampleRate * durationSec);
        var dataBytes = sampleCount * 2;

        using var ms = new MemoryStream(44 + dataBytes);
        using (var bw = new BinaryWriter(ms, Encoding.ASCII, leaveOpen: true))
        {
            bw.Write(Encoding.ASCII.GetBytes("RIFF"));
            bw.Write(36 + dataBytes);
            bw.Write(Encoding.ASCII.GetBytes("WAVE"));
            bw.Write(Encoding.ASCII.GetBytes("fmt "));
            bw.Write(16);
            bw.Write((short)1); // PCM
            bw.Write((short)1); // Mono
            bw.Write(sampleRate);
            bw.Write(sampleRate * 2);
            bw.Write((short)2);
            bw.Write((short)16);
            bw.Write(Encoding.ASCII.GetBytes("data"));
            bw.Write(dataBytes);

            for (var i = 0; i < sampleCount; i++)
            {
                var t = (double)i / sampleRate;
                double s = 0.0;

                if (t < 0.24)
                {
                    var env1 = Math.Min(1.0, t / 0.006) * Math.Exp(-t * 18.0);
                    s += env1 * (Math.Sin(2.0 * Math.PI * 659.25 * t) * 0.72 + Math.Sin(2.0 * Math.PI * 1318.5 * t) * 0.18);
                }

                if (t >= 0.095)
                {
                    var dt = t - 0.095;
                    var env2 = Math.Min(1.0, dt / 0.006) * Math.Exp(-dt * 13.0);
                    s += env2 * (Math.Sin(2.0 * Math.PI * 987.77 * dt) * 0.78 + Math.Sin(2.0 * Math.PI * 1975.54 * dt) * 0.16);
                }

                var sample = (short)Math.Clamp(s * 0.25 * short.MaxValue, short.MinValue, short.MaxValue);
                bw.Write(sample);
            }
        }

        return ms.ToArray();
    }
}
