using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace MonitorHUD;

/// <summary>悬浮条要显示的条目。</summary>
[Flags]
public enum OverlayItem
{
    None = 0,
    Cpu = 1 << 0,
    Ram = 1 << 1,
    GpuUtil = 1 << 2,
    GpuMem = 1 << 3,
    GpuTemp = 1 << 4,
    CpuTemp = 1 << 5,
    Net = 1 << 6,
    BoardTemp = 1 << 7,
}

/// <summary>悬浮条外观样式。</summary>
public enum OverlayStyle
{
    Text = 0,    // 一行紧凑文字：彩色标签 + 绿色数值
    Cards = 1,   // 小卡片：每项一卡片，彩色标题 + 绿色数值，内存/显存带进度条
}

/// <summary>
/// 桌面顶部悬浮条：无边框、置顶、半透明黑底。
/// 标签用各指标卡片的配色，数值统一用绿色（当前默认绿）。
/// 宽高随文字与字号自适应；未锁定时可拖动 / 右键菜单；锁定后整条鼠标穿透。
/// </summary>
public sealed class OverlayForm : Form
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x20;
    private const int WS_EX_LAYERED = 0x80000;
    private const int WS_EX_TOOLWINDOW = 0x80;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    // ---- 虚拟桌面：固定到所有桌面 ----
    // 任务视图里"在所有桌面上显示"走的是 IVirtualDesktopPinnedApps.PinView，
    // 不是窗口扩展样式，也不是 IPropertyStore。IID 用 26100+ 系列（本机 26300 实测通过）。

    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IServiceProvider
    {
        [PreserveSig] int QueryService(ref Guid guidService, ref Guid riid, out IntPtr ppvObject);
    }

    [ComImport, Guid("1841C6D7-4F9D-42C0-AF41-8747538F10E5"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationViewCollection
    {
        [PreserveSig] int GetViews(out IntPtr array);
        [PreserveSig] int GetViewsByZOrder(out IntPtr array);
        [PreserveSig] int GetViewsByAppUserModelId([MarshalAs(UnmanagedType.LPWStr)] string id, out IntPtr array);
        [PreserveSig] int GetViewForHwnd(IntPtr hwnd, out IntPtr view);
        [PreserveSig] int GetViewForApplication(IntPtr application, out IntPtr view);
        [PreserveSig] int GetViewForAppUserModelId([MarshalAs(UnmanagedType.LPWStr)] string id, out IntPtr view);
        [PreserveSig] int GetViewInFocus(out IntPtr view);
        [PreserveSig] int RefreshCollection();
    }

    [ComImport, Guid("4CE81583-1E4C-4632-A621-07A53543148F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IVirtualDesktopPinnedApps
    {
        [PreserveSig] int IsAppIdPinned([MarshalAs(UnmanagedType.LPWStr)] string appId);
        [PreserveSig] int PinAppID([MarshalAs(UnmanagedType.LPWStr)] string appId);
        [PreserveSig] int UnpinAppID([MarshalAs(UnmanagedType.LPWStr)] string appId);
        [PreserveSig] int IsViewPinned(IntPtr view, out short pinned);
        [PreserveSig] int PinView(IntPtr view);
        [PreserveSig] int UnpinView(IntPtr view);
    }

    private static readonly Guid ImmersiveShellClsid = new("C2F03A33-21F5-47FA-B4BB-156362A2F239");
    private static readonly Guid ViewCollectionIid = new("1841C6D7-4F9D-42C0-AF41-8747538F10E5");
    private static readonly Guid PinnedAppsClsid = new("B5A399E7-1C87-46B8-88E9-FC5747B171BD");
    private static readonly Guid PinnedAppsIid = new("4CE81583-1E4C-4632-A621-07A53543148F");

    private static IntPtr QueryService(Guid service, Guid iid)
    {
        var shellType = Type.GetTypeFromCLSID(ImmersiveShellClsid)
            ?? throw new InvalidOperationException("ImmersiveShell 不存在");
        var shell = (IServiceProvider)Activator.CreateInstance(shellType)!;
        try
        {
            int hr = shell.QueryService(ref service, ref iid, out var ptr);
            if (hr < 0) Marshal.ThrowExceptionForHR(hr);
            if (ptr == IntPtr.Zero) throw new InvalidOperationException("QueryService 返回空");
            return ptr;
        }
        finally { Marshal.ReleaseComObject(shell); }
    }

    private static void PinLog(string msg)
    {
        try
        {
            var path = Path.Combine(Path.GetTempPath(), "MonitorHUD-pin.log");
            File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff} {msg}{Environment.NewLine}");
        }
        catch { /* 日志失败不影响功能 */ }
    }

    /// <summary>
    /// PinView 返回 S_OK 不代表真的固定成功（窗口刚显示时 view 可能还没登记好）。
    /// 必须回读 IsViewPinned == 1 才算成功，否则继续重试。
    /// </summary>
    private static bool TryPinToAllVirtualDesktops(IntPtr hwnd, out string detail)
    {
        object? collObj = null, appsObj = null;
        IntPtr viewPtr = IntPtr.Zero;
        detail = "";
        try
        {
            var collPtr = QueryService(ViewCollectionIid, ViewCollectionIid);
            collObj = Marshal.GetObjectForIUnknown(collPtr);
            Marshal.Release(collPtr);   // 引用转交 RCW
            var coll = (IApplicationViewCollection)collObj;
            int hrView = coll.GetViewForHwnd(hwnd, out viewPtr);
            if (hrView < 0 || viewPtr == IntPtr.Zero)
            {
                detail = $"GetViewForHwnd=0x{hrView:X8}";
                return false;
            }

            var appsPtr = QueryService(PinnedAppsClsid, PinnedAppsIid);
            appsObj = Marshal.GetObjectForIUnknown(appsPtr);
            Marshal.Release(appsPtr);
            var apps = (IVirtualDesktopPinnedApps)appsObj;

            int hrPin = apps.PinView(viewPtr);
            int hrRead = apps.IsViewPinned(viewPtr, out short pinned);
            detail = $"view=0x{viewPtr:X} PinView=0x{hrPin:X8} IsViewPinned=0x{hrRead:X8}/{pinned}";
            return hrPin >= 0 && hrRead >= 0 && pinned == 1;
        }
        catch (Exception ex) { detail = "EX:" + ex.Message; return false; }
        finally
        {
            if (viewPtr != IntPtr.Zero) Marshal.Release(viewPtr);
            if (appsObj is not null) Marshal.ReleaseComObject(appsObj);
            if (collObj is not null) Marshal.ReleaseComObject(collObj);
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);
    private const int DWMWA_CLOAKED = 14;

    /// <summary>
    /// 真正在不在当前桌面要看 DWMWA_CLOAKED：被系统隐藏的窗口 cloak!=0。
    /// 不能用 IsWindowOnCurrentVirtualDesktop——pin 到所有桌面后它恒为 1，是假象。
    /// </summary>
    private static bool IsCloaked(IntPtr hwnd)
    {
        try
        {
            int hr = DwmGetWindowAttribute(hwnd, DWMWA_CLOAKED, out int cloaked, sizeof(int));
            return hr >= 0 && cloaked != 0;
        }
        catch { return false; }
    }

    private System.Windows.Forms.Timer? _pinTimer;
    private System.Windows.Forms.Timer? _pinGuard;
    private int _pinAttempts;
    private bool _pinnedOk;

    /// <summary>
    /// 窗口刚创建时 ApplicationView 可能还没登记，延迟重试到"回读确认固定"为止。
    /// 成功后再挂一个守护定时器：切桌面后若发现窗口不在当前桌面，自动重新固定。
    /// </summary>
    private void StartPinRetry()
    {
        _pinAttempts = 0;
        _pinnedOk = false;
        PinLog($"StartPinRetry hwnd=0x{Handle:X}");
        _pinTimer?.Dispose();
        _pinTimer = new System.Windows.Forms.Timer { Interval = 400 };
        _pinTimer.Tick += (_, _) =>
        {
            if (!IsHandleCreated || IsDisposed) { _pinTimer?.Stop(); return; }
            _pinAttempts++;
            bool ok = TryPinToAllVirtualDesktops(Handle, out var detail);
            PinLog($"try#{_pinAttempts} ok={ok} {detail}");
            if (ok || _pinAttempts >= 15)
            {
                _pinnedOk = ok;
                _pinTimer!.Stop();
                _pinTimer.Dispose();
                _pinTimer = null;
                StartPinGuard();
            }
        };
        _pinTimer.Start();
    }

    private void StartPinGuard()
    {
        _pinGuard?.Dispose();
        _pinGuard = new System.Windows.Forms.Timer { Interval = 1500 };
        _pinGuard.Tick += (_, _) =>
        {
            if (!IsHandleCreated || IsDisposed) { _pinGuard?.Stop(); return; }
            bool cloaked = IsCloaked(Handle);
            if (cloaked)   // 被系统隐藏（切桌面后留在旧桌面）=> 重新固定（只在异常时记日志）
            {
                bool ok = TryPinToAllVirtualDesktops(Handle, out var detail);
                PinLog($"guard_repin cloaked=True ok={ok} {detail}");
            }
        };
        _pinGuard.Start();
    }

    private static void SetExStyle(IntPtr hwnd, int flag, bool on)
    {
        var cur = (int)GetWindowLongPtr64(hwnd, GWL_EXSTYLE);
        cur = on ? cur | flag : cur & ~flag;
        SetWindowLongPtr64(hwnd, GWL_EXSTYLE, new IntPtr(cur));
    }

    // ------------------------------------------------------------- 配色

    /// <summary>标签颜色 = 主面板数值卡的配色。</summary>
    private static readonly Dictionary<OverlayItem, Color> LabelColors = new()
    {
        [OverlayItem.Cpu] = Color.FromArgb(79, 195, 247),      // 蓝
        [OverlayItem.CpuTemp] = Color.FromArgb(239, 154, 154), // 红
        [OverlayItem.Ram] = Color.FromArgb(255, 183, 77),      // 橙
        [OverlayItem.GpuUtil] = Color.FromArgb(129, 199, 132), // 绿
        [OverlayItem.GpuMem] = Color.FromArgb(77, 208, 225),   // 青
        [OverlayItem.GpuTemp] = Color.FromArgb(239, 154, 154), // 红
        [OverlayItem.BoardTemp] = Color.FromArgb(186, 104, 200),// 紫
        [OverlayItem.Net] = Color.FromArgb(149, 117, 205),     // 紫
    };

    private static readonly Color ValueGreen = Color.FromArgb(64, 255, 112); // 数值绿
    private static readonly Color DimColor = Color.FromArgb(150, 190, 205);  // 无数据暗青
    private const float Gap = 14f;      // 段间距
    private const int HPad = 18;        // 左右留白
    private const int VPad = 8;         // 上下留白（随 DPI 缩放）

    // ------------------------------------------------------------- 状态

    private Font _font = new("Consolas", 10.5f, FontStyle.Bold);
    private double _fontSize = 10.5;
    private Sample? _last;
    private string _clockText = DateTime.Now.ToString("T");   // 系统长时间格式（本机 H:mm:ss，精确到秒）
    private System.Windows.Forms.Timer? _clockTimer;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public OverlayItem Items { get; set; } =
        OverlayItem.Cpu | OverlayItem.CpuTemp | OverlayItem.BoardTemp | OverlayItem.Ram |
        OverlayItem.GpuUtil | OverlayItem.GpuMem | OverlayItem.GpuTemp | OverlayItem.Net;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool ClickThrough { get; private set; } = true;

    private OverlayStyle _style = OverlayStyle.Text;

    /// <summary>外观样式：文字条 / 小卡片。切换后立即重排尺寸。</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public OverlayStyle Style
    {
        get => _style;
        set
        {
            if (_style == value) return;
            _style = value;
            if (IsHandleCreated) { UpdateSize(); Refresh(); }
        }
    }

    /// <summary>显示顺序（用户可调）。为空时按枚举默认顺序。</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public List<OverlayItem> ItemOrder { get; set; } = new();

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public MainForm? Owner1 { get; set; }   // 右键菜单跳主面板用

    /// <summary>悬浮条字号（pt）。主面板调节，背景框随文字和字号一起变大变小。</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public double FontSizePt
    {
        get => _fontSize;
        set
        {
            if (value is < 8 or > 20) return;
            _fontSize = value;
            _font.Dispose();
            _font = new Font("Consolas", (float)value, FontStyle.Bold);
            if (IsHandleCreated) { UpdateSize(); Refresh(); }
        }
    }

    public OverlayForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        DoubleBuffered = true;
        BackColor = Color.Black;
        Opacity = 0.78;

        var wa = Screen.PrimaryScreen!.WorkingArea;
        Location = new Point(wa.Left, wa.Top);
        Size = new Size(360, HeightFor(DeviceDpi));

        ContextMenuStrip = BuildMenu();
        MouseDown += OnDragStart;
        MouseMove += OnDragMove;
        MouseUp += OnDragEnd;
    }

    private static int HeightFor(int dpi) => (int)((_fontHeightPt * dpi / 72f) + VPad * dpi / 96f * 2);
    private const float _fontHeightPt = 14f;   // 粗体 Consolas 估高，实际随字号在 UpdateSize 里精确算

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("打开主面板", null, (_, _) => Owner1?.ShowFromTray());
        var lockItem = new ToolStripMenuItem("锁定（鼠标穿透）") { CheckOnClick = true, Checked = ClickThrough };
        lockItem.Click += (_, _) => SetClickThrough(lockItem.Checked);
        menu.Items.Add(lockItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => Application.Exit());
        return menu;
    }

    // ------------------------------------------------------------- 穿透与拖动

    public void SetClickThrough(bool on)
    {
        ClickThrough = on;
        if (IsHandleCreated)
            SetExStyle(Handle, WS_EX_TRANSPARENT, on);
    }

    /// <summary>
    /// 安全改透明度：WinForms 设置 Opacity 会重写窗口扩展样式，把穿透标志抹掉，必须补写。
    /// </summary>
    public void SetOpacitySafe(double opacity)
    {
        Opacity = Math.Clamp(opacity, 0.30, 1.0);
        if (IsHandleCreated)
            SetExStyle(Handle, WS_EX_TRANSPARENT, ClickThrough);
    }

    private bool _dragging;
    private Point _dragOffset;

    private const int SnapPixels = 20;   // 磁力吸附感应距离

    private void OnDragStart(object? s, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            _dragging = true;
            _dragOffset = e.Location;
        }
    }

    private void OnDragMove(object? s, MouseEventArgs e)
    {
        if (!_dragging) return;
        var pos = new Point(Left + e.X - _dragOffset.X, Top + e.Y - _dragOffset.Y);

        // 磁力吸附：接近所在屏幕工作区顶边时吸上去（顺便吸左右边，防止拖出屏幕）
        var wa = Screen.FromPoint(pos).WorkingArea;
        if (Math.Abs(pos.Y - wa.Top) <= SnapPixels)
            pos.Y = wa.Top;
        if (pos.X < wa.Left + SnapPixels / 2)
            pos.X = wa.Left;
        if (pos.X + Width > wa.Right - SnapPixels / 2)
            pos.X = wa.Right - Width;

        Location = pos;
    }

    private void OnDragEnd(object? s, MouseEventArgs e) => _dragging = false;

    // ------------------------------------------------------------- 数据与绘制

    public void SetSample(Sample s)
    {
        _last = s;
        UpdateSize();
        Refresh();   // 仅在 UI 线程调用（由主窗体 BeginInvoke 保证）
    }

    /// <summary>组装悬浮条各段：(标签, 数值, 标签颜色)。顺序按用户自定义 ItemOrder。</summary>
    private List<(string Label, string Value, Color Color)> BuildSegments()
    {
        var segs = new List<(string, string, Color)>();

        if (_last is null)
        {
            segs.Add(("MonitorHUD", " 启动中…", DimColor));
        }
        else
        {
            var s = _last;

            // 自定义顺序优先；未收录的新条目按枚举顺序追加在后面
            IEnumerable<OverlayItem> order = ItemOrder.Count > 0
                ? ItemOrder.Concat(Enum.GetValues<OverlayItem>().Where(i => i != OverlayItem.None && !ItemOrder.Contains(i)))
                : Enum.GetValues<OverlayItem>().Where(i => i != OverlayItem.None);

            foreach (var item in order)
            {
                if (!Items.HasFlag(item)) continue;
                if (SegmentFor(item, s) is { } seg) segs.Add(seg);
            }

            if (segs.Count == 0)
                segs.Add(("提示 ", "右键 → 打开主面板勾选显示内容", DimColor));
        }

        // 时间固定在最后：不进勾选列表，也不受上移/下移顺序影响
        segs.Add(("时间 ", _clockText, Color.FromArgb(120, 170, 190)));
        return segs;
    }

    private (string Label, string Value, Color Color)? SegmentFor(OverlayItem item, Sample s)
    {
        return item switch
        {
            OverlayItem.Cpu => ("CPU ", $"{s.CpuPercent,4:0}%", LabelColors[item]),
            OverlayItem.CpuTemp => ("CPU ", s.CpuTemp is { } ct ? $"{ct:0}°C" : "--°C", LabelColors[item]),
            OverlayItem.BoardTemp => ("主板 ", s.BoardTemp is { } bt ? $"{bt:0}°C" : "--°C", LabelColors[item]),
            OverlayItem.Ram => ("内存 ", $"{s.RamPercent,4:0}% {s.RamUsedGb:0.0}/{s.RamTotalGb:0}G", LabelColors[item]),
            OverlayItem.GpuUtil => ("GPU ", s.GpuUtil is { } gu ? $"{gu,4:0}%" : "--%", LabelColors[item]),
            OverlayItem.GpuMem => ("显存 ", s.GpuMemUsedMb is { } gm && s.GpuMemTotalMb > 0
                ? $"{gm / 1024:0.0}/{s.GpuMemTotalMb / 1024:0.0}G" : "--", LabelColors[item]),
            OverlayItem.GpuTemp => ("GPU ", s.GpuTemp is { } gt ? $"{gt:0}°C" : "--°C", LabelColors[item]),
            OverlayItem.Net => ("网速 ", $"↓{FmtSpeed(s.NetDown)} ↑{FmtSpeed(s.NetUp)}", LabelColors[item]),
            _ => null,
        };
    }

    private static string FmtSpeed(double mbps) =>
        mbps >= 1024 ? $"{mbps / 1024:0.00}G" : mbps >= 10 ? $"{mbps:0}M" : $"{mbps:0.0}M";

    /// <summary>
    /// 排版并（可选）绘制所有段。draw=false 时只算尺寸，给 UpdateSize 用。
    /// 文字样式：紧凑一行；卡片样式：每项一圆角小卡，内存/显存带进度条。
    /// </summary>
    private (float W, float H) LayoutAndDraw(Graphics g, bool draw)
    {
        return _style == OverlayStyle.Cards
            ? LayoutCards(g, draw)
            : LayoutText(g, draw);
    }

    // ---------------- 样式 A：一行文字 ----------------
    private (float W, float H) LayoutText(Graphics g, bool draw)
    {
        var fmt = StringFormat.GenericDefault;
        float x = HPad;
        float fontH = _font.GetHeight(g);
        float y = (Height - fontH) / 2f;

        foreach (var (label, value, color) in BuildSegments())
        {
            if (draw)
                using (var b = new SolidBrush(color))
                    g.DrawString(label, _font, b, x, y, fmt);
            x += g.MeasureString(label, _font, PointF.Empty, fmt).Width;

            bool missing = value.Contains("--") || value.Contains("启动中");
            if (draw)
                using (var b = new SolidBrush(missing ? DimColor : ValueGreen))
                    g.DrawString(value, _font, b, x, y, fmt);
            x += g.MeasureString(value, _font, PointF.Empty, fmt).Width + Gap;
        }
        return (x - Gap + HPad, fontH + VPad * 2);   // 总宽、总高
    }

    // ---------------- 样式 B：小卡片 ----------------
    // 卡片：圆角边框 + 彩色标题（上）+ 绿色数值（下）；内存/显存画进度条
    private float _cardW = 78f;    // 单卡宽（逻辑像素）
    private float _cardH = 62f;    // 单卡高

    private (float W, float H) LayoutCards(Graphics g, bool draw)
    {
        var fmt = StringFormat.GenericDefault;
        using var titleFont = new Font("Microsoft YaHei UI", (float)Math.Max(8.0, _fontSize - 1.5), FontStyle.Bold);
        using var valFont = new Font("Consolas", (float)_fontSize, FontStyle.Bold);
        float titleH = titleFont.GetHeight(g);
        float valH = valFont.GetHeight(g);

        var segs = BuildSegments();
        // 先量出最宽卡宽：标题、数值、进度条区取最大
        float cardW = 0;
        foreach (var seg in segs)
        {
            float w = Math.Max(
                g.MeasureString(seg.Label.Trim(), titleFont, PointF.Empty, fmt).Width,
                g.MeasureString(seg.Value, valFont, PointF.Empty, fmt).Width);
            cardW = Math.Max(cardW, w);
        }
        bool hasBar = segs.Any(s => BarRatio(s) is not null);
        cardW = Math.Max(64f, cardW + 16f);        // 左右各 8 内边距
        if (hasBar) cardW = Math.Max(cardW, 92f);  // 进度条卡稍宽
        _cardW = cardW;

        float cardH = titleH + valH + (hasBar ? 12f : 8f) + 12f;  // 标题+数值+进度条+上下留白
        _cardH = cardH;

        float cardGap = 6f;
        float totalW = HPad * 2 + segs.Count * cardW + Math.Max(0, segs.Count - 1) * cardGap;
        float totalH = cardH + VPad * 2;

        if (!draw) return (totalW, totalH);

        // ---- 实际绘制 ----
        float x = HPad, y = VPad;
        foreach (var (label, value, color) in segs)
        {
            var rect = new RectangleF(x, y, cardW, cardH);
            using (var path = RoundedRect(rect, 6f))
            using (var bg = new SolidBrush(Color.FromArgb(28, 30, 36)))
            using (var border = new Pen(Color.FromArgb(70, 90, 100)))
            {
                g.FillPath(bg, path);
                g.DrawPath(border, path);
            }

            using (var tb = new SolidBrush(color))
                g.DrawString(label.Trim(), titleFont, tb, x + 8, y + 5, fmt);

            bool missing = value.Contains("--") || value.Contains("启动中");
            using (var vb = new SolidBrush(missing ? DimColor : ValueGreen))
                g.DrawString(value, valFont, vb, x + 8, y + 5 + titleH, fmt);

            // 进度条（内存/显存）
            var ratio = BarRatio((label, value, color));
            if (ratio is { } r)
            {
                var bar = new RectangleF(x + 8, y + 5 + titleH + valH + 3, cardW - 16, 6f);
                using (var track = new SolidBrush(Color.FromArgb(55, 58, 66)))
                    g.FillRectangle(track, bar);
                int fillW = (int)Math.Max(0, Math.Min(bar.Width, bar.Width * r));
                if (fillW > 0)
                {
                    var fillRect = new RectangleF(bar.X, bar.Y, fillW, bar.Height);
                    using var path2 = RoundedRect(fillRect, 3f);
                    using var fb = new SolidBrush(color);
                    g.FillPath(fb, path2);
                }
            }

            x += cardW + cardGap;
        }
        return (totalW, totalH);
    }

    /// <summary>该段是否为内存/显存，返回进度比例 0..1；其它返回 null。</summary>
    private float? BarRatio((string Label, string Value, Color Color) seg)
    {
        if (_last is null) return null;
        var label = seg.Label.Trim();
        if (label == "内存" && _last.RamTotalGb > 0)
            return (float)Math.Clamp(_last.RamUsedGb / _last.RamTotalGb, 0, 1);
        if (label == "显存" && seg.Value != "--" && _last.GpuMemTotalMb is > 0)
            return (float)Math.Clamp((_last.GpuMemUsedMb ?? 0) / _last.GpuMemTotalMb.Value, 0, 1);
        return null;
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        float d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>背景框宽高随文字与字号自适应。</summary>
    private void UpdateSize()
    {
        using var g = CreateGraphics();
        var (logicalW, logicalH) = LayoutAndDraw(g, draw: false);
        int dpi = DeviceDpi;
        int newW = (int)Math.Ceiling(logicalW * dpi / 96f);
        int newH = (int)Math.Ceiling(logicalH * dpi / 96f);

        var wa = Screen.FromControl(this).WorkingArea;
        newW = Math.Min(newW, wa.Width);
        newH = Math.Min(newH, wa.Height);

        if (Math.Abs(newW - Width) > 2 || Math.Abs(newH - Height) > 1)
        {
            int right = Left + newW;
            if (right > wa.Right)
                Left = Math.Max(wa.Left, wa.Right - newW);
            Size = new Size(newW, newH);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        LayoutAndDraw(e.Graphics, draw: true);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        SetExStyle(Handle, WS_EX_TRANSPARENT, ClickThrough);
        SetExStyle(Handle, WS_EX_LAYERED, true);
        BeginInvoke(StartPinRetry);   // 等窗口真正显示后再钉到所有虚拟桌面
        UpdateSize();

        // 独立时钟：按系统长时间格式（含秒）刷新，不受监控采样间隔影响
        _clockTimer?.Dispose();
        _clockTimer = new System.Windows.Forms.Timer { Interval = 250 };
        _clockTimer.Tick += (_, _) =>
        {
            var text = DateTime.Now.ToString("T");
            if (text == _clockText) return;
            _clockText = text;
            if (IsHandleCreated && !IsDisposed) { UpdateSize(); Refresh(); }
        };
        _clockTimer.Start();
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        _clockTimer?.Stop();
        base.OnHandleDestroyed(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _clockTimer?.Dispose();
            _clockTimer = null;
            _pinTimer?.Dispose();
            _pinTimer = null;
            _pinGuard?.Dispose();
            _pinGuard = null;
            _font.Dispose();
        }
        base.Dispose(disposing);
    }
}
