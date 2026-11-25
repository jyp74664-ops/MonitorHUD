using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace MonitorHUD;

/// <summary>一次采样的完整数据快照。</summary>
public sealed class Sample
{
    public DateTime Time { get; init; } = DateTime.Now;

    // CPU
    public double CpuPercent { get; init; }
    public double? CpuTemp { get; init; }
    public double? BoardTemp { get; init; }   // 主板（第二热区，启发式）

    // 内存
    public double RamPercent { get; init; }
    public double RamUsedGb { get; init; }
    public double RamTotalGb { get; init; }

    // 显卡（nvidia-smi 不可用时为 null）
    public double? GpuUtil { get; init; }
    public double? GpuMemUsedMb { get; init; }
    public double? GpuMemTotalMb { get; init; }
    public double? GpuTemp { get; init; }

    // 网络（MB/s）
    public double NetDown { get; init; }
    public double NetUp { get; init; }
}

/// <summary>
/// 后台采样服务：CPU 用 GetSystemTimes，内存用 GlobalMemoryStatusEx，
/// 网速用网卡累计字节数差值，显卡走 nvidia-smi 子进程，CPU 温度走 WMI 热区（仅管理员）。
/// 全部避开本地化的性能计数器名称，中文系统无兼容问题。
/// </summary>
public sealed class MonitorService : IDisposable
{
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public Sample Latest { get; private set; } = new();
    public int IntervalMs { get; set; } = 1000;

    public event Action<Sample>? SampleReady;

