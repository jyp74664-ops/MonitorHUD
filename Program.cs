using System.Runtime.InteropServices;

namespace MonitorHUD;

internal static class Program
{
    // 会话内唯一：同一用户重复双击 exe 时不再开第二个进程/第二根悬浮条
    private const string SingleInstanceMutex = @"Local\MonitorHUD_SingleInstance_7E4A9C1B";
    private const int WmShowExisting = 0x8001;

    [STAThread]
    private static void Main()
    {
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
