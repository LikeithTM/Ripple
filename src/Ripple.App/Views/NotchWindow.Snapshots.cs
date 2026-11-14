using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Ripple.Core.Events;
using Ripple.Core.Geometry;
using Ripple.Core.Services;
using Ripple.Core.StateMachine;

namespace Ripple.App.Views;

/// <summary>
/// Offscreen UI snapshot suite (<c>Ripple.exe --snapshot &lt;dir&gt;</c>). Drives the real window through every
/// state with a simulated clock and renders PNGs, so visual regressions can be reviewed without a screen capture.
/// </summary>
public partial class NotchWindow
{
    public async Task RunSnapshotSuiteAsync(string dir)
    {
        _snapshotMode = true;
        _idleTimer?.Stop();
        EnsureRenderLoop(false);
        CompositionTarget.Rendering -= OnCompositionRendering;
        Directory.CreateDirectory(dir);

        var now = DateTimeOffset.UtcNow;
        void Pump(int frames, double dt = 1.0 / 120.0)
        {
            for (var i = 0; i < frames; i++)
            {
                now = now.AddSeconds(dt);
                RenderFrame(dt, now, pollPointer: false);
            }
        }

        async Task SettleAsync() =>
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);

        async Task ShotAsync(string name)
        {
            await SettleAsync();
            RootCanvas.UpdateLayout();
            SaveVisual(RootCanvas, Path.Combine(dir, name + ".png"), backdrop: true);
        }

        void DrainEventBus()
        {
            while (_coord.EventBus.GetSnapshot().Primary is { } e)
            {
                _coord.EventBus.Dismiss(e.Payload.Id);
            }
            _coord.StateMachine.DismissLiveActivity();
        }

        var sm = _coord.StateMachine;
        _coord.Media.LoadDemoSnapshot();
        _coord.SelectModuleByUser("music");
        DrainEventBus();
        sm.Collapse();

        // --- Rest (music playing glance), Peek ---
        Pump(120);
        await ShotAsync("01_rest_music_glance");

        sm.UpdatePointer(false, true, new Point2D(280, 10));
        Pump(60);
        await ShotAsync("02_peek");
        sm.UpdatePointer(false, false, new Point2D(280, 200));

        // --- Expansion, sampled mid-flight to inspect spring + cascade + blur-to-sharp ---
        sm.HandleTrigger(NotchInteractionTrigger.Click);
        Pump(12);
        await ShotAsync("03_expanding_t100ms");
        Pump(18);
        await ShotAsync("04_expanding_t250ms");
        Pump(150);
        await ShotAsync("05_expanded_music");

        // --- Seed rich module state & capture every module pinned open ---
        sm.HandleTrigger(NotchInteractionTrigger.PinToggle);
        _coord.Shelf.AddDroppedPaths([Environment.ProcessPath ?? "Ripple.exe", Environment.CurrentDirectory]);
        _coord.FocusTimer.StartTimer(25, "Deep Work");
        _coord.FocusTimer.TogglePause();
        _coord.Notifications.PushNotification("Slack", "Maya", "Lunch at 1? I booked the corner table.", true);
        _coord.Notifications.PushNotification("GitHub", "CI passed", "ripple/main • 24 tests green");
        _coord.Notchy.HandleAgentEvent(new ApiAgentEventRequest(
            "Claude Code", "permission", "Run unit tests?", "dotnet test Ripple.sln", "snap1", 1240));
        var index = 6;
        foreach (var id in _coord.GetOrderedEnabledModules().Select(m => m.Id).ToList())
        {
            _coord.SelectModuleByUser(id);
            RebuildModuleTabRail();
            _contentDirty = true;
            _lastContentRefresh = DateTimeOffset.MinValue;
            Pump(150);
            await ShotAsync($"{index++:00}_module_{id}");
        }

        _coord.IsMotionTunerVisible = true;
        _contentDirty = true;
        _lastContentRefresh = DateTimeOffset.MinValue;
        Pump(30);
        await ShotAsync($"{index++:00}_motion_tuner");
        _coord.IsMotionTunerVisible = false;

