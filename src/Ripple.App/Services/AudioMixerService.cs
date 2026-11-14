using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Ripple.App.Services;

public sealed record AudioAppSessionInfo(
    string SessionId,
    int ProcessId,
    string AppName,
    double Volume,
    bool IsMuted,
    double PeakLevel,
    bool IsActive = false);

public sealed record AudioDeviceInfo(
    string Id,
    string Name,
    bool IsDefault);

/// <summary>
/// Direct WASAPI / Core Audio COM integration for master volume, output device enumeration,
/// mic mute toggle, live audio peak levels, and per-application volume mixer without external dependencies.
/// </summary>
public sealed class AudioMixerService : IDisposable
{
    private const int CLSCTX_ALL = 23;
    private IMMDeviceEnumerator? _enumerator;
    private bool _duckOthersActive;
    private readonly Dictionary<string, float> _preDuckVolumes = [];

    public AudioMixerService()
    {
        try
        {
            var type = Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"));
            if (type is not null)
            {
                _enumerator = Activator.CreateInstance(type) as IMMDeviceEnumerator;
            }
        }
        catch
        {
            _enumerator = null;
        }
    }

    public bool IsAvailable => _enumerator is not null;
    public bool IsDuckOthersActive => _duckOthersActive;

    public (double Volume, bool IsMuted) GetMasterVolume()
    {
        if (_enumerator is null) return (0.5, false);
        try
        {
            _enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out var device);
            if (device is null) return (0.5, false);
            try
            {
                var iid = typeof(IAudioEndpointVolume).GUID;
                device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out var obj);
                if (obj is IAudioEndpointVolume vol)
                {
                    vol.GetMasterVolumeLevelScalar(out var level);
                    vol.GetMute(out var mute);
                    Marshal.ReleaseComObject(vol);
                    return (Math.Clamp(level, 0.0, 1.0), mute);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(device);
            }
        }
        catch
        {
            // Fallback silently when no audio endpoint is active
        }
        return (0.5, false);
    }

    public void SetMasterVolume(double scalar)
    {
        if (_enumerator is null) return;
        try
        {
            _enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out var device);
            if (device is null) return;
            try
            {
                var iid = typeof(IAudioEndpointVolume).GUID;
                device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out var obj);
                if (obj is IAudioEndpointVolume vol)
                {
                    var guid = Guid.Empty;
                    vol.SetMasterVolumeLevelScalar((float)Math.Clamp(scalar, 0.0, 1.0), ref guid);
                    Marshal.ReleaseComObject(vol);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(device);
            }
        }
        catch { }
    }

    public void ToggleMasterMute()
    {
        if (_enumerator is null) return;
        try
        {
            _enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out var device);
            if (device is null) return;
            try
            {
                var iid = typeof(IAudioEndpointVolume).GUID;
                device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out var obj);
                if (obj is IAudioEndpointVolume vol)
                {
                    vol.GetMute(out var mute);
                    var guid = Guid.Empty;
                    vol.SetMute(!mute, ref guid);
                    Marshal.ReleaseComObject(vol);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(device);
            }
        }
        catch { }
    }

    public bool ToggleMicrophoneMute()
    {
        if (_enumerator is null) return false;
        try
        {
            _enumerator.GetDefaultAudioEndpoint(EDataFlow.eCapture, ERole.eCommunications, out var device);
            if (device is null) return false;
            try
            {
                var iid = typeof(IAudioEndpointVolume).GUID;
                device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out var obj);
                if (obj is IAudioEndpointVolume vol)
                {
                    vol.GetMute(out var mute);
                    var guid = Guid.Empty;
                    var next = !mute;
                    vol.SetMute(next, ref guid);
                    Marshal.ReleaseComObject(vol);
                    return next;
                }
            }
            finally
            {
                Marshal.ReleaseComObject(device);
            }
        }
        catch { }
        return false;
    }

    public double GetMasterPeakLevel()
    {
        if (_enumerator is null) return 0.0;
        try
        {
            _enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out var device);
            if (device is null) return 0.0;
            try
            {
                var iid = typeof(IAudioMeterInformation).GUID;
                device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out var obj);
                if (obj is IAudioMeterInformation meter)
                {
                    meter.GetPeakValue(out var peak);
                    Marshal.ReleaseComObject(meter);
                    return Math.Clamp(peak, 0.0, 1.0);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(device);
            }
        }
        catch { }
        return 0.0;
    }

    public IReadOnlyList<AudioDeviceInfo> GetRenderDevices()
    {
        var list = new List<AudioDeviceInfo>();
        if (_enumerator is null) return list;
        try
        {
            string? defaultId = null;
            try
            {
                _enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out var defDev);
                if (defDev is not null)
                {
                    defDev.GetId(out defaultId);
                    Marshal.ReleaseComObject(defDev);
                }
            }
            catch { }

            _enumerator.EnumAudioEndpoints(EDataFlow.eRender, 0x00000001 /* ACTIVE */, out var col);
            if (col is null) return list;
            try
            {
                col.GetCount(out var count);
                for (uint i = 0; i < count; i++)
                {
                    col.Item(i, out var dev);
                    if (dev is null) continue;
                    try
                    {
                        dev.GetId(out var id);
                        var name = ReadFriendlyName(dev) ?? $"Output Device {i + 1}";
                        list.Add(new AudioDeviceInfo(id ?? i.ToString(), name, id == defaultId));
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(dev);
                    }
                }
            }
            finally
            {
                Marshal.ReleaseComObject(col);
            }
        }
        catch { }
        return list;
    }

    public IReadOnlyList<AudioAppSessionInfo> GetAppSessions()
    {
        var sessions = new List<AudioAppSessionInfo>();
        if (_enumerator is null) return sessions;

        try
        {
            _enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out var device);
            if (device is null) return sessions;
            try
            {
                var iidMgr = typeof(IAudioSessionManager2).GUID;
                device.Activate(ref iidMgr, CLSCTX_ALL, IntPtr.Zero, out var mgrObj);
                if (mgrObj is not IAudioSessionManager2 mgr) return sessions;

                try
                {
                    mgr.GetSessionEnumerator(out var sessionEnum);
                    if (sessionEnum is null) return sessions;
                    try
                    {
                        sessionEnum.GetCount(out var count);
                        for (var i = 0; i < count; i++)
                        {
                            sessionEnum.GetSession(i, out var ctl);
                            if (ctl is null) continue;
                            try
                            {
                                if (ctl is not IAudioSessionControl2 ctl2) continue;
                                ctl2.GetState(out var state);
                                if (state == 2) continue; // AudioSessionStateExpired

                                ctl2.GetProcessId(out var pid);
                                ctl2.GetSessionInstanceIdentifier(out var sessionId);
                                ctl2.GetDisplayName(out var disp);

                                var appName = ResolveAppName((int)pid, disp);
                                if (string.IsNullOrWhiteSpace(appName)) continue;

                                float vol = 1f;
                                bool muted = false;
                                if (ctl is ISimpleAudioVolume sav)
                                {
                                    sav.GetMasterVolume(out vol);
                                    sav.GetMute(out muted);
                                }

                                float peak = 0f;
                                if (ctl is IAudioMeterInformation meter)
                                {
                                    meter.GetPeakValue(out peak);
                                }

                                sessions.Add(new AudioAppSessionInfo(
                                    sessionId ?? pid.ToString(),
                                    (int)pid,
                                    appName,
                                    Math.Clamp(vol, 0.0, 1.0),
                                    muted,
                                    Math.Clamp(peak, 0.0, 1.0),
                                    IsActive: state == 1));
                            }
                            finally
                            {
                                Marshal.ReleaseComObject(ctl);
                            }
                        }
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(sessionEnum);
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(mgr);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(device);
            }
        }
        catch { }

        return sessions
            .GroupBy(s => s.ProcessId)
            .Select(g => g.OrderByDescending(x => x.IsActive).ThenByDescending(x => x.PeakLevel).First())
            .OrderByDescending(s => s.PeakLevel > 0.005 || s.IsActive)
            .ThenBy(s => s.ProcessId == 0)
            .ThenBy(s => s.AppName)
            .Take(10)
            .ToList();
    }

    public void SetAppVolume(int processId, double scalar)
    {
        MutateSession(processId, sav =>
        {
            var g = Guid.Empty;
            sav.SetMasterVolume((float)Math.Clamp(scalar, 0.0, 1.0), ref g);
        });
    }

    public void ToggleAppMute(int processId)
    {
        MutateSession(processId, sav =>
        {
            sav.GetMute(out var muted);
            var g = Guid.Empty;
            sav.SetMute(!muted, ref g);
        });
    }

    public void ToggleDuckOthers(int? focusPid = null)
    {
        var sessions = GetAppSessions();
        if (!_duckOthersActive)
        {
            _preDuckVolumes.Clear();
            foreach (var s in sessions)
            {
                if (focusPid is not null && s.ProcessId == focusPid) continue;
                _preDuckVolumes[s.SessionId] = (float)s.Volume;
                SetAppVolume(s.ProcessId, Math.Min(s.Volume, 0.2));
            }
            _duckOthersActive = true;
        }
        else
        {
            foreach (var s in sessions)
            {
                if (_preDuckVolumes.TryGetValue(s.SessionId, out var prev))
                {
                    SetAppVolume(s.ProcessId, prev);
                }
            }
            _preDuckVolumes.Clear();
            _duckOthersActive = false;
        }
    }

    private void MutateSession(int processId, Action<ISimpleAudioVolume> action)
    {
        if (_enumerator is null) return;
        try
        {
            _enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out var device);
            if (device is null) return;
            try
            {
                var iidMgr = typeof(IAudioSessionManager2).GUID;
                device.Activate(ref iidMgr, CLSCTX_ALL, IntPtr.Zero, out var mgrObj);
                if (mgrObj is not IAudioSessionManager2 mgr) return;
                try
                {
                    mgr.GetSessionEnumerator(out var sessionEnum);
                    if (sessionEnum is null) return;
                    try
                    {
                        sessionEnum.GetCount(out var count);
                        for (var i = 0; i < count; i++)
                        {
                            sessionEnum.GetSession(i, out var ctl);
                            if (ctl is null) continue;
                            try
                            {
                                if (ctl is IAudioSessionControl2 ctl2)
                                {
                                    ctl2.GetProcessId(out var pid);
                                    if ((int)pid == processId && ctl is ISimpleAudioVolume sav)
                                    {
                                        action(sav);
                                    }
                                }
                            }
                            finally
                            {
                                Marshal.ReleaseComObject(ctl);
                            }
                        }
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(sessionEnum);
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(mgr);
                }
            }
            finally
            {
                Marshal.ReleaseComObject(device);
            }
        }
        catch { }
    }

    private static string ResolveAppName(int pid, string? fallbackDisplay)
    {
        if (pid == 0) return "System Sounds";
        try
        {
            using var proc = Process.GetProcessById(pid);
            var name = proc.ProcessName;
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            {
                name = name[..^4];
            }

            return name.Length > 0 && char.IsLower(name[0])
                ? char.ToUpperInvariant(name[0]) + name[1..]
                : name;
        }
        catch
        {
            return string.IsNullOrWhiteSpace(fallbackDisplay) ? $"PID {pid}" : fallbackDisplay;
        }
    }

    private static string? ReadFriendlyName(IMMDevice device)
    {
        try
        {
            device.OpenPropertyStore(0 /* STGM_READ */, out var store);
            if (store is null) return null;
            try
            {
                var pkey = new PROPERTYKEY
                {
                    fmtid = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"),
                    pid = 14
                };
                store.GetValue(ref pkey, out var propVar);
                return propVar.vt == 31 ? Marshal.PtrToStringUni(propVar.pwszVal) : null;
            }
            finally
            {
                Marshal.ReleaseComObject(store);
            }
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_enumerator is not null)
        {
            try { Marshal.ReleaseComObject(_enumerator); } catch { }
            _enumerator = null;
        }
    }

    private enum EDataFlow { eRender = 0, eCapture = 1, eAll = 2 }
    private enum ERole { eConsole = 0, eMultimedia = 1, eCommunications = 2 }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPERTYKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct PROPVARIANT
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr pwszVal;
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(EDataFlow dataFlow, uint dwStateMask, out IMMDeviceCollection ppDevices);
        [PreserveSig] int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice ppEndpoint);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string pwstrId, out IMMDevice ppDevice);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr pClient);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr pClient);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint pcDevices);
        [PreserveSig] int Item(uint nDevice, out IMMDevice ppDevice);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams, [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
        [PreserveSig] int OpenPropertyStore(int stgmAccess, out IPropertyStore ppProperties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string ppstrId);
        [PreserveSig] int GetState(out uint pdwState);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint cProps);
        [PreserveSig] int GetAt(uint iProp, out PROPERTYKEY pkey);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT pv);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr pNotify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr pNotify);
        [PreserveSig] int GetChannelCount(out uint pnChannelCount);
        [PreserveSig] int SetMasterVolumeLevel(float fLevelDB, ref Guid pguidEventContext);
        [PreserveSig] int SetMasterVolumeLevelScalar(float fLevel, ref Guid pguidEventContext);
        [PreserveSig] int GetMasterVolumeLevel(out float pfLevelDB);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float pfLevel);
        [PreserveSig] int SetChannelVolumeLevel(uint nChannel, float fLevelDB, ref Guid pguidEventContext);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint nChannel, float fLevel, ref Guid pguidEventContext);
        [PreserveSig] int GetChannelVolumeLevel(uint nChannel, out float pfLevelDB);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint nChannel, out float pfLevel);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool bMute, ref Guid pguidEventContext);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool pbMute);
    }

    [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionManager2
    {
        [PreserveSig] int GetAudioSessionControl(ref Guid AudioSessionGuid, uint StreamFlags, out IntPtr SessionControl);
        [PreserveSig] int GetSimpleAudioVolume(ref Guid AudioSessionGuid, uint StreamFlags, out IntPtr AudioVolume);
        [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator SessionEnum);
    }

    [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionEnumerator
    {
        [PreserveSig] int GetCount(out int SessionCount);
        [PreserveSig] int GetSession(int SessionCount, out IAudioSessionControl Session);
    }

    [ComImport, Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl
    {
        [PreserveSig] int GetState(out int pRetVal);
        [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
    }

    [ComImport, Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioSessionControl2
    {
        [PreserveSig] int GetState(out int pRetVal);
        [PreserveSig] int GetDisplayName([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
        [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string Value, ref Guid EventContext);
        [PreserveSig] int GetIconPath([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
        [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string Value, ref Guid EventContext);
        [PreserveSig] int GetGroupingParam(out Guid pRetVal);
        [PreserveSig] int SetGroupingParam(ref Guid Override, ref Guid EventContext);
        [PreserveSig] int RegisterAudioSessionNotification(IntPtr NewNotifications);
        [PreserveSig] int UnregisterAudioSessionNotification(IntPtr NewNotifications);
        [PreserveSig] int GetSessionIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
        [PreserveSig] int GetSessionInstanceIdentifier([MarshalAs(UnmanagedType.LPWStr)] out string pRetVal);
        [PreserveSig] int GetProcessId(out uint pRetVal);
    }

    [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISimpleAudioVolume
    {
        [PreserveSig] int SetMasterVolume(float fLevel, ref Guid EventContext);
        [PreserveSig] int GetMasterVolume(out float pfLevel);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool bMute, ref Guid EventContext);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool pbMute);
    }

    [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioMeterInformation
    {
        [PreserveSig] int GetPeakValue(out float pfPeak);
    }
}
