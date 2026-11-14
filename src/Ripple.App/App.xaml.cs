using System.IO;
using System.Threading;
using System.Windows;
using Ripple.App.ViewModels;
using Ripple.App.Views;
using Ripple.Core.Geometry;
using Ripple.Core.StateMachine;
using DrawingBitmap = System.Drawing.Bitmap;
using DrawingBrush = System.Drawing.SolidBrush;
using DrawingColor = System.Drawing.Color;
using DrawingGraphics = System.Drawing.Graphics;
using DrawingIcon = System.Drawing.Icon;
using FormsContextMenu = System.Windows.Forms.ContextMenuStrip;
using FormsNotifyIcon = System.Windows.Forms.NotifyIcon;
using WpfApplication = System.Windows.Application;

namespace Ripple.App;

public partial class App : WpfApplication
{
    private Mutex? _singleInstanceMutex;
    private RippleCoordinator? _coord;
    private NotchWindow? _notchWindow;
    private FormsNotifyIcon? _trayIcon;
    private SettingsWindow? _settingsWindow;
    private CommandPaletteWindow? _paletteWindow;
    private FullscreenMusicWindow? _fsMusicWindow;
    private OnboardingWindow? _onboardingWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            LogCrash("DispatcherUnhandled", args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex) LogCrash("AppDomainUnhandled", ex);
        };

        var isSelfTest = e.Args.Any(a => string.Equals(a, "--self-test", StringComparison.OrdinalIgnoreCase));
        var snapIdx = Array.FindIndex(e.Args, a => string.Equals(a, "--snapshot", StringComparison.OrdinalIgnoreCase));
        var snapshotDir = snapIdx >= 0 && snapIdx + 1 < e.Args.Length ? e.Args[snapIdx + 1] : null;

        if (!isSelfTest && snapshotDir is null)
        {
            _singleInstanceMutex = new Mutex(true, @"Local\RippleDynamicNotchSingleInstance", out var createdNew);
            if (!createdNew)
            {
                Shutdown(0);
                return;
            }
        }

        _coord = new RippleCoordinator();
        _notchWindow = new NotchWindow(
            _coord,
            OpenSettingsWindow,
            OpenCommandPalette,
            OpenFullscreenMusic);

        _notchWindow.Show();

        if (snapshotDir is not null)
        {
            var win = _notchWindow;
            var coord = _coord;
            _ = Dispatcher.InvokeAsync(async () =>
            {
                try { await win.RunSnapshotSuiteAsync(snapshotDir); }
                catch (Exception ex) { LogCrash("Snapshot", ex); }
                win.Close();
                coord.Dispose();
                Shutdown(0);
            }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            return;
        }
        if (isSelfTest)
        {
            var geo = NotchGeometryBuilder.Build(new NotchGeometrySpec(740, 360, 188, 32, 16, 12, 0, NotchLayoutMode.Notch));
            if (geo.Segments.Count < 4)
            {
                Shutdown(2);
                return;
            }
            Dispatcher.BeginInvoke(() =>
            {
                _notchWindow.Close();
                _coord.Dispose();
                Shutdown(0);
            }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            return;
        }

        InitializeTrayIcon();

        if (!_coord.Settings.OnboardingCompleted)
        {
            OpenOnboardingWindow();
        }
    }

    private void InitializeTrayIcon()
    {
        var menu = new FormsContextMenu();
        menu.Items.Add("Toggle / Pin Notch (Ctrl+Alt+N)", null, (_, _) =>
            _coord?.StateMachine.HandleTrigger(NotchInteractionTrigger.Hotkey));
        menu.Items.Add("Command Palette (Ctrl+Alt+Space)", null, (_, _) => OpenCommandPalette());
        menu.Items.Add("Fullscreen Music Visualizer", null, (_, _) => OpenFullscreenMusic());
        menu.Items.Add("MotionTuner Debug", null, (_, _) =>
        {
            if (_coord is null) return;
            _coord.IsMotionTunerVisible = !_coord.IsMotionTunerVisible;
            _coord.StateMachine.HandleTrigger(NotchInteractionTrigger.PinToggle);
        });
        menu.Items.Add("-");
        menu.Items.Add("Settings...", null, (_, _) => OpenSettingsWindow());
        menu.Items.Add("Onboarding Tour", null, (_, _) => OpenOnboardingWindow());
        menu.Items.Add("Notch Doctor Diagnostics", null, (_, _) =>
        {
            if (_coord is not null)
                System.Windows.MessageBox.Show(_coord.BuildDiagnosticsReport(), "Ripple Notch Doctor", MessageBoxButton.OK, MessageBoxImage.Information);
        });
        menu.Items.Add("-");
        menu.Items.Add("Exit Ripple", null, (_, _) => ShutdownApplication());

        _trayIcon = new FormsNotifyIcon
        {
            Icon = CreateRippleTrayIcon(),
            Text = "Ripple — Dynamic Notch",
            ContextMenuStrip = menu,
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => OpenSettingsWindow();
    }

    private void OpenSettingsWindow()
    {
        if (_coord is null || _notchWindow is null) return;
        if (_settingsWindow is { IsLoaded: true })
        {
            _settingsWindow.Activate();
            return;
        }
        _settingsWindow = new SettingsWindow(_coord, () => _notchWindow.RepositionToActiveMonitor(), OpenOnboardingWindow);
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private void OpenCommandPalette()
    {
        if (_coord is null) return;
        if (_paletteWindow is { IsLoaded: true })
        {
            _paletteWindow.Activate();
            return;
        }
        _paletteWindow = new CommandPaletteWindow(_coord, OpenSettingsWindow, OpenFullscreenMusic, OpenOnboardingWindow);
        _paletteWindow.Show();
        _paletteWindow.Activate();
    }

    private void OpenFullscreenMusic()
    {
        if (_coord is null) return;
        if (_fsMusicWindow is { IsLoaded: true })
        {
            _fsMusicWindow.Activate();
            return;
        }
        _fsMusicWindow = new FullscreenMusicWindow(_coord);
        _fsMusicWindow.Show();
        _fsMusicWindow.Activate();
    }

    private void OpenOnboardingWindow()
    {
        if (_coord is null) return;
        if (_onboardingWindow is { IsLoaded: true })
        {
            _onboardingWindow.Activate();
            return;
        }
        _onboardingWindow = new OnboardingWindow(_coord);
        _onboardingWindow.Show();
        _onboardingWindow.Activate();
    }

    private void ShutdownApplication()
    {
        _trayIcon?.Dispose();
        _notchWindow?.Close();
        _coord?.Dispose();
        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        Shutdown(0);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIcon?.Dispose();
        _coord?.Dispose();
        base.OnExit(e);
    }

    private static DrawingIcon CreateRippleTrayIcon()
    {
        using var bmp = new DrawingBitmap(32, 32);
        using (var g = DrawingGraphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(DrawingColor.Transparent);
            using var bg = new DrawingBrush(DrawingColor.FromArgb(14, 16, 24));
            g.FillEllipse(bg, 2, 2, 28, 28);
            using var ring = new System.Drawing.Pen(DrawingColor.FromArgb(108, 140, 255), 3f);
            g.DrawEllipse(ring, 6, 6, 20, 20);
            using var dot = new DrawingBrush(DrawingColor.White);
            g.FillEllipse(dot, 13, 13, 6, 6);
        }
        return DrawingIcon.FromHandle(bmp.GetHicon());
    }

    private static void LogCrash(string kind, Exception ex)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ripple");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "crash.log"), $"[{DateTimeOffset.Now:O}] {kind}: {ex}\n\n");
        }
        catch { }
    }
}
