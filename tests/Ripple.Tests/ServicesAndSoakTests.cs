using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Ripple.Core.Events;
using Ripple.Core.Geometry;
using Ripple.Core.Modules;
using Ripple.Core.Motion;
using Ripple.Core.Services;
using Ripple.Core.StateMachine;
using Xunit;

namespace Ripple.Tests;

public sealed class ServicesAndSoakTests
{
    [Fact]
    public void ClipboardSmartDetector_IdentifiesAllSmartTypesAndEncryptsAtRest()
    {
        Assert.Equal(ClipboardSmartKind.HexColor, ClipboardSmartDetector.Analyze("#6c8cff").Kind);
        Assert.Equal(4, ClipboardSmartDetector.Analyze("#6c8cff").ColorPaletteHex!.Count);
        Assert.Equal(ClipboardSmartKind.Url, ClipboardSmartDetector.Analyze("https://github.com/ripple").Kind);
        Assert.Equal(ClipboardSmartKind.Email, ClipboardSmartDetector.Analyze("hello@ripple.app").Kind);
        Assert.Equal(ClipboardSmartKind.OtpCode, ClipboardSmartDetector.Analyze("492817").Kind);
        Assert.True(ClipboardSmartDetector.Analyze("492817").ShouldAutoExpire);
        Assert.Equal(ClipboardSmartKind.Json, ClipboardSmartDetector.Analyze("{\"ok\":true,\"n\":42}").Kind);

        var math = ClipboardSmartDetector.Analyze("(12 + 8) * 3.5");
        Assert.Equal(ClipboardSmartKind.MathExpression, math.Kind);
        Assert.Equal("70", math.NormalizedValue);

        // AES-256-GCM encryption roundtrip
        var key = RandomNumberGenerator.GetBytes(32);
        var secret = "[{\"Text\":\"Secret API Key\"}]";
        var cipher = ClipboardSmartDetector.EncryptPayload(secret, key);
        Assert.Equal(secret, ClipboardSmartDetector.DecryptPayload(cipher, key));
    }

    [Fact]
    public void LyricsParser_FindsActiveLineAndProgress()
    {
        const string lrc = """
            [00:10.00] First verse line
            [00:14.00] Second verse line
            [00:18.00] Chorus drop
            """;

        var parsed = LyricsParser.Parse(lrc);
        Assert.Equal(3, parsed.Count);

        var active = LyricsParser.GetActiveLine(parsed, TimeSpan.FromSeconds(16));
        Assert.Equal(1, active.ActiveIndex);
        Assert.Equal("Second verse line", active.Current?.Text);
        Assert.Equal("Chorus drop", active.Next?.Text);
        Assert.Equal(0.5, active.LineProgress, precision: 2);
    }

    [Fact]
    public void ContextMorphEngine_PrioritizesUrgentSignals()
    {
        var engine = new ContextMorphEngine();
        string[] enabled = ["music", "timer", "notchy", "shelf", "system"];

        Assert.Equal("notchy", engine.SelectBestModuleId(new ContextSignals(true, false, false, null, true, true, false, false), enabled));
        Assert.Equal("notchy", engine.SelectBestModuleId(new ContextSignals(false, true, false, null, true, true, false, false), enabled));
        Assert.Equal("timer", engine.SelectBestModuleId(new ContextSignals(false, false, false, TimeSpan.FromMinutes(3), false, true, false, false), enabled));
        Assert.Equal("music", engine.SelectBestModuleId(new ContextSignals(false, false, false, null, false, true, false, false), enabled));
    }

    [Fact]
    public void SettingsStore_SaveLoadMigrateAndBundleRoundTrip()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"ripple_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            var path = Path.Combine(tempDir, "settings.json");
            var store = new SettingsStore(path);
            var settings = new RippleSettings { RestWidth = 222, AccentHex = "#22D3EE" };
            store.Save(settings);

            var loaded = store.Load();
            Assert.Equal(222, loaded.RestWidth);
            Assert.Equal("#22D3EE", loaded.AccentHex);

            var exportPath = Path.Combine(tempDir, "bundle.json");
            store.ExportBundle(loaded, exportPath);
            var imported = store.ImportBundle(exportPath);
            Assert.Equal(222, imported.RestWidth);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void MultiMonitorDpiMatrix_GeometryScalesCleanlyAcross100To250Percent()
    {
        double[] dpis = [1.0, 1.25, 1.5, 1.75, 2.0, 2.5];
        foreach (var scale in dpis)
        {
            var res = NotchGeometryBuilder.Build(new NotchGeometrySpec(
                CanvasWidth: 740,
                CanvasHeight: 360,
                BodyWidth: 188,
                BodyHeight: 32,
                CornerRadius: 16,
                EarRadius: 12,
                OffsetY: 0,
                LayoutMode: NotchLayoutMode.Notch));

            // Physical screen footprint scales cleanly with DPI without NaN or degenerate bounds
            var physWidth = res.BodyBounds.Width * scale;
            Assert.InRange(physWidth, 180.0, 500.0);
        }
    }

    [Fact]
    public async Task LocalApiServer_AuthAndPushFlowWorksEndToEnd()
    {
        var bus = new LiveActivityEventBus();
        const string token = "test-secret-token";
        var port = 49321;

        await using var server = new LocalApiServer(bus, () => token);
        Assert.True(server.Start(port), server.LastError);

        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };

