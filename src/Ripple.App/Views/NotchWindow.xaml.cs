using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Microsoft.Win32;
using Ripple.App.Interop;
using Ripple.App.Services;
using Ripple.App.ViewModels;
using Ripple.Core.Events;
using Ripple.Core.Geometry;
using Ripple.Core.Motion;
using Ripple.Core.Services;
using Ripple.Core.StateMachine;
using FormsScreen = System.Windows.Forms.Screen;
using FormsPower = System.Windows.Forms.SystemInformation;
using FormsBattery = System.Windows.Forms.PowerLineStatus;
using WpfBrushes = System.Windows.Media.Brushes;
using WpfColor = System.Windows.Media.Color;
using WpfDataFormats = System.Windows.DataFormats;
using WpfDragDropEffects = System.Windows.DragDropEffects;
using WpfDragEventArgs = System.Windows.DragEventArgs;
using WpfPoint = System.Windows.Point;
using WpfRectangle = System.Windows.Shapes.Rectangle;

namespace Ripple.App.Views;

public partial class NotchWindow : Window
{
    private const int HotkeyToggleId = 0x5201;
    private const int HotkeyPaletteId = 0x5202;

    private readonly RippleCoordinator _coord;
    private readonly Action _openSettings;
    private readonly Action _openPalette;
    private readonly Action _openFullscreenMusic;

    private HwndSource? _hwndSource;
    private IntPtr _hwnd;
    private readonly Stopwatch _frameWatch = Stopwatch.StartNew();
    private long _lastRenderTicks;
    private DateTimeOffset _lastServiceTick = DateTimeOffset.MinValue;
    private DateTimeOffset _lastContentRefresh = DateTimeOffset.MinValue;
    private bool _isSessionLocked;
    private bool _contentDirty = true;
    private string _lastRenderedModuleId = string.Empty;
    private int _lastTabDirection = 1;
    private readonly List<WpfRectangle> _eqBars = [];

    // Idle-cost control: frames are only fully processed when something is animating or dirty.
    private bool _snapshotMode;
    private bool _visualsDirty = true;
    private bool _allowActivation;
    private bool _onBattery;
    private long _lastPowerCheckTick;
    private NotchState _appliedState = NotchState.HiddenFullscreen;
    private (double, double, double, double, double, double, double, int) _lastShellKey;
    private WpfColor _lastRestColor;
    private bool _restColorInit;
    private System.Windows.Media.Effects.BlurEffect? _blurEffect;
    private readonly List<(FrameworkElement El, TranslateTransform T)> _stagger = [];
    private Panel? _eqPanel;
    private System.Windows.Threading.DispatcherTimer? _idleTimer;
    private bool _renderHooked;

    private static readonly Dictionary<string, string> RailGlyphs = new()
    {
        ["music"] = "\uE8D6",
        ["system"] = "\uE9D9",
        ["bluetooth"] = "\uE702",
        ["clipboard"] = "\uE77F",
        ["shelf"] = "\uE896",
        ["apps"] = "\uE71D",
        ["timer"] = "\uE916",
        ["mixer"] = "\uE767",
        ["notifications"] = "\uEA8F",
        ["notchy"] = "\uE943",
        ["utilities"] = "\uE90F"
    };

    public NotchWindow(
        RippleCoordinator coord,
        Action openSettings,
        Action openPalette,
        Action openFullscreenMusic)
    {
        InitializeComponent();
        _coord = coord;
        _openSettings = openSettings;
        _openPalette = openPalette;
        _openFullscreenMusic = openFullscreenMusic;

        InitializeEqualizerDots();

        _coord.CoordinatorChanged += () => Dispatcher.BeginInvoke(() => { _contentDirty = true; _visualsDirty = true; RebuildModuleTabRail(); });
        _coord.ActiveModuleChanged += newId => Dispatcher.BeginInvoke(() => AnimateModuleSwitch(newId));
        foreach (var mod in _coord.Modules)
        {
            switch (mod)
            {
                case MediaService ms: ms.Updated += MarkDirty; break;
                case SystemMonitorService sm: sm.Updated += MarkDirty; break;
                case BluetoothService bt: bt.Updated += MarkDirty; break;
                case ClipboardService cb: cb.Updated += MarkDirty; break;
                case ShelfService sh: sh.Updated += MarkDirty; break;
                case QuickAppsModuleService qa: qa.Updated += MarkDirty; break;
                case FocusProductivityService fp: fp.Updated += MarkDirty; break;
                case MixerModuleAdapter mx: mx.Updated += MarkDirty; break;
                case NotificationService nf: nf.Updated += MarkDirty; break;
                case AgentAndDevService ag: ag.Updated += MarkDirty; break;
                case UtilitiesService ut: ut.Updated += MarkDirty; break;
            }
        }

        Loaded += OnLoaded;
        Closing += OnClosing;
        PreviewKeyDown += OnPreviewKeyDown;
        PreviewMouseLeftButtonDown += OnPreviewMouseDownForTextInput;
        Deactivated += (_, _) => RestoreNoActivate();
        MouseDown += OnWindowMouseDown;
        MouseWheel += OnWindowMouseWheel;
        DragEnter += OnNotchDragEnter;
        DragOver += OnNotchDragOver;
        DragLeave += OnNotchDragLeave;
        Drop += OnNotchDrop;

        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.DisplaySettingsChanged += (_, _) => Dispatcher.BeginInvoke(RepositionToActiveMonitor);
    }

