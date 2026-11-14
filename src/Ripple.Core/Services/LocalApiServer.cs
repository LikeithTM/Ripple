using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Ripple.Core.Events;

namespace Ripple.Core.Services;

public sealed record ApiPushRequest(
    string? Title,
    string? Subtitle,
    string? Icon,
    string? AccentHex,
    int? DurationSeconds,
    double? Progress,
    string? Priority,
    string? CoalesceKey);

public sealed record ApiTimerRequest(int Minutes, string? Label);

public sealed record ApiAgentEventRequest(
    string AgentName,
    string Status,
    string? Title,
    string? Detail,
    string? ActionId,
    int? TokensUsed);

/// <summary>
/// Loopback-only (127.0.0.1) REST + WebSocket server enabling CLI, AI-agent hooks,
/// Stream Deck, and AutoHotkey integrations with bearer-token authentication.
/// </summary>
public sealed class LocalApiServer : IAsyncDisposable
{
    private readonly LiveActivityEventBus _bus;
    private readonly Func<string> _tokenProvider;
    private readonly Action<int, string?>? _onStartTimer;
    private readonly Action<ApiAgentEventRequest>? _onAgentEvent;
    private readonly Action<string>? _onProfileSwitch;
    private readonly Func<object>? _onStatusQuery;

    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _listenTask;
    private readonly List<WebSocket> _sockets = [];
    private readonly object _socketLock = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public LocalApiServer(
        LiveActivityEventBus bus,
        Func<string> tokenProvider,
        Action<int, string?>? onStartTimer = null,
        Action<ApiAgentEventRequest>? onAgentEvent = null,
        Action<string>? onProfileSwitch = null,
        Func<object>? onStatusQuery = null)
    {
        _bus = bus;
        _tokenProvider = tokenProvider;
        _onStartTimer = onStartTimer;
        _onAgentEvent = onAgentEvent;
        _onProfileSwitch = onProfileSwitch;
        _onStatusQuery = onStatusQuery;
    }

    public int Port { get; private set; }
    public bool IsRunning => _listener is { IsListening: true };
    public string? LastError { get; private set; }

    public bool Start(int port = 49152)
    {
        if (IsRunning)
        {
            return true;
        }

        try
        {
            Port = port;
            _cts = new CancellationTokenSource();
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            _listenTask = Task.Run(() => AcceptLoopAsync(_cts.Token));
            LastError = null;
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return false;
        }
    }

    public async Task BroadcastEventAsync(object payload)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
        List<WebSocket> snapshot;
        lock (_socketLock)
        {
            snapshot = [.. _sockets];
        }

