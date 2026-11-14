using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using Ripple.App.Interop;
using Ripple.Core.Events;
using Ripple.Core.Modules;
using FormsPower = System.Windows.Forms.SystemInformation;
using FormsBattery = System.Windows.Forms.PowerLineStatus;

namespace Ripple.App.Services;

public sealed record ProcessResourceInfo(
    int Pid,
    string Name,
    double CpuPercent,
    double MemoryMb);

public sealed record SystemStatsSnapshot(
    double CpuPercent,
    double RamPercent,
    double RamUsedGb,
    double RamTotalGb,
    double DiskActivePercent,
    double NetworkDownMbps,
    double NetworkUpMbps,
    string NetworkAdapterName,
    int BatteryPercent,
    bool IsCharging,
    bool IsBatterySaver,
    bool HasActiveDownload,
    IReadOnlyList<double> CpuHistory,
    IReadOnlyList<double> NetHistory,
    IReadOnlyList<ProcessResourceInfo> TopProcesses);

/// <summary>
/// Adaptive-rate system telemetry (1s when expanded, 5s at rest) collecting CPU, RAM,
/// per-adapter network throughput, battery/charging state, 60s sparklines, and top processes.
/// </summary>
public sealed class SystemMonitorService : IRippleModule, IDisposable
{
    private readonly LiveActivityEventBus _bus;
    private readonly Queue<double> _cpuHistory = new(60);
    private readonly Queue<double> _netHistory = new(60);
    private readonly Dictionary<int, (TimeSpan Cpu, DateTimeOffset At)> _prevProcTimes = [];

    private ulong _prevIdle, _prevKernel, _prevUser;
    private long _prevBytesRx, _prevBytesTx;
    private DateTimeOffset _lastNetSampleAt = DateTimeOffset.MinValue;
    private DateTimeOffset _lastPollAt = DateTimeOffset.MinValue;
    private bool _wasCharging;
    private PerformanceCounter? _diskCounter;

    public SystemMonitorService(LiveActivityEventBus bus)
    {
        _bus = bus;
        _wasCharging = FormsPower.PowerStatus.PowerLineStatus == FormsBattery.Online;
        try
        {
            _diskCounter = new PerformanceCounter("PhysicalDisk", "% Disk Time", "_Total", readOnly: true);
            _diskCounter.NextValue();
        }
        catch
        {
            _diskCounter = null;
        }
        SampleNow();
    }

    public string Id => "system";
    public string Name => "System";
    public string Icon => "⚡";
    public int Priority => 60;
    public bool IsEnabled { get; set; } = true;
    public IReadOnlyCollection<ModulePermission> RequiredPermissions { get; } = [ModulePermission.SystemStats];
    public double PreferredExpandedWidth => 460;
    public double PreferredExpandedHeight => 240;

    public SystemStatsSnapshot Snapshot { get; private set; } = new(0, 0, 0, 16, 0, 0, 0, "Wi-Fi", 100, true, false, false, [], [], []);

    public event Action? Updated;

    public ModuleGlanceState GetGlanceState()
    {
        if (Snapshot.HasActiveDownload)
        {
            return new ModuleGlanceState(true, $"{Snapshot.NetworkDownMbps:F1} MB/s", "↓", "#22D3EE", Pulse: true);
        }

        if (Snapshot.CpuPercent >= 90)
        {
            return new ModuleGlanceState(true, $"{Snapshot.CpuPercent:F0}%", "⚡", "#EF4444", Snapshot.CpuPercent / 100.0, Pulse: true);
        }

        if (!Snapshot.IsCharging && Snapshot.BatteryPercent <= 20)
        {
            return new ModuleGlanceState(true, $"{Snapshot.BatteryPercent}%", "🔋", "#F59E0B", Snapshot.BatteryPercent / 100.0);
        }

        return new ModuleGlanceState(false);
    }