    private void MarkDirty()
    {
        _contentDirty = true;
        _visualsDirty = true;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;
        _blurEffect = ContentBlur;
        Win32Interop.ConfigureNotchWindowStyles(_hwnd);
        Win32Interop.AddClipboardFormatListener(_hwnd);

        // Register global hotkeys: Ctrl+Alt+N (Notch toggle) and Ctrl+Alt+Space (Command Palette)
        Win32Interop.RegisterHotKey(_hwnd, HotkeyToggleId, Win32Interop.MOD_CONTROL | Win32Interop.MOD_ALT | Win32Interop.MOD_NOREPEAT, 0x4E /* N */);
        Win32Interop.RegisterHotKey(_hwnd, HotkeyPaletteId, Win32Interop.MOD_CONTROL | Win32Interop.MOD_ALT | Win32Interop.MOD_NOREPEAT, 0x20 /* Space */);

        _hwndSource = HwndSource.FromHwnd(_hwnd);
        _hwndSource?.AddHook(WndProc);

        RepositionToActiveMonitor();
        RebuildModuleTabRail();
        RefreshModuleContent();

        _lastRenderTicks = _frameWatch.ElapsedTicks;
        _idleTimer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(45)
        };
        _idleTimer.Tick += (_, _) =>
        {
            if (!_isSessionLocked && !_snapshotMode && !_renderHooked)
            {
                RenderFrame(0.045, DateTimeOffset.UtcNow, pollPointer: true);
            }
        };
        _idleTimer.Start();
        EnsureRenderLoop(true);
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        _idleTimer?.Stop();
        EnsureRenderLoop(false);
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        if (_hwnd != IntPtr.Zero)
        {
            Win32Interop.RemoveClipboardFormatListener(_hwnd);
            Win32Interop.UnregisterHotKey(_hwnd, HotkeyToggleId);
            Win32Interop.UnregisterHotKey(_hwnd, HotkeyPaletteId);
        }
        _hwndSource?.RemoveHook(WndProc);
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        _isSessionLocked = e.Reason == SessionSwitchReason.SessionLock;
    }

    private void InitializeEqualizerDots()
    {
        RestEqCanvas.Children.Clear();
        _eqBars.Clear();
        for (var i = 0; i < 4; i++)
        {
            var rect = new WpfRectangle
            {
                Width = 3,
                Height = 6,
                RadiusX = 1.5,
                RadiusY = 1.5,
                Fill = new SolidColorBrush(WpfColor.FromRgb(108, 140, 255))
            };
            Canvas.SetLeft(rect, i * 6);
            Canvas.SetTop(rect, 4);
            RestEqCanvas.Children.Add(rect);
            _eqBars.Add(rect);
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case Win32Interop.WM_MOUSEACTIVATE:
                handled = true;
                return new IntPtr(_allowActivation ? 1 /* MA_ACTIVATE */ : Win32Interop.MA_NOACTIVATE);

            case Win32Interop.WM_NCHITTEST:
                var screenX = (short)(lParam.ToInt64() & 0xFFFF);
                var screenY = (short)((lParam.ToInt64() >> 16) & 0xFFFF);
                if (!IsScreenPointInteractive(screenX, screenY))
                {
                    handled = true;
                    return new IntPtr(Win32Interop.HTTRANSPARENT);
                }
                handled = true;
                return new IntPtr(Win32Interop.HTCLIENT);

            case Win32Interop.WM_CLIPBOARDUPDATE:
                _coord.Clipboard.OnClipboardChanged();
                break;

            case Win32Interop.WM_HOTKEY:
                var id = wParam.ToInt32();
                if (id == HotkeyToggleId)
                {
                    _coord.StateMachine.HandleTrigger(NotchInteractionTrigger.Hotkey);
                }
                else if (id == HotkeyPaletteId)
                {
                    _openPalette();
                }
                handled = true;
                break;
        }

        return IntPtr.Zero;
    }

    private void OnCompositionRendering(object? sender, EventArgs e)
    {
        if (_isSessionLocked || _snapshotMode) return;

        var nowTicks = _frameWatch.ElapsedTicks;
        var dt = (double)(nowTicks - _lastRenderTicks) / Stopwatch.Frequency;

        var tick = Environment.TickCount64;
        if (tick - _lastPowerCheckTick > 5000)
        {
            _lastPowerCheckTick = tick;
            _onBattery = FormsPower.PowerStatus.PowerLineStatus == FormsBattery.Offline;
        }

        // Idle frames are throttled to ~30 Hz; animating frames run at the display refresh rate
        // (or 30 fps on battery saver).
        var minInterval = _coord.Motion.IsAnimating
            ? (_coord.Settings.BatterySaver30Fps && _onBattery ? 1.0 / 31.0 : 0.0)
            : 1.0 / 30.0;
        if (dt < minInterval) return;

        _lastRenderTicks = nowTicks;
        RenderFrame(Math.Min(dt, 0.05), DateTimeOffset.UtcNow, pollPointer: true);
    }

    private void RenderFrame(double dt, DateTimeOffset utcNow, bool pollPointer)
    {
        var wasAnimating = _coord.Motion.IsAnimating;
        if (wasAnimating) _coord.RecordFrameTime(dt * 1000.0);

        if (pollPointer)
        {
            UpdatePointerAndMonitor(utcNow);
        }

        var state = _coord.StateMachine.CurrentState;
        var isOpen = state is NotchState.Expanded or NotchState.Pinned;

        var serviceInterval = (wasAnimating || isOpen) ? 100 : 250;
        if ((utcNow - _lastServiceTick).TotalMilliseconds >= serviceInterval)
        {
            _lastServiceTick = utcNow;
            _coord.TickServices(utcNow, isOpen);
            state = _coord.StateMachine.CurrentState;
            isOpen = state is NotchState.Expanded or NotchState.Pinned;
        }

        // Spring physics: retarget (interruptible, keeps velocity) and step every animated channel.
        var (targetW, targetH, targetR, targetY, peekGlow, contentReveal, blur, spring) = _coord.ComputeTargetMetrics();
        _coord.Motion.SetShellTarget(targetW, targetH, targetR, targetY, peekGlow, contentReveal, blur, spring, _coord.Settings.ReducedMotion);
        wasAnimating |= _coord.Motion.IsAnimating;
        _coord.Motion.Step(dt);

        // Visualizer only runs while something that shows it is on screen.
        var restGlanceStatus = state is NotchState.Rest or NotchState.Peek ? _coord.GetPrimaryGlanceIndicator().StatusText : null;
        var restViz = state is NotchState.Rest or NotchState.Peek && string.IsNullOrWhiteSpace(restGlanceStatus) && _coord.Media.Snapshot.IsPlaying;
        var openViz = isOpen && _coord.ActiveModuleId == "music";
        var vizActive = (restViz || openViz) && _coord.Media.VisualizerActive;
        if (vizActive)
        {
            _coord.Media.StepVisualizer();
        }

        if (wasAnimating || vizActive || _visualsDirty || state != _appliedState || (_contentDirty && isOpen))
        {
            _visualsDirty = false;
            _appliedState = state;
            ApplyMotionVisuals(utcNow);
        }

        EnsureRenderLoop(_coord.Motion.IsAnimating || vizActive);
    }

    private void EnsureRenderLoop(bool active)
    {
        if (_snapshotMode) return;
        if (active && !_renderHooked)
        {
            _renderHooked = true;
            _lastRenderTicks = _frameWatch.ElapsedTicks;
            CompositionTarget.Rendering += OnCompositionRendering;
        }
        else if (!active && _renderHooked)
        {
            _renderHooked = false;
            CompositionTarget.Rendering -= OnCompositionRendering;
        }
    }

    private bool IsUserInteracting() =>
        (Mouse.LeftButton == MouseButtonState.Pressed && IsMouseOver) ||
        Keyboard.FocusedElement is System.Windows.Controls.TextBox;

    private void UpdatePointerAndMonitor(DateTimeOffset utcNow)
    {
        if (!Win32Interop.GetCursorPos(out var cursor)) return;

        if (_coord.Settings.FollowCursorMonitor &&
            _coord.StateMachine.CurrentState is NotchState.Rest or NotchState.Peek)
        {
            var screen = FormsScreen.FromPoint(new System.Drawing.Point(cursor.X, cursor.Y));
            if (screen.DeviceName != _coord.ActiveScreenDeviceName)
            {
                RepositionToMonitor(screen);
            }
        }

        var localPt = ScreenToLocalDip(cursor.X, cursor.Y);
        var (restRect, currentRect, expandedRect) = GetHitTestRects();
        var isOpenOrOpening = _coord.StateMachine.CurrentState is NotchState.Expanded or NotchState.Pinned or NotchState.Activity;

        var insideHit = SafeCorridorHitTester.IsInsideInteractiveZone(
            localPt,
            restRect,
            currentRect,
            expandedRect,
            isOpenOrOpening,
            _coord.StateMachine.HoverEntryPoint);

        // While typing into a field inside the notch, never collapse underneath the user.
        if (Keyboard.FocusedElement is System.Windows.Controls.TextBox && isOpenOrOpening)
        {
            insideHit = true;
        }

        var inProximity = SafeCorridorHitTester.IsInProximityHalo(localPt, restRect, _coord.Settings.ProximityRadiusPx);

        // Collision avoidance only matters near the top edge, so skip the Win32 window queries elsewhere.
        var shouldDodge = false;
        if (_coord.Settings.CollisionAvoidance && localPt.Y < 80)
        {
            var restLeftScreen = LocalDipToScreenX(restRect.Left);
            var restRightScreen = LocalDipToScreenX(restRect.Right);
            var restBottomScreen = LocalDipToScreenY(restRect.Bottom);
            shouldDodge = FullscreenAndCollisionService.ShouldDodgeUnderlyingTitleBar(
                cursor, restLeftScreen, restRightScreen, restBottomScreen, _hwnd);
        }
        _coord.StateMachine.SetCollisionState(shouldDodge);

        _coord.StateMachine.UpdatePointer(insideHit, inProximity, localPt, _coord.Settings.HoverToExpand);
    }

    private void ApplyMotionVisuals(DateTimeOffset utcNow)
    {
        var m = _coord.Motion;
        var (shellBrush, borderBrush, glowColor) = _coord.ResolveThemeColors();
        var state = _coord.StateMachine.CurrentState;
        var isRestish = state is NotchState.Rest or NotchState.Peek;

        var focusGlance = _coord.FocusTimer.GetGlanceState();
        var ring = focusGlance.HasActiveIndicator ? focusGlance.Progress : null;

        Shell.BodyWidth = m.Width.Value;
        Shell.BodyHeight = m.Height.Value;
        Shell.CornerRadius = m.CornerRadius.Value;
        Shell.EarRadius = _coord.Settings.EarRadius;
        Shell.ShellOffsetY = m.OffsetY.Value;
        Shell.PeekGlowIntensity = m.PeekGlow.Value;
        Shell.PressScale = m.PressScale.Value;
        Shell.LayoutMode = _coord.Settings.LayoutMode;
        Shell.ShellFill = shellBrush;
        Shell.ShellBorderBrush = borderBrush;
        Shell.GlowColor = glowColor;
        Shell.ShowGlowEdge = _coord.Settings.EnableGlowEdge;
        Shell.ShowAuroraEffect = _coord.Settings.EnableAuroraEffect;
        Shell.IsRestState = isRestish;
        Shell.RestProgressRing = ring;

        var shellKey = (
            m.Width.Value, m.Height.Value, m.CornerRadius.Value + m.PressScale.Value * 1000.0, m.OffsetY.Value,
            m.PeekGlow.Value, (ring ?? -1.0) + _coord.Settings.EarRadius * 10.0,
            (double)shellBrush.GetHashCode() + glowColor.GetHashCode() + (int)_coord.Settings.LayoutMode,
            (int)state);
        if (shellKey != _lastShellKey)
        {
            _lastShellKey = shellKey;
            Shell.InvalidateShell();
        }

        // Rest / glance layer: subtle ambient indicator at rest, or active glance / equalizer.
        var glance = _coord.GetPrimaryGlanceIndicator();
        var showRest = state is NotchState.Rest or NotchState.Peek
                       && m.Height.Value > 18
                       && m.ContentReveal.Value < 0.05;
        RestLayer.Visibility = showRest ? Visibility.Visible : Visibility.Collapsed;
        if (showRest)
        {
            RestLayer.Width = Math.Max(40, m.Width.Value);
            RestLayer.Height = Math.Max(20, m.Height.Value);
            RestLayer.Margin = new Thickness(0, Math.Max(0, m.OffsetY.Value), 0, 0);
            UpdateRestGlance(glowColor, glance);
        }

        // Live Activity layer (with split capsule support)
        var actSnap = state == NotchState.Activity ? _coord.EventBus.GetSnapshot() : null;
        var showAct = actSnap?.Primary is not null;
        ActivityLayer.Visibility = showAct ? Visibility.Visible : Visibility.Collapsed;
        if (showAct && actSnap!.Primary is { } primary)
        {
            ActivityLayer.Width = Math.Max(120, m.Width.Value);
            ActivityLayer.Height = Math.Max(36, m.Height.Value);
            ActivityLayer.Margin = new Thickness(0, Math.Max(0, m.OffsetY.Value), 0, 0);
            ActivityLayer.Opacity = Math.Clamp((m.Width.Value - _coord.Settings.RestWidth) / 80.0, 0.0, 1.0);
            UpdateActivityCapsule(primary, actSnap.Secondary);
        }

        // Expanded layer: container fades in fast, children cascade in; blur-to-sharp only while animating.
        var reveal = Math.Clamp(m.ContentReveal.Value, 0.0, 1.0);
        var showExpanded = (state is NotchState.Expanded or NotchState.Pinned || reveal > 0.02) && state != NotchState.HiddenFullscreen;
        ExpandedLayer.Visibility = showExpanded ? Visibility.Visible : Visibility.Collapsed;
        if (showExpanded)
        {
            ExpandedLayer.Width = Math.Max(160, m.Width.Value);
            ExpandedLayer.Height = Math.Max(80, m.Height.Value);
            ExpandedLayer.Margin = new Thickness(0, Math.Max(0, m.OffsetY.Value), 0, 0);
            ExpandedLayer.Opacity = Math.Min(1.0, reveal * 3.0);

            var blurRadius = Math.Clamp(m.BlurRadius.Value, 0, 12);
            if (blurRadius > 0.4 && _blurEffect is not null)
            {
                _blurEffect.Radius = blurRadius;
                ExpandedLayer.Effect = _blurEffect;
            }
            else
            {
                ExpandedLayer.Effect = null;
            }
            ContentSlideTransform.Y = (1.0 - reveal) * -8.0;

            PinBtn.Opacity = state == NotchState.Pinned ? 1.0 : 0.6;

            if (_contentDirty && !IsUserInteracting() &&
                (utcNow - _lastContentRefresh).TotalMilliseconds >= 400)
            {
                _lastContentRefresh = utcNow;
                _contentDirty = false;
                RefreshModuleContent();
            }

            ApplyStagger();
            UpdateExpandedVisualizer();
        }
        else if (ExpandedLayer.Effect is not null)
        {
            ExpandedLayer.Effect = null;
        }
    }

    private void UpdateRestGlance(WpfColor accent, Ripple.Core.Modules.ModuleGlanceState glance)
    {
        var dot = RippleCoordinator.ParseColorOrDefault(glance.AccentHex, accent);
        if (!_restColorInit || dot != _lastRestColor)
        {
            _restColorInit = true;
            _lastRestColor = dot;
            var b = new SolidColorBrush(dot);
            b.Freeze();
            RestStatusDot.Fill = b;
            foreach (var bar in _eqBars) bar.Fill = b;
        }

        if (!glance.HasActiveIndicator)
        {
            RestEqCanvas.Visibility = Visibility.Collapsed;
            RestLeftLabel.Text = "Ripple";
            RestRightLabel.Text = DateTime.Now.ToString("HH:mm");
        }
        else if (!string.IsNullOrWhiteSpace(glance.StatusText))
        {
            RestEqCanvas.Visibility = Visibility.Collapsed;
            RestLeftLabel.Text = Truncate(glance.Label, 14);
            RestRightLabel.Text = "·  " + Truncate(glance.StatusText, 28);
        }
        else if (_coord.Media.Snapshot.IsPlaying)
        {
            RestLeftLabel.Text = Truncate(glance.Label, 14);
            RestRightLabel.Text = "";
            RestEqCanvas.Visibility = Visibility.Visible;
            var bands = _coord.Media.EqualizerBands;
            for (var i = 0; i < _eqBars.Count; i++)
            {
                var h = Math.Clamp(3 + bands[i * 4] * 11, 3, 14);
                _eqBars[i].Height = h;
                Canvas.SetTop(_eqBars[i], 14 - h);
            }
        }
        else
        {
            RestEqCanvas.Visibility = Visibility.Collapsed;
            RestLeftLabel.Text = Truncate(glance.Label, 14);
            RestRightLabel.Text = "";
        }
    }

    private void ApplyStagger()
    {
        var count = _stagger.Count;
        for (var i = 0; i < count; i++)
        {
            var (el, t) = _stagger[i];
            var p = _coord.Motion.GetStaggeredProgress(i, count);
            el.Opacity = p;
            t.Y = (1.0 - p) * 10.0;
        }
    }

    private void UpdateExpandedVisualizer()
    {
        if (_eqPanel is null) return;
        var bands = _coord.Media.EqualizerBands;
        var n = _eqPanel.Children.Count;
        for (var i = 0; i < n; i++)
        {
            if (_eqPanel.Children[i] is FrameworkElement bar)
            {
                bar.Height = Math.Max(3, bands[i % bands.Count] * 16);
            }
        }
    }

    private void PrepareStagger(UIElement? root)
    {
        _stagger.Clear();
        _eqPanel = null;
        if (root is Panel p)
        {
            foreach (var child in p.Children.OfType<FrameworkElement>())
            {
                var t = new TranslateTransform();
                child.RenderTransform = t;
                _stagger.Add((child, t));
            }
        }
        _eqPanel = FindTagged(root as DependencyObject, "eq") as Panel;
    }

    private static DependencyObject? FindTagged(DependencyObject? node, string tag)
    {
        if (node is null) return null;
        if (node is FrameworkElement { Tag: string s } && s == tag) return node;
        foreach (var child in LogicalTreeHelper.GetChildren(node))
        {
            if (child is DependencyObject d && FindTagged(d, tag) is { } hit) return hit;
        }
        return null;
    }

    private static void CollectScrollViewers(DependencyObject? node, List<ScrollViewer> into)
    {
        if (node is null) return;
        if (node is ScrollViewer sv) into.Add(sv);
        foreach (var child in LogicalTreeHelper.GetChildren(node))
        {
            if (child is DependencyObject d) CollectScrollViewers(d, into);
        }
    }

    private void OnPreviewMouseDownForTextInput(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject d) return;
        while (d is not null && d is not System.Windows.Controls.TextBox)
        {
            d = d is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(d)
                : LogicalTreeHelper.GetParent(d);
        }
        if (d is System.Windows.Controls.TextBox tb)
        {
            // Text entry needs real keyboard focus: allow activation just for the duration of the edit.
            _allowActivation = true;
            Win32Interop.SetNoActivate(_hwnd, false);
            Activate();
            tb.Focus();
        }
    }

    private void RestoreNoActivate()
    {
        _allowActivation = false;
        if (_hwnd != IntPtr.Zero) Win32Interop.SetNoActivate(_hwnd, true);
    }

    private void UpdateActivityCapsule(LiveActivityEntry primary, LiveActivityEntry? secondary)
    {
        var p = primary.Payload;
        ActIcon.Text = p.Icon;
        ActTitle.Text = p.Title;
        ActSubtitle.Text = p.Subtitle ?? "";
        ActSubtitle.Visibility = string.IsNullOrWhiteSpace(p.Subtitle) ? Visibility.Collapsed : Visibility.Visible;

        if (p.Progress is { } prog)
        {
            ActProgress.Visibility = Visibility.Visible;
            ActProgress.Value = Math.Clamp(prog, 0, 1);
            ActProgress.Foreground = new SolidColorBrush(RippleCoordinator.ParseColorOrDefault(p.AccentHex, WpfColor.FromRgb(108, 140, 255)));
        }
        else
        {
            ActProgress.Visibility = Visibility.Collapsed;
        }

        if (p.RequiresAction)
        {
            ActPrimaryBtn.Visibility = string.IsNullOrEmpty(p.PrimaryActionLabel) ? Visibility.Collapsed : Visibility.Visible;
            ActPrimaryBtn.Content = p.PrimaryActionLabel ?? "OK";
            ActPrimaryBtn.Tag = p;

            ActSecondaryBtn.Visibility = string.IsNullOrEmpty(p.SecondaryActionLabel) ? Visibility.Collapsed : Visibility.Visible;
            ActSecondaryBtn.Content = p.SecondaryActionLabel ?? "Dismiss";
            ActSecondaryBtn.Tag = p;
        }
        else
        {
            ActPrimaryBtn.Visibility = Visibility.Collapsed;
            ActSecondaryBtn.Visibility = Visibility.Collapsed;
        }

        if (secondary is not null)
        {
            SplitCapsuleBadge.Visibility = Visibility.Visible;
            SplitIcon.Text = secondary.Payload.Icon;
            SplitLabel.Text = Truncate(secondary.Payload.Title, 10);
            SplitCapsuleBadge.Tag = secondary.Payload.SourceModuleId;
        }
        else
        {
            SplitCapsuleBadge.Visibility = Visibility.Collapsed;
        }
    }

    private string _railKey = string.Empty;
    private string _contentModuleId = string.Empty;

    private void RebuildModuleTabRail()
    {
        var mods = _coord.GetOrderedEnabledModules().ToList();
        var (_, _, glow) = _coord.ResolveThemeColors();
        var key = string.Join(",", mods.Select(m => m.Id)) + "|" + _coord.ActiveModuleId + "|" +
                  _coord.IsMotionTunerVisible + "|" + glow;
        if (key == _railKey) return;
        _railKey = key;

        var iconFont = (System.Windows.Media.FontFamily)FindResource("IconFont");
        var selectedBg = new SolidColorBrush(WpfColor.FromArgb(0x46, glow.R, glow.G, glow.B));
        selectedBg.Freeze();
        var idleBg = new SolidColorBrush(WpfColor.FromArgb(0x10, 255, 255, 255));
        idleBg.Freeze();
        var idleFg = new SolidColorBrush(WpfColor.FromRgb(168, 180, 200));
        idleFg.Freeze();

        ModuleTabRail.Children.Clear();
        foreach (var mod in mods)
        {
            var isSelected = mod.Id == _coord.ActiveModuleId && !_coord.IsMotionTunerVisible;
            var glyph = RailGlyphs.TryGetValue(mod.Id, out var g) ? g : "\uE80F";

            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(new TextBlock
            {
                Text = glyph,
                FontFamily = iconFont,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center
            });
            if (isSelected)
            {
                content.Children.Add(new TextBlock
                {
                    Text = mod.Name,
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(5, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center
                });
            }

            var btn = new Button
            {
                Content = content,
                Tag = mod.Id,
                ToolTip = mod.Name,
                Background = isSelected ? selectedBg : idleBg,
                Foreground = isSelected ? WpfBrushes.White : idleFg,
                Padding = new Thickness(isSelected ? 8 : 6, 4, isSelected ? 9 : 6, 4),
                Margin = new Thickness(0, 0, 2, 0)
            };
            btn.Click += (_, _) =>
            {
                _coord.IsMotionTunerVisible = false;
                _railKey = string.Empty;
                _coord.SelectModuleByUser(mod.Id);
                RebuildModuleTabRail();
                _contentDirty = true;
            };
            if (isSelected)
            {
                btn.Loaded += (_, _) => btn.BringIntoView();
            }
            ModuleTabRail.Children.Add(btn);
        }
    }

    private void AnimateModuleSwitch(string newId)
    {
        var enabled = _coord.GetOrderedEnabledModules().Select(m => m.Id).ToList();
        var oldIdx = enabled.IndexOf(_lastRenderedModuleId);
        var newIdx = enabled.IndexOf(newId);
        _lastTabDirection = newIdx >= oldIdx ? 1 : -1;
        _lastRenderedModuleId = newId;

        RebuildModuleTabRail();
        RefreshModuleContent();
        _visualsDirty = true;

        if (!_coord.Settings.ReducedMotion && !_snapshotMode)
        {
            // Shared-element feel: content slides in from the direction of travel while fading up.
            var slide = new DoubleAnimation(26 * _lastTabDirection, 0, TimeSpan.FromMilliseconds(320))
            {
                EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 }
            };
            ModuleSlideTransform.BeginAnimation(TranslateTransform.XProperty, slide);
            var fade = new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(200));
            ModuleContentHost.BeginAnimation(OpacityProperty, fade);
        }
    }

    private void RefreshModuleContent()
    {
        var sameModule = _contentModuleId == _coord.ActiveModuleId;
        var offsets = new List<double>();
        if (sameModule)
        {
            var old = new List<ScrollViewer>();
            CollectScrollViewers(ModuleContentHost.Content as DependencyObject, old);
            offsets.AddRange(old.Select(s => s.VerticalOffset));
        }

        var content = NotchModulePanelBuilder.BuildForModule(
            _coord.ActiveModuleId,
            _coord,
            _openFullscreenMusic,
            _openPalette,
            _openSettings);
        _contentModuleId = _coord.ActiveModuleId;
        ModuleContentHost.Content = content;
        PrepareStagger(content);
        ApplyStagger();

        if (offsets.Any(o => o > 0.5))
        {
            var fresh = new List<ScrollViewer>();
            CollectScrollViewers(content, fresh);
            ModuleContentHost.UpdateLayout();
            for (var i = 0; i < Math.Min(fresh.Count, offsets.Count); i++)
            {
                fresh[i].ScrollToVerticalOffset(offsets[i]);
            }
        }
    }

    public void RepositionToActiveMonitor()
    {
        _visualsDirty = true;
        _railKey = string.Empty;
        Win32Interop.GetCursorPos(out var pt);
        var screen = FormsScreen.FromPoint(new System.Drawing.Point(pt.X, pt.Y));
        RepositionToMonitor(screen);
    }

    private void RepositionToMonitor(FormsScreen screen)
    {
        _coord.ActiveScreenDeviceName = screen.DeviceName;
        var source = PresentationSource.FromVisual(this);
        var scaleX = source?.CompositionTarget?.TransformFromDevice.M11 ?? 1.0;
        var scaleY = source?.CompositionTarget?.TransformFromDevice.M22 ?? 1.0;

        var monLeft = screen.Bounds.Left * scaleX;
        var monTop = screen.Bounds.Top * scaleY;
        var monWidth = screen.Bounds.Width * scaleX;

        var anchorX = _coord.Settings.HorizontalAnchor switch
        {
            NotchHorizontalAnchor.Left => monLeft + 24,
            NotchHorizontalAnchor.Right => monLeft + monWidth - Width - 24,
            _ => monLeft + (monWidth - Width) * 0.5
        };

        Left = anchorX + _coord.Settings.OffsetX;
        Top = monTop + (_coord.Settings.DockEdge == NotchDockEdge.Bottom
            ? screen.Bounds.Height * scaleY - Height
            : 0) + _coord.Settings.OffsetY;
    }

    private bool IsScreenPointInteractive(int screenX, int screenY)
    {
        var pt = ScreenToLocalDip(screenX, screenY);
        var (restRect, currentRect, expandedRect) = GetHitTestRects();
        var isOpen = _coord.StateMachine.CurrentState is NotchState.Expanded or NotchState.Pinned or NotchState.Activity;
        return SafeCorridorHitTester.IsInsideInteractiveZone(
            pt,
            restRect,
            currentRect,
            expandedRect,
            isOpen,
            _coord.StateMachine.HoverEntryPoint);
    }

    private (Rect2D RestRect, Rect2D CurrentRect, Rect2D ExpandedRect) GetHitTestRects()
    {
        var cx = Width * 0.5;
        var rw = _coord.Settings.RestWidth + 32;
        var rh = _coord.Settings.RestHeight;
        var restY = _coord.Settings.LayoutMode == NotchLayoutMode.FloatingIsland ? 10.0 : 0.0;
        var rest = new Rect2D(cx - rw * 0.5, restY, rw, rh);

        var cw = Math.Max(40, _coord.Motion.Width.Value);
        var ch = Math.Max(6, _coord.Motion.Height.Value);
        var cy = Math.Max(0, _coord.Motion.OffsetY.Value);
        var current = new Rect2D(cx - cw * 0.5, cy, cw, ch);

        var mod = _coord.GetActiveModule();
        var ew = mod.PreferredExpandedWidth;
        var eh = mod.PreferredExpandedHeight;
        var expanded = new Rect2D(cx - ew * 0.5, restY, ew, eh);
        return (rest, current, expanded);
    }

    private Point2D ScreenToLocalDip(int screenX, int screenY)
    {
        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget is { } ct)
        {
            var pt = ct.TransformFromDevice.Transform(new WpfPoint(screenX, screenY));
            return new Point2D(pt.X - Left, pt.Y - Top);
        }
        return new Point2D(screenX - Left, screenY - Top);
    }

    private double LocalDipToScreenX(double localX)
    {
        var source = PresentationSource.FromVisual(this);
        var scale = source?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        return (Left + localX) * scale;
    }

    private double LocalDipToScreenY(double localY)
    {
        var source = PresentationSource.FromVisual(this);
        var scale = source?.CompositionTarget?.TransformToDevice.M22 ?? 1.0;
        return (Top + localY) * scale;
    }

    private void OnWindowMouseDown(object sender, MouseButtonEventArgs e)
    {
        var st = _coord.StateMachine.CurrentState;
        if (st is NotchState.Rest or NotchState.Peek or NotchState.Activity)
        {
            _coord.Motion.PressScale.Impulse(-0.55);
        }

        if (e.ChangedButton == MouseButton.Middle)
        {
            _coord.StateMachine.HandleTrigger(NotchInteractionTrigger.PinToggle);
            e.Handled = true;
        }
        else if (e.ChangedButton == MouseButton.Left &&
                 _coord.StateMachine.CurrentState is NotchState.Rest or NotchState.Peek or NotchState.Activity)
        {
            _coord.StateMachine.HandleTrigger(NotchInteractionTrigger.Click);
        }
        else if (e.ChangedButton == MouseButton.Right)
        {
            ShowQuickContextMenu();
            e.Handled = true;
        }
    }

    private void OnWindowMouseWheel(object sender, MouseWheelEventArgs e)
    {
        // Volume-by-scroll applies to the collapsed pill only, so scrolling lists inside the open panel is safe.
        if (_coord.StateMachine.CurrentState is NotchState.Expanded or NotchState.Pinned) return;
        var (vol, _) = _coord.AudioMixer.GetMasterVolume();
        var delta = e.Delta > 0 ? 0.04 : -0.04;
        _coord.AudioMixer.SetMasterVolume(Math.Clamp(vol + delta, 0.0, 1.0));
    }

    private void OnPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            _coord.StateMachine.Collapse();
        }
        else if (e.Key == Key.Left && Keyboard.Modifiers == ModifierKeys.Control)
        {
            _coord.CycleModule(-1);
        }
        else if (e.Key == Key.Right && Keyboard.Modifiers == ModifierKeys.Control)
        {
            _coord.CycleModule(1);
        }
    }

    private void OnNotchDragEnter(object sender, WpfDragEventArgs e)
    {
        _coord.SelectModuleByUser("shelf");
        _coord.StateMachine.HandleTrigger(NotchInteractionTrigger.DragDrop);
        e.Effects = WpfDragDropEffects.Copy;
        e.Handled = true;
    }

    private void OnNotchDragOver(object sender, WpfDragEventArgs e)
    {
        e.Effects = WpfDragDropEffects.Copy;
        e.Handled = true;
    }

    private void OnNotchDragLeave(object sender, WpfDragEventArgs e)
    {
        _coord.StateMachine.EndDragDrop(keepOpenBriefly: true);
    }

    private void OnNotchDrop(object sender, WpfDragEventArgs e)
    {
        // Tactile settle wobble when a drop is accepted
        _coord.Motion.PressScale.Impulse(-0.85);

        if (e.Data.GetDataPresent(WpfDataFormats.FileDrop) &&
            e.Data.GetData(WpfDataFormats.FileDrop) is string[] files)
        {
            _coord.Shelf.AddDroppedPaths(files);
        }
        else if (e.Data.GetDataPresent(WpfDataFormats.UnicodeText) &&
                 e.Data.GetData(WpfDataFormats.UnicodeText) is string text)
        {
            _coord.Shelf.AddDroppedTextOrUrl(text);
        }

        _coord.SelectModuleByUser("shelf");
        _coord.StateMachine.EndDragDrop(keepOpenBriefly: true);
        RefreshModuleContent();
    }

    private void OnPinClicked(object sender, RoutedEventArgs e) =>
        _coord.StateMachine.HandleTrigger(NotchInteractionTrigger.PinToggle);

    private void OnPaletteClicked(object sender, RoutedEventArgs e) => _openPalette();

    private void OnMotionTunerClicked(object sender, RoutedEventArgs e)
    {
        _coord.IsMotionTunerVisible = !_coord.IsMotionTunerVisible;
        RebuildModuleTabRail();
        RefreshModuleContent();
    }

    private void OnSettingsClicked(object sender, RoutedEventArgs e) => _openSettings();

    private void OnActivityPrimaryActionClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: LiveActivityPayload p })
        {
            if (p.Kind == LiveActivityKind.AgentEvent)
            {
                var agentName = p.Id.StartsWith("agent:") ? p.Id[6..] : "Claude Code";
                _coord.Notchy.ResolveApproval(agentName, true);
            }
            else if (p.Kind == LiveActivityKind.TimerAlert)
            {
                _coord.FocusTimer.StartTimer(5, "Snoozed Focus");
                _coord.EventBus.Dismiss(p.Id);
            }
        }
    }

    private void OnActivitySecondaryActionClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: LiveActivityPayload p })
        {
            if (p.Kind == LiveActivityKind.AgentEvent)
            {
                var agentName = p.Id.StartsWith("agent:") ? p.Id[6..] : "Claude Code";
                _coord.Notchy.ResolveApproval(agentName, false);
            }
            else
            {
                _coord.EventBus.Dismiss(p.Id);
            }
        }
    }

    private void OnSplitBadgeClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string moduleId } && !string.IsNullOrWhiteSpace(moduleId))
        {
            _coord.SelectModuleByUser(moduleId);
            _coord.StateMachine.HandleTrigger(NotchInteractionTrigger.Click);
        }
    }

    private void ShowQuickContextMenu()
    {
        var menu = new ContextMenu();
        var pinItem = new MenuItem { Header = _coord.StateMachine.CurrentState == NotchState.Pinned ? "Unpin Notch" : "Pin Open" };
        pinItem.Click += (_, _) => _coord.StateMachine.HandleTrigger(NotchInteractionTrigger.PinToggle);
        menu.Items.Add(pinItem);

        var fsMusic = new MenuItem { Header = "Fullscreen Music Visualizer" };
        fsMusic.Click += (_, _) => _openFullscreenMusic();
        menu.Items.Add(fsMusic);

        var profMenu = new MenuItem { Header = "Switch Profile" };
        foreach (var p in _coord.Settings.Profiles)
        {
            var pi = new MenuItem { Header = p.Name, IsChecked = p.Name == _coord.Settings.ActiveProfileName };
            pi.Click += (_, _) => _coord.ApplyProfile(p.Name);
            profMenu.Items.Add(pi);
        }
        menu.Items.Add(profMenu);

        var settingsItem = new MenuItem { Header = "Settings..." };
        settingsItem.Click += (_, _) => _openSettings();
        menu.Items.Add(settingsItem);

        menu.IsOpen = true;
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "…";
}
