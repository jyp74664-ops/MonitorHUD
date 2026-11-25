using System.Diagnostics;
using System.Runtime.InteropServices;

namespace MonitorHUD;

internal static class Program
{
    // 会话内唯一：同一用户重复双击 exe 时不再开第二个进程/第二根悬浮条
    private const string SingleInstanceMutex = @"Local\MonitorHUD_SingleInstance_7E4A9C1B";
    private const int WmShowExisting = 0x8001;

    /// <summary>重启时由旧进程传给新进程：新进程先等这个 PID 退出，再抢单实例 Mutex。</summary>
    private const string RestartAfterArg = "--restart-after";

    [STAThread]
    private static void Main(string[] args)
    {
        // 必须在抢 Mutex 之前等待：旧进程不退，Mutex 一直被它持有，
        // 新进程会误判成"已有实例"而自己退出，重启就变成了直接关掉。
        WaitForPredecessor(args);

        ApplicationConfiguration.Initialize();

        using var mutex = new Mutex(true, SingleInstanceMutex, out bool createdNew);
        if (!createdNew)
        {
            // 已有实例（含最小化到托盘）→ 唤出它的主面板，自己直接退出
            SignalExistingInstance();
            return;
        }

        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) =>
            MessageBox.Show($"出错了：{e.Exception.Message}", "MonitorHUD",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        Application.Run(new MainForm());
        GC.KeepAlive(mutex);
    }

    /// <summary>
    /// 解析 --restart-after &lt;pid&gt;：等旧进程完全终止（句柄全关 = Mutex 已释放）。
    /// 没有该参数时立即返回，不影响正常启动。
    /// </summary>
    private static void WaitForPredecessor(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (!string.Equals(args[i], RestartAfterArg, StringComparison.Ordinal)) continue;
            if (!int.TryParse(args[i + 1], out int pid)) return;

            try
            {
                using var old = Process.GetProcessById(pid);
                // 上限 8 秒兜底：旧进程卡住也不能让新进程永久不启动
                old.WaitForExit(8000);
            }
            catch
            {
                // 旧进程已经不存在（或被系统回收），直接进入正常启动
            }
            return;
        }
    }

    private static void SignalExistingInstance()
    {
        try
        {
            // 主面板 Hide() 后窗口仍在，FindWindow 按标题能找到隐藏窗口
            var hwnd = FindWindow(null, "MonitorHUD 硬件监控");
            if (hwnd != IntPtr.Zero)
                PostMessage(hwnd, WmShowExisting, IntPtr.Zero, IntPtr.Zero);
        }
        catch { /* 找不到也不新开实例 */ }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string? lpClassName, string lpWindowName);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
}