    public void Start()
    {
        if (_loop is { IsCompleted: false }) return;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_cts.Token));
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _loop?.Wait(1500); } catch { /* 取消异常忽略 */ }
        _cts?.Dispose();
        _cts = null;
        _loop = null;
    }

    public void Dispose() => Stop();

    // ---------------------------------------------------------------- 循环

    private async Task LoopAsync(CancellationToken ct)
    {
        var cpuPrev = CpuTimes.Read();
        var netPrev = NetCounters.Snapshot();
        var prevTime = DateTime.UtcNow;
        var lastGpuOk = DateTime.MinValue;
        (double util, double memUsed, double memTotal, double temp)? gpu = null;
        double? cpuTemp = null;
        double? boardTemp = null;
        var lastTempProbe = DateTime.MinValue;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(IntervalMs, ct).ConfigureAwait(false);

                var now = DateTime.UtcNow;
                var dt = (now - prevTime).TotalSeconds;
                if (dt < 0.2) continue;
                prevTime = now;

                // CPU
                var cpuCur = CpuTimes.Read();
                double cpuPct = CpuTimes.Percent(cpuPrev, cpuCur);
                cpuPrev = cpuCur;

                // 内存
                var mem = MemoryInfo.Read();

                // 网络
                var netCur = NetCounters.Snapshot();
                var (down, up) = NetCounters.Rate(netPrev, netCur, dt);
                netPrev = netCur;

                // 显卡：至少 2 秒查一次 nvidia-smi，失败时保留旧值 30 秒
                if ((now - lastGpuOk).TotalSeconds >= 2)
                {
                    var g = NvSmi.Query();
                    if (g is not null) { gpu = g; lastGpuOk = now; }
                    else if ((now - lastGpuOk).TotalSeconds > 30) gpu = null;
                }

                // CPU/主板温度：仅管理员可读；每 5 秒探一次
                // WMI 热区无官方语义，启发式：温度最高的热区当 CPU，第二高当主板
                if (Elevated.IsAdmin && (now - lastTempProbe).TotalSeconds >= 5)
                {
                    lastTempProbe = now;
                    var zones = CpuTempReader.ReadZones();   // 已按温度降序
                    cpuTemp = zones.Count > 0 ? zones[0] : null;
                    boardTemp = zones.Count > 1 ? zones[1] : null;
                }

                var s = new Sample
                {
                    CpuPercent = cpuPct,
                    CpuTemp = cpuTemp,
                    BoardTemp = boardTemp,
                    RamPercent = mem.pct,
                    RamUsedGb = mem.usedGb,
                    RamTotalGb = mem.totalGb,
                    GpuUtil = gpu?.util,
                    GpuMemUsedMb = gpu?.memUsed,
                    GpuMemTotalMb = gpu?.memTotal,
                    GpuTemp = gpu?.temp,
                    NetDown = down,
                    NetUp = up,
                };
                Latest = s;
                SampleReady?.Invoke(s);
            }
            catch (OperationCanceledException) { break; }
            catch { /* 单轮失败不影响循环 */ }
        }
    }

    // ------------------------------------------------------------ CPU 占用

    private static class CpuTimes
    {
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

        public static (long idle, long kernel, long user) Read()
        {
            GetSystemTimes(out var idle, out var kernel, out var user);
            return (idle, kernel, user);
        }

        public static double Percent((long i, long k, long u) a, (long i, long k, long u) b)
        {
            long dK = b.k - a.k, dU = b.u - a.u, dI = b.i - a.i;
            long total = dK + dU;
            if (total <= 0) return 0;
            return Math.Clamp((1.0 - (double)dI / total) * 100.0, 0, 100);
        }
    }

    // ---------------------------------------------------------------- 内存

    private static class MemoryInfo
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buf);

        public static (double pct, double usedGb, double totalGb) Read()
        {
            var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (!GlobalMemoryStatusEx(ref m)) return (0, 0, 0);
            double total = m.ullTotalPhys / 1073741824.0;
            double used = (m.ullTotalPhys - m.ullAvailPhys) / 1073741824.0;
            return (m.dwMemoryLoad, used, total);
        }
    }

    // ---------------------------------------------------------------- 网速

    private static class NetCounters
    {
        public static Dictionary<string, (long rx, long tx)> Snapshot()
        {
            var dict = new Dictionary<string, (long, long)>();
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType is NetworkInterfaceType.Loopback
                        or NetworkInterfaceType.Tunnel) continue;
                    var st = ni.GetIPv4Statistics();
                    dict[ni.Id] = (st.BytesReceived, st.BytesSent);
                }
            }
            catch { }
            return dict;
        }

        public static (double down, double up) Rate(
            Dictionary<string, (long rx, long tx)> prev,
            Dictionary<string, (long rx, long tx)> cur, double dt)
        {
            double down = 0, up = 0;
            foreach (var (id, (rx, tx)) in cur)
            {
                if (prev.TryGetValue(id, out var p) && rx >= p.rx && tx >= p.tx)
                {
                    down += rx - p.rx;
                    up += tx - p.tx;
                }
            }
            return (down / dt / 1048576.0, up / dt / 1048576.0);
        }
    }

    // ------------------------------------------------------------ 子进程

    /// <summary>
    /// 跑一个外部命令并取回 stdout，带真正生效的超时。
    ///
    /// 两条铁律（原实现踩过的坑）：
    /// 1) 只要 Redirect 了 stdout / stderr，就**必须都读**。只读 stdout 时，子进程写
    ///    stderr 管道写满（约 4KB）就会阻塞在写，父进程永远等不到 EOF —— 死锁，
    ///    采样线程永久冻结，表现为界面不崩但数字不动。
    /// 2) 超时必须在读之前生效。同步 ReadToEnd() 会一直阻塞到 EOF，写在它后面的
    ///    WaitForExit(timeout) 根本轮不到执行，等于没有超时。
    ///
    /// 这里改成异步读 + Task.WaitAll(超时)，超时直接杀进程树，保证调用方一定拿回控制权。
    /// </summary>
    private static string? RunAndCapture(string fileName, string arguments, int timeoutMs)
    {
        Process? p = null;
        try
        {
            p = Process.Start(new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            });
            if (p is null) return null;

            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();   // 不读 stderr 就是等着死锁

            if (!Task.WaitAll([outTask, errTask], timeoutMs))
            {
                // 超时：连子进程树一起杀掉，避免残留 nvidia-smi / powershell 僵尸进程
                try { p.Kill(entireProcessTree: true); } catch { /* 已退出 */ }
                return null;
            }

            try { p.WaitForExit(1000); } catch { /* 已退出 */ }   // 句柄释放后再 Dispose
            return outTask.Result;
        }
        catch { return null; }
        finally { p?.Dispose(); }
    }

    // ------------------------------------------------------------ 显卡查询

    private static class NvSmi
    {
        private static readonly string[] Candidates =
        {
            "nvidia-smi",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                @"System32\nvidia-smi.exe"),
            @"C:\Program Files\NVIDIA Corporation\NVSMI\nvidia-smi.exe",
        };

        public static (double util, double memUsed, double memTotal, double temp)? Query()
        {
            foreach (var exe in Candidates)
            {
                var outp = RunAndCapture(exe,
                    "--query-gpu=utilization.gpu,memory.used,memory.total,temperature.gpu --format=csv,noheader,nounits",
                    timeoutMs: 3000);
                if (outp is null) continue;

                var line = outp.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (line is null) continue;
                var parts = line.Split(',');
                if (parts.Length < 4) continue;
                return (ParseD(parts[0]), ParseD(parts[1]), ParseD(parts[2]), ParseD(parts[3]));
            }
            return null;
        }

        private static double ParseD(string s) =>
            double.TryParse(s.Trim(), out var v) ? v : 0;
    }

    // ------------------------------------------------------------ CPU/主板温度

    private static class CpuTempReader
    {
        // WMI 热区需要管理员；用 PowerShell 子进程读，避免引入 System.Management 依赖。
        // 返回所有热区温度（℃），按温度降序。约定：[0]≈CPU，[1]≈主板（启发式）。
        public static List<double> ReadZones()
        {
            var result = new List<double>();
            var outp = RunAndCapture("powershell.exe",
                "-NoProfile -Command " +
                "\"Get-CimInstance -Namespace root/wmi -ClassName MSAcpi_ThermalZoneTemperature " +
                "-EA Stop | ForEach-Object { '{0}={1}' -f $_.InstanceName, $_.CurrentTemperature }\"",
                timeoutMs: 5000);
            if (outp is null) return result;

            foreach (var line in outp.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var idx = line.LastIndexOf('=');
                if (idx <= 0) continue;
                if (double.TryParse(line[(idx + 1)..].Trim(), out var k10) && k10 > 0)
                {
                    var c = k10 / 10.0 - 273.15;
                    if (c is > 0 and < 120) result.Add(Math.Round(c, 0));
                }
            }
            result.Sort((a, b) => b.CompareTo(a));
            return result;
        }
    }

    // ------------------------------------------------------------ 权限检测

    private static class Elevated
    {
        public static bool IsAdmin { get; } = Check();

        private static bool Check()
        {
            using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            var pr = new System.Security.Principal.WindowsPrincipal(id);
            return pr.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
    }
}
