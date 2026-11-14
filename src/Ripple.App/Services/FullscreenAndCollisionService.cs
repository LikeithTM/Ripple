using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Ripple.App.Interop;
using Ripple.Core.Services;

namespace Ripple.App.Services;

/// <summary>
/// Handles per-monitor DPI v2 screen geometry, exclusive/borderless fullscreen game &amp; video detection,
/// Focus Assist status, and cursor collision avoidance over underlying window title bars/tabs.
/// </summary>
public sealed class FullscreenAndCollisionService
{
    private static readonly HashSet<string> ShellClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Windows.UI.Core.CoreWindow"
    };

    private long _lastFsCheckTick;
    private bool _lastFsResult;
    private string? _lastFsApp;

    public bool IsFullscreenActive(RippleSettings settings, out string? activeAppName)
    {
        // Foreground/fullscreen state changes slowly: sample at ~2 Hz instead of every service tick.
        var tick = Environment.TickCount64;
        if (tick - _lastFsCheckTick < 500)
        {
            activeAppName = _lastFsApp;
            return _lastFsResult;
        }

        _lastFsCheckTick = tick;
        _lastFsResult = ComputeFullscreen(settings, out _lastFsApp);
        activeAppName = _lastFsApp;
        return _lastFsResult;
    }

    private bool ComputeFullscreen(RippleSettings settings, out string? activeAppName)
    {
        activeAppName = null;
        try
        {
            if (Win32Interop.SHQueryUserNotificationState(out var qState) == 0 &&
                qState is Win32Interop.QUERY_USER_NOTIFICATION_STATE.QUNS_RUNNING_D3D_FULL_SCREEN or
                         Win32Interop.QUERY_USER_NOTIFICATION_STATE.QUNS_PRESENTATION_MODE)
            {
                activeAppName = qState.ToString();
                return true;
            }

            var fg = Win32Interop.GetForegroundWindow();
            if (fg == IntPtr.Zero) return false;

            var cls = new StringBuilder(128);
            Win32Interop.GetClassName(fg, cls, cls.Capacity);
            if (ShellClasses.Contains(cls.ToString())) return false;

            Win32Interop.GetWindowThreadProcessId(fg, out var pid);
            if (pid == Environment.ProcessId) return false;

            string? procName = null;
            try
            {
                using var proc = Process.GetProcessById((int)pid);
                procName = proc.ProcessName;
            }
            catch { }

            activeAppName = procName;

            if (!string.IsNullOrEmpty(procName))
            {
                if (settings.FullscreenDenyList.Contains(procName, StringComparer.OrdinalIgnoreCase))
                    return false; // User explicitly wants Ripple visible over this app

                if (settings.FullscreenAllowList.Contains(procName, StringComparer.OrdinalIgnoreCase))
                    return true;
            }

            if (!Win32Interop.GetWindowRect(fg, out var winRect)) return false;
            var mon = Win32Interop.MonitorFromWindow(fg, 2 /* MONITOR_DEFAULTTONEAREST */);
            var mi = new Win32Interop.MONITORINFO { cbSize = Marshal.SizeOf<Win32Interop.MONITORINFO>() };
            if (!Win32Interop.GetMonitorInfo(mon, ref mi)) return false;

            // Borderless or exclusive fullscreen covers entire monitor rectangle (within 2px tolerance)
            return winRect.Left <= mi.rcMonitor.Left + 2 &&
                   winRect.Top <= mi.rcMonitor.Top + 2 &&
                   winRect.Right >= mi.rcMonitor.Right - 2 &&
                   winRect.Bottom >= mi.rcMonitor.Bottom - 2;
        }
        catch
        {
            return false;
        }
    }

    public static bool IsFocusAssistActive()
    {
        try
        {
            return Win32Interop.SHQueryUserNotificationState(out var state) == 0 &&
                   state == Win32Interop.QUERY_USER_NOTIFICATION_STATE.QUNS_QUIET_TIME;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Detects if a maximized window's top bar sits directly beneath the notch AND the user's cursor
    /// is sweeping horizontally along the top caption/tab strip outside the rest pill or toward title buttons,
    /// so Ripple dodges out of the way rather than obstructing browser tabs or caption buttons.
    /// </summary>
    public static bool ShouldDodgeUnderlyingTitleBar(
        Win32Interop.POINT cursorScreen,
        double notchRestLeftScreen,
        double notchRestRightScreen,
        double notchRestBottomScreen,
        IntPtr rippleHwnd)
    {
        try
        {
            var fg = Win32Interop.GetForegroundWindow();
            if (fg == IntPtr.Zero || fg == rippleHwnd) return false;

            if (!Win32Interop.GetWindowRect(fg, out var fgRect)) return false;
            var mon = Win32Interop.MonitorFromPoint(cursorScreen, 2);
            var mi = new Win32Interop.MONITORINFO { cbSize = Marshal.SizeOf<Win32Interop.MONITORINFO>() };
            if (!Win32Interop.GetMonitorInfo(mon, ref mi)) return false;

            var isMaximizedOrTopAligned = fgRect.Top <= mi.rcWork.Top + 6;
            if (!isMaximizedOrTopAligned) return false;

            // Check if cursor is in the top caption/tab strip (top 38 px of screen)
            // near the notch flanks or approaching caption controls on the right.
            var inTopStrip = cursorScreen.Y >= mi.rcMonitor.Top && cursorScreen.Y <= Math.Max(mi.rcMonitor.Top + 38, notchRestBottomScreen + 4);
            if (!inTopStrip) return false;

            var inFlankApproachZone =
                (cursorScreen.X >= notchRestLeftScreen - 24 && cursorScreen.X < notchRestLeftScreen) ||
                (cursorScreen.X > notchRestRightScreen && cursorScreen.X <= notchRestRightScreen + 24);

            return inFlankApproachZone;
        }
        catch
        {
            return false;
        }
    }
}
