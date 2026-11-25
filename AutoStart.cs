using Microsoft.Win32;

namespace MonitorHUD;

/// <summary>
/// 开机自启：写 HKCU\...\Run，只影响当前用户，不需要管理员权限。
/// 状态以注册表为准（不额外存 config）——用户若用其它工具删了启动项，界面也能如实反映。
/// </summary>
internal static class AutoStart
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "MonitorHUD";

    /// <summary>当前可执行文件的绝对路径（单文件发布下即 exe 自身）。</summary>
    public static string ExePath =>
        Environment.ProcessPath ?? Application.ExecutablePath;

    /// <summary>
    /// 注册表里存在启动项，且指向的就是当前这份 exe 才算"已开启"。
    /// 路径不符（程序被挪过位置）视为未开启，避免勾选框显示与实际行为不一致。
    /// </summary>
    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
                if (key?.GetValue(ValueName) is not string raw) return false;
                var registered = raw.Trim().Trim('"');
                return string.Equals(registered, ExePath, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }
    }

    /// <summary>开启 / 关闭开机自启。失败时返回 false 并给出原因，由调用方回滚勾选状态。</summary>
    public static bool Set(bool enable, out string? error)
    {
        error = null;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key is null)
            {
                error = "无法打开注册表启动项（Run 键不存在）";
                return false;
            }

            if (enable)
            {
                // 路径带引号：路径里有空格时系统才解析得对
                key.SetValue(ValueName, $"\"{ExePath}\"", RegistryValueKind.String);
            }
            else if (key.GetValue(ValueName) is not null)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