        // --- Collapse, then Live Activities ---
        DrainEventBus();
        sm.HandleTrigger(NotchInteractionTrigger.PinToggle);
        sm.Collapse();
        _coord.SelectModuleByUser("music");
        Pump(150);
        await ShotAsync($"{index++:00}_collapsed_again");

        _coord.EventBus.Publish(new LiveActivityPayload(
            "snap:vol", "mixer", LiveActivityKind.VolumeHud, "Volume 72%", "Master Output", "\uD83D\uDD0A",
            "#6C8CFF", LiveActivityPriority.High, TimeSpan.FromSeconds(30), 0.72, "hud-volume"));
        Pump(150);
        await ShotAsync($"{index++:00}_activity_volume");

        _coord.EventBus.Publish(new LiveActivityPayload(
            "snap:bt", "bluetooth", LiveActivityKind.BluetoothConnect, "Sony WH-1000XM5", "Bluetooth • 82% battery",
            "\uD83C\uDFA7", "#38BDF8", LiveActivityPriority.High, TimeSpan.FromSeconds(30), 0.82, "bt"));
        Pump(150);
        await ShotAsync($"{index++:00}_activity_split_stack");

        _coord.Notchy.HandleAgentEvent(new ApiAgentEventRequest(
            "Claude Code", "permission", "Edit auth.cs?", "Allow file write", "snap2", 900));
        Pump(150);
        await ShotAsync($"{index++:00}_activity_agent_permission");

        // --- Layout variants ---
        foreach (var (layout, tag) in new[] { (NotchLayoutMode.FloatingIsland, "island"), (NotchLayoutMode.FullBar, "bar") })
        {
            _coord.Settings.LayoutMode = layout;
            DrainEventBus();
            sm.Collapse();
            Pump(150);
            await ShotAsync($"{index++:00}_layout_{tag}_rest");
            sm.HandleTrigger(NotchInteractionTrigger.PinToggle);
            Pump(150);
            await ShotAsync($"{index++:00}_layout_{tag}_expanded");
            sm.HandleTrigger(NotchInteractionTrigger.PinToggle);
            sm.Collapse();
        }
        _coord.Settings.LayoutMode = NotchLayoutMode.Notch;

        // --- Companion windows ---
        var settings = new SettingsWindow(_coord, () => { }, () => { });
        settings.Show();
        await SettleAsync();
        for (var tab = 0; tab < settings.Tabs.Items.Count; tab++)
        {
            settings.Tabs.SelectedIndex = tab;
            await SettleAsync();
            settings.UpdateLayout();
            SaveVisual((FrameworkElement)settings.Content, Path.Combine(dir, $"settings_{tab}.png"), backdrop: false);
        }
        settings.Close();

        var onboarding = new OnboardingWindow(_coord);
        onboarding.Show();
        await SettleAsync();
        SaveVisual((FrameworkElement)onboarding.Content, Path.Combine(dir, "onboarding.png"), backdrop: true);
        onboarding.Close();

        var palette = new CommandPaletteWindow(_coord, () => { }, () => { }, () => { });
        palette.Show();
        await SettleAsync();
        SaveVisual((FrameworkElement)palette.Content, Path.Combine(dir, "command_palette.png"), backdrop: true);
        palette.Close();
    }

    private static void SaveVisual(FrameworkElement element, string path, bool backdrop)
    {
        const double scale = 2.0;
        var w = (int)Math.Ceiling(element.ActualWidth);
        var h = (int)Math.Ceiling(element.ActualHeight);
        if (w <= 0 || h <= 0) return;

        var rtb = new RenderTargetBitmap((int)(w * scale), (int)(h * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        rtb.Render(element);

        BitmapSource final = rtb;
        if (backdrop)
        {
            var dv = new DrawingVisual();
            using (var dc = dv.RenderOpen())
            {
                var bg = new LinearGradientBrush(
                    Color.FromRgb(86, 108, 150), Color.FromRgb(30, 38, 62), 60);
                dc.DrawRectangle(bg, null, new Rect(0, 0, w, h));
                dc.DrawImage(rtb, new Rect(0, 0, w, h));
            }
            var composed = new RenderTargetBitmap((int)(w * scale), (int)(h * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            composed.Render(dv);
            final = composed;
        }

        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(final));
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}