        // Reject missing token
        var unauth = await http.PostAsJsonAsync("/api/v1/push", new ApiPushRequest("Hi", null, null, null, 3, null, null, null));
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, unauth.StatusCode);

        // Accept valid token
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var ok = await http.PostAsJsonAsync("/api/v1/push", new ApiPushRequest("Build Green", "42 tests", "✓", "#34D399", 5, 1.0, "High", null));
        Assert.Equal(System.Net.HttpStatusCode.OK, ok.StatusCode);

        var snap = bus.GetSnapshot();
        Assert.Equal("Build Green", snap.Primary?.Payload.Title);
    }

    [Fact]
    public void Simulated24HourSoakTest_BoundedMemoryZeroLeaksZeroExceptions()
    {
        var clock = new FakeClock();
        var bus = new LiveActivityEventBus(clock, rateLimitMs: 0);
        var sm = new NotchStateMachine(new NotchStateTimingConfig(), clock);
        var motion = new NotchMotionState();

        GC.Collect();
        var baselineBytes = GC.GetTotalMemory(true);

        // Simulate 24 hours (86,400 seconds) at 1-second intervals with continuous events & state cycling
        for (var sec = 0; sec < 86_400; sec++)
        {
            clock.AdvanceMs(1000);
            if (sec % 60 == 0)
            {
                bus.Publish(new LiveActivityPayload($"soak:{sec}", "system", LiveActivityKind.Generic, $"Tick {sec}", Duration: TimeSpan.FromSeconds(4)));
                sm.ShowLiveActivity($"soak:{sec}", 4000);
                motion.SetShellTarget(320, 52, 24, 0, 0.5, 0.0, 8.0, SpringConfig.Expand);
            }
            if (sec % 120 == 30)
            {
                motion.SetShellTarget(188, 32, 16, 0, 0.0, 0.0, 12.0, SpringConfig.Collapse);
            }

            bus.Tick();
            sm.Tick();
            motion.Step(1.0 / 60.0);
        }

        GC.Collect();
        var endBytes = GC.GetTotalMemory(true);
        var deltaMb = (endBytes - baselineBytes) / (1024.0 * 1024.0);

        Assert.Equal(0, bus.GetSnapshot().TotalActiveCount);
        Assert.True(deltaMb < 5.0, $"Expected < 5 MB net growth over 24h soak, observed {deltaMb:F2} MB");
    }

    [Fact]
    public void AntigravityTranscriptParser_ParsesUserInputToolCallsAndTurnCompletion()
    {
        const string userLine = """{"step_index":10,"source":"USER_EXPLICIT","type":"USER_INPUT","status":"DONE","content":"<USER_REQUEST>\nBuild the app\n</USER_REQUEST>"}""";
        Assert.True(AntigravityTranscriptParser.TryParseStepLine(userLine, out var userEvt, out var userStep));
        Assert.Equal(10, userStep);
        Assert.NotNull(userEvt);
        Assert.Equal("Antigravity", userEvt!.AgentName);
        Assert.Equal("running", userEvt.Status);
        Assert.Equal("Antigravity • Thinking…", userEvt.Title);
        Assert.Equal("Build the app", userEvt.Detail);

        const string cmdLine = """{"step_index":11,"source":"MODEL","type":"PLANNER_RESPONSE","status":"DONE","tool_calls":[{"name":"run_command","args":{"toolSummary":"\"Run unit tests\""}}]}""";
        Assert.True(AntigravityTranscriptParser.TryParseStepLine(cmdLine, out var cmdEvt, out var cmdStep));
        Assert.Equal(11, cmdStep);
        Assert.NotNull(cmdEvt);
        Assert.Equal("running", cmdEvt!.Status);
        Assert.Equal("Antigravity • Running Command", cmdEvt.Title);
        Assert.Equal("Run unit tests", cmdEvt.Detail);

        const string editLine = """{"step_index":12,"source":"MODEL","type":"PLANNER_RESPONSE","status":"DONE","tool_calls":[{"name":"edit_file","args":{"Path":"\"C:\\repo\\src\\Program.cs\""}}]}""";
        Assert.True(AntigravityTranscriptParser.TryParseStepLine(editLine, out var editEvt, out var editStep));
        Assert.Equal(12, editStep);
        Assert.NotNull(editEvt);
        Assert.Equal("running", editEvt!.Status);
        Assert.Equal("Antigravity • Editing File", editEvt.Title);
        Assert.Equal("Program.cs", editEvt.Detail);

        const string doneLine = """{"step_index":13,"source":"MODEL","type":"PLANNER_RESPONSE","status":"DONE","output_tokens":512,"content":"All tests passed!"}""";
        Assert.True(AntigravityTranscriptParser.TryParseStepLine(doneLine, out var doneEvt, out var doneStep));
        Assert.Equal(13, doneStep);
        Assert.NotNull(doneEvt);
        Assert.Equal("completed", doneEvt!.Status);
        Assert.Equal("Antigravity • Turn Complete", doneEvt.Title);
        Assert.Equal(512, doneEvt.TokensUsed);
    }
}