    public void OnTick(DateTimeOffset utcNow, bool isExpanded)
    {
        var intervalSec = isExpanded ? 1.0 : 5.0;
        if ((utcNow - _lastPollAt).TotalSeconds >= intervalSec)
        {
            _lastPollAt = utcNow;
            SampleNow(includeTopProcesses: isExpanded);
        }
    }

    public void SampleNow(bool includeTopProcesses = false)
    {
        var cpu = ReadCpuPercent();
        var (ramPct, ramUsedGb, ramTotalGb) = ReadMemory();
        var diskPct = ReadDiskPercent();
        var (downMbps, upMbps, adapter) = ReadNetworkThroughput();

        var power = FormsPower.PowerStatus;
        var batPct = power.BatteryLifePercent <= 1.0f ? (int)Math.Round(power.BatteryLifePercent * 100) : 100;
        var charging = power.PowerLineStatus == FormsBattery.Online;
        var batterySaver = (int)power.BatteryChargeStatus == 8; // Low/Saver fallback

        if (charging && !_wasCharging)
        {
            _bus.Publish(new LiveActivityPayload(
                Id: "power:charging",
                SourceModuleId: Id,
                Kind: LiveActivityKind.BatteryCharging,
                Title: "Power Connected",
                Subtitle: $"Charging • {batPct}%",
                Icon: "⚡",
                AccentHex: "#34D399",
                Priority: LiveActivityPriority.High,
                Duration: TimeSpan.FromSeconds(3.2),
                Progress: batPct / 100.0,
                CoalesceKey: "power-charging"));
        }
        _wasCharging = charging;

        if (cpu >= 92.0)
        {
            _bus.Publish(new LiveActivityPayload(
                Id: "sys:high-cpu",
                SourceModuleId: Id,
                Kind: LiveActivityKind.SystemAlert,
                Title: $"High CPU Usage ({cpu:F0}%)",
                Subtitle: "Click to inspect top processes",
                Icon: "⚠",
                AccentHex: "#EF4444",
                Priority: LiveActivityPriority.High,
                Duration: TimeSpan.FromSeconds(4),
                CoalesceKey: "sys-cpu-alert"));
        }

        PushRing(_cpuHistory, cpu);
        PushRing(_netHistory, downMbps + upMbps);

        var topProcs = includeTopProcesses ? ReadTopProcesses() : Snapshot.TopProcesses;
        var activeDownload = downMbps >= 1.5;

        Snapshot = new SystemStatsSnapshot(
            cpu,
            ramPct,
            ramUsedGb,
            ramTotalGb,
            diskPct,
            downMbps,
            upMbps,
            adapter,
            batPct,
            charging,
            batterySaver,
            activeDownload,
            _cpuHistory.ToArray(),
            _netHistory.ToArray(),
            topProcs);

        Updated?.Invoke();
    }

    public bool TryEndProcess(int pid, out string message)
    {
        try
        {
            using var proc = Process.GetProcessById(pid);
            var name = proc.ProcessName;
            proc.Kill(entireProcessTree: false);
            message = $"Ended {name} (PID {pid})";
            SampleNow(includeTopProcesses: true);
            return true;
        }
        catch (Exception ex)
        {
            message = $"Could not end PID {pid}: {ex.Message}";
            return false;
        }
    }

    private double ReadCpuPercent()
    {
        if (!Win32Interop.GetSystemTimes(out var idleFt, out var kernelFt, out var userFt))
        {
            return 0;
        }

        var idle = idleFt.ToUInt64();
        var kernel = kernelFt.ToUInt64();
        var user = userFt.ToUInt64();

        if (_prevKernel == 0 && _prevUser == 0)
        {
            _prevIdle = idle;
            _prevKernel = kernel;
            _prevUser = user;
            return 0;
        }

        var idleDelta = idle - _prevIdle;
        var totalDelta = (kernel - _prevKernel) + (user - _prevUser);
        _prevIdle = idle;
        _prevKernel = kernel;
        _prevUser = user;

        if (totalDelta == 0) return 0;
        return Math.Clamp((1.0 - (double)idleDelta / totalDelta) * 100.0, 0.0, 100.0);
    }