        foreach (var ws in snapshot)
        {
            if (ws.State == WebSocketState.Open)
            {
                try
                {
                    await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
                }
                catch
                {
                    lock (_socketLock) { _sockets.Remove(ws); }
                }
            }
        }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener is { IsListening: true })
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync();
            }
            catch
            {
                break;
            }

            _ = Task.Run(() => HandleContextAsync(ctx, ct), ct);
        }
    }

    private async Task HandleContextAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        try
        {
            var req = ctx.Request;
            var res = ctx.Response;
            var path = req.Url?.AbsolutePath?.TrimEnd('/').ToLowerInvariant() ?? "/";

            // Require bearer token on all mutating /api/ routes (allow unauthenticated /health)
            if (path != "/health" && !IsAuthorized(req))
            {
                res.StatusCode = 401;
                await WriteJsonAsync(res, new { error = "Unauthorized. Pass X-Ripple-Token or Authorization: Bearer <token>." });
                return;
            }

            if (req.IsWebSocketRequest && path == "/ws")
            {
                var wsCtx = await ctx.AcceptWebSocketAsync(null);
                lock (_socketLock) { _sockets.Add(wsCtx.WebSocket); }
                await KeepWebSocketAliveAsync(wsCtx.WebSocket, ct);
                return;
            }

            switch (path)
            {
                case "/health":
                case "/api/v1/status":
                    res.StatusCode = 200;
                    var statusObj = _onStatusQuery?.Invoke() ?? new { ok = true, port = Port, stack = _bus.GetSnapshot().TotalActiveCount };
                    await WriteJsonAsync(res, statusObj);
                    break;

                case "/api/v1/push":
                    if (req.HttpMethod != "POST")
                    {
                        res.StatusCode = 405;
                        await WriteJsonAsync(res, new { error = "Use POST" });
                        return;
                    }
                    var push = await ReadBodyAsync<ApiPushRequest>(req);
                    if (push is null || string.IsNullOrWhiteSpace(push.Title))
                    {
                        res.StatusCode = 400;
                        await WriteJsonAsync(res, new { error = "Title is required" });
                        return;
                    }

                    var prio = Enum.TryParse<LiveActivityPriority>(push.Priority, true, out var parsedPrio)
                        ? parsedPrio
                        : LiveActivityPriority.Normal;

                    var activity = new LiveActivityPayload(
                        Id: $"api:{Guid.NewGuid():N}",
                        SourceModuleId: "api",
                        Kind: LiveActivityKind.Generic,
                        Title: push.Title,
                        Subtitle: push.Subtitle,
                        Icon: string.IsNullOrWhiteSpace(push.Icon) ? "✦" : push.Icon,
                        AccentHex: string.IsNullOrWhiteSpace(push.AccentHex) ? "#6C8CFF" : push.AccentHex,
                        Priority: prio,
                        Duration: TimeSpan.FromSeconds(Math.Clamp(push.DurationSeconds ?? 4, 1, 60)),
                        Progress: push.Progress,
                        CoalesceKey: push.CoalesceKey);

                    _bus.Publish(activity);
                    await BroadcastEventAsync(new { type = "push", activity });
                    res.StatusCode = 200;
                    await WriteJsonAsync(res, new { ok = true, id = activity.Id });
                    break;

                case "/api/v1/timer":
                    var timerReq = await ReadBodyAsync<ApiTimerRequest>(req);
                    if (timerReq is null || timerReq.Minutes <= 0)
                    {
                        res.StatusCode = 400;
                        await WriteJsonAsync(res, new { error = "Minutes > 0 required" });
                        return;
                    }
                    _onStartTimer?.Invoke(timerReq.Minutes, timerReq.Label);
                    res.StatusCode = 200;
                    await WriteJsonAsync(res, new { ok = true, minutes = timerReq.Minutes });
                    break;

                case "/api/v1/agent/event":
                    var agentReq = await ReadBodyAsync<ApiAgentEventRequest>(req);
                    if (agentReq is null || string.IsNullOrWhiteSpace(agentReq.AgentName))
                    {
                        res.StatusCode = 400;
                        await WriteJsonAsync(res, new { error = "AgentName required" });
                        return;
                    }
                    _onAgentEvent?.Invoke(agentReq);
                    res.StatusCode = 200;
                    await WriteJsonAsync(res, new { ok = true });
                    break;

                case "/api/v1/profile":
                    var body = await ReadBodyAsync<Dictionary<string, string>>(req);
                    if (body is not null && body.TryGetValue("name", out var profileName))
                    {
                        _onProfileSwitch?.Invoke(profileName);
                        res.StatusCode = 200;
                        await WriteJsonAsync(res, new { ok = true, profile = profileName });
                    }
                    else
                    {
                        res.StatusCode = 400;
                        await WriteJsonAsync(res, new { error = "name required" });
                    }
                    break;

                default:
                    res.StatusCode = 404;
                    await WriteJsonAsync(res, new { error = "Not found" });
                    break;
            }
        }
        catch
        {
            try { ctx.Response.StatusCode = 500; ctx.Response.Close(); } catch { }
        }
    }

    private bool IsAuthorized(HttpListenerRequest req)
    {
        var expected = _tokenProvider();
        if (string.IsNullOrEmpty(expected))
        {
            return true;
        }

        var headerToken = req.Headers["X-Ripple-Token"];
        if (string.Equals(headerToken, expected, StringComparison.Ordinal))
        {
            return true;
        }

        var auth = req.Headers["Authorization"];
        if (!string.IsNullOrEmpty(auth) && auth.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return string.Equals(auth[7..].Trim(), expected, StringComparison.Ordinal);
        }

        return false;
    }

    private static async Task<T?> ReadBodyAsync<T>(HttpListenerRequest req)
    {
        using var reader = new StreamReader(req.InputStream, req.ContentEncoding ?? Encoding.UTF8);
        var json = await reader.ReadToEndAsync();
        return string.IsNullOrWhiteSpace(json) ? default : JsonSerializer.Deserialize<T>(json, JsonOpts);
    }

    private static async Task WriteJsonAsync(HttpListenerResponse res, object data)
    {
        res.ContentType = "application/json; charset=utf-8";
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(data));
        res.ContentLength64 = bytes.Length;
        await res.OutputStream.WriteAsync(bytes);
        res.Close();
    }

    private async Task KeepWebSocketAliveAsync(WebSocket ws, CancellationToken ct)
    {
        var buf = new byte[1024];
        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var res = await ws.ReceiveAsync(buf, ct);
                if (res.MessageType == WebSocketMessageType.Close)
                {
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closed", ct);
                }
            }
        }
        catch
        {
            // Ignore closed socket
        }
        finally
        {
            lock (_socketLock) { _sockets.Remove(ws); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            _cts?.Cancel();
            _listener?.Stop();
            _listener?.Close();
            if (_listenTask is not null)
            {
                await _listenTask.ConfigureAwait(false);
            }
        }
        catch
        {
            // Ignore shutdown races
        }
        finally
        {
            _cts?.Dispose();
        }
    }
}