    private static (double Pct, double UsedGb, double TotalGb) ReadMemory()
    {
        var mem = new Win32Interop.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<Win32Interop.MEMORYSTATUSEX>() };
        if (!Win32Interop.GlobalMemoryStatusEx(ref mem))
        {
            return (0, 0, 0);
        }

        var totalGb = mem.ullTotalPhys / (1024.0 * 1024.0 * 1024.0);
        var availGb = mem.ullAvailPhys / (1024.0 * 1024.0 * 1024.0);
        return (mem.dwMemoryLoad, Math.Max(0, totalGb - availGb), totalGb);
    }

    private double ReadDiskPercent()
    {
        try
        {
            return _diskCounter is null ? 0 : Math.Clamp(_diskCounter.NextValue(), 0, 100);
        }
        catch
        {
            return 0;
        }
    }

    private (double DownMBs, double UpMBs, string Adapter) ReadNetworkThroughput()
    {
        try
        {
            long rx = 0, tx = 0;
            var adapterName = "Network";
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up ||
                    ni.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                var stats = ni.GetIPv4Statistics();
                rx += stats.BytesReceived;
                tx += stats.BytesSent;
                if (adapterName == "Network")
                {
                    adapterName = ni.Name;
                }
            }

            var now = DateTimeOffset.UtcNow;
            if (_lastNetSampleAt == DateTimeOffset.MinValue)
            {
                _prevBytesRx = rx;
                _prevBytesTx = tx;
                _lastNetSampleAt = now;
                return (0, 0, adapterName);
            }

            var dt = Math.Max(0.25, (now - _lastNetSampleAt).TotalSeconds);
            var downMBs = Math.Max(0, (rx - _prevBytesRx) / (1024.0 * 1024.0) / dt);
            var upMBs = Math.Max(0, (tx - _prevBytesTx) / (1024.0 * 1024.0) / dt);

            _prevBytesRx = rx;
            _prevBytesTx = tx;
            _lastNetSampleAt = now;
            return (downMBs, upMBs, adapterName);
        }
        catch
        {
            return (0, 0, "Offline");
        }
    }

    private IReadOnlyList<ProcessResourceInfo> ReadTopProcesses()
    {
        var now = DateTimeOffset.UtcNow;
        var processors = Math.Max(1, Environment.ProcessorCount);
        var list = new List<ProcessResourceInfo>();
        var livePids = new HashSet<int>();

        foreach (var p in Process.GetProcesses())
        {
            try
            {
                livePids.Add(p.Id);
                if (p.Id <= 4) continue;
                var memMb = p.WorkingSet64 / (1024.0 * 1024.0);
                var cpuTime = p.TotalProcessorTime;
                double cpuPct = 0;

                if (_prevProcTimes.TryGetValue(p.Id, out var prev))
                {
                    var dtMs = (now - prev.At).TotalMilliseconds;
                    if (dtMs > 50)
                    {
                        cpuPct = Math.Clamp((cpuTime - prev.Cpu).TotalMilliseconds / (dtMs * processors) * 100.0, 0, 100);
                    }
                }
                _prevProcTimes[p.Id] = (cpuTime, now);

                list.Add(new ProcessResourceInfo(p.Id, p.ProcessName, cpuPct, memMb));
            }
            catch
            {
                // System/protected process access denied
            }
            finally
            {
                p.Dispose();
            }
        }

        // Prevent dictionary growth over 24-hour soak runs
        foreach (var stalePid in _prevProcTimes.Keys.Where(id => !livePids.Contains(id)).ToList())
        {
            _prevProcTimes.Remove(stalePid);
        }

        return list.OrderByDescending(x => x.CpuPercent)
                   .ThenByDescending(x => x.MemoryMb)
                   .Take(3)
                   .ToList();
    }

    private static void PushRing(Queue<double> q, double val)
    {
        if (q.Count >= 60) q.Dequeue();
        q.Enqueue(val);
    }

    public void Dispose()
    {
        _diskCounter?.Dispose();
    }
}
