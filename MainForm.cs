using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Text.Json;

namespace MonitorHUD;

/// <summary>持久化配置（%APPDATA%\MonitorHUD\config.json）。</summary>
public sealed class Config
{
    public OverlayItem Items { get; set; } =
        OverlayItem.Cpu | OverlayItem.CpuTemp | OverlayItem.BoardTemp | OverlayItem.Ram |
        OverlayItem.GpuUtil | OverlayItem.GpuMem | OverlayItem.GpuTemp | OverlayItem.Net;
    public int IntervalMs { get; set; } = 1000;
    public bool ClickThrough { get; set; } = true;
    public bool MinimizeToTray { get; set; } = true;
    public double FontSize { get; set; } = 10.5;
    public double OverlayOpacity { get; set; } = 0.78;
    public int OverlayStyle { get; set; } = 0;   // 0=文字条 1=卡片条
    public List<int> Order { get; set; } = new();   // 悬浮条显示顺序（OverlayItem 值序列）
    public bool Migrated { get; set; }   // 一次性把 CPU/主板温度并入勾选
    public int OverlayX { get; set; } = -1;   // -1 = 默认屏幕顶端
    public int OverlayY { get; set; } = -1;

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MonitorHUD", "config.json");

    public static Config Load()
    {
        Config? cfg;
        try
        {
            if (!File.Exists(FilePath)) return new Config();
            cfg = JsonSerializer.Deserialize<Config>(File.ReadAllText(FilePath));
            if (cfg is null) return new Config();
        }
        catch { return new Config(); }

        // 旧配置迁移：CPU/主板温度默认勾上（只做一次，之后用户取消勾选会被尊重）
        if (!cfg.Migrated)
        {
            cfg.Items |= OverlayItem.CpuTemp | OverlayItem.BoardTemp;
            cfg.Migrated = true;
            cfg.Save();
        }
        return cfg;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            // 锁 + 临时文件原子替换：任何线程并发保存都不会写坏配置
            lock (SaveLock)
            {
                var tmp = FilePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(tmp, FilePath, overwrite: true);
            }
        }
        catch { }
    }

    private static readonly object SaveLock = new();
}

/// <summary>主面板：实时数值 + 曲线图 + 悬浮条配置。</summary>
public sealed class MainForm : Form
{
    private static readonly (string Label, OverlayItem Item)[] ItemDefs =
    {
        ("CPU 占用", OverlayItem.Cpu),
        ("CPU 温度（需管理员）", OverlayItem.CpuTemp),
        ("主板温度（需管理员）", OverlayItem.BoardTemp),
        ("内存占用", OverlayItem.Ram),
        ("GPU 占用", OverlayItem.GpuUtil),
        ("显存占用", OverlayItem.GpuMem),
        ("GPU 温度", OverlayItem.GpuTemp),
        ("网速 ↓↑", OverlayItem.Net),
    };

    private readonly Config _cfg = Config.Load();
    private readonly MonitorService _service = new();

    private readonly List<Sample> _history = new();
    private const int HistoryMax = 180;

    private readonly OverlayForm _overlay;
    private readonly NotifyIcon _tray;

    private readonly ChartPanel _chart;
    private readonly Dictionary<string, Label> _vals = new();
    private Panel? _valuesPanel;            // 数值区面板：高度随卡片行数自适应
    private FlowLayoutPanel? _valuesFlow;   // 数值卡容器：宽度变化时重排行数
    private readonly CheckedListBox _itemList;
    // 勾选来源标记：仅当点击落在复选框方框内（或按空格）才允许切换勾选；
    // 点文字行只选中（用于上移/下移调顺序），不改变勾选状态。
    private bool _checkAllowed = true;
    private readonly NumericUpDown _interval;
    private NumericUpDown? _fontSizeNum;
    private NumericUpDown? _opacityNum;
    private ComboBox? _styleCombo;
    private readonly CheckBox _clickThrough;
    private readonly CheckBox _minToTray;
    private CheckBox? _autoStart;          // 开机自启（状态以注册表为准）
    private bool _autoStartSyncing;        // 程序化改勾选时抑制事件递归
    private readonly Label _adminHint;

    public MainForm()
    {
        Text = "MonitorHUD 硬件监控";
        Size = new Size(860, 700);
        MinimumSize = new Size(820, 640);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(24, 26, 32);
        ForeColor = Color.Gainsboro;
        Font = new Font("Microsoft YaHei UI", 9.5f);
        Icon = MakeIcon();

        _overlay = new OverlayForm { Owner1 = this };
        _overlay.Items = _cfg.Items;
        _overlay.Opacity = Math.Clamp(_cfg.OverlayOpacity, 0.30, 1.0);
        if (_cfg.OverlayX >= 0) _overlay.Location = new Point(_cfg.OverlayX, _cfg.OverlayY);

        // ------- 布局
        _chart = new ChartPanel(_history) { Dock = DockStyle.Fill };
        _chart.BackColor = Color.FromArgb(16, 18, 23);

        var valuesPanel = BuildValuesPanel(out _adminHint);
        _valuesPanel = valuesPanel;
        var settingsPanel = BuildSettingsPanel(out _itemList, out _interval, out _clickThrough, out _minToTray);

        var bottom = new Panel { Dock = DockStyle.Bottom, Height = 240 };
        settingsPanel.Dock = DockStyle.Fill;
        bottom.Controls.Add(settingsPanel);

        Controls.Add(_chart);
        Controls.Add(valuesPanel);
        Controls.Add(bottom);
        valuesPanel.Dock = DockStyle.Top;
        valuesPanel.Height = 132;

        // ------- 托盘
        _tray = new NotifyIcon
        {
            Icon = Icon,
            Text = "MonitorHUD",
            Visible = true,
            ContextMenuStrip = BuildTrayMenu(),
        };
        _tray.DoubleClick += (_, _) => ShowFromTray();

        Load += (_, _) =>
        {
            // 不能用 Show(this)：附属窗口会跟随 owner（主面板）被虚拟桌面连带 cloak，
            // 即使 PinView 成功，切到别的桌面后仍被系统隐藏（肉眼看不到的根因）。
            _overlay.Show();
            _overlay.SetClickThrough(_cfg.ClickThrough);
            _service.IntervalMs = _cfg.IntervalMs;
            _service.SampleReady += OnSample;
            _service.Start();
            _adminHint.Visible = !MonitorServiceAdmin;
            ResizeValuesPanel();
        };
        Resize += (_, _) => ResizeValuesPanel();

        FormClosing += (_, e) =>
        {
            if (!_exiting && _cfg.MinimizeToTray && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
            }
        };

        // 注意：这里不能调 ApplyCheckedList——勾选框还没初始化，
        // 会把"全没勾"写进配置（正是之前"勾选记不住"的根因）。初始勾选在 OnLoad 里做。
    }

    private static bool MonitorServiceAdmin =>
        new System.Security.Principal.WindowsPrincipal(
            System.Security.Principal.WindowsIdentity.GetCurrent())
        .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);

    /// <summary>
    /// 数值区高度自适应卡片行数：窗口窄时卡片换行，面板自动变高让全部 10 张卡始终可见，
    /// 避免第二行（下载/上传/内存/显存）被裁掉却看不到。上限保护防止挤没曲线图。
    /// </summary>
    private void ResizeValuesPanel()
    {
        if (_valuesFlow is null || _adminHint is null || ClientSize.Height <= 0) return;
        const int cardW = 136;   // 128 宽 + 左右各 4 margin
        const int cardH = 92;    // 84 高 + 上下各 4 margin
        int availW = _valuesFlow.ClientSize.Width;
        if (availW <= 0) return;

        int n = Math.Max(1, _valuesFlow.Controls.Count);
        int perRow = Math.Max(1, availW / cardW);
        int rows = (int)Math.Ceiling(n / (double)perRow);
        int needed = rows * cardH + 16 + _adminHint.Height;   // 上下 padding + 提示条

        // 上限：不超过窗体高度 42%，否则开滚动条兜底（极端小窗口）
        int maxH = (int)(ClientSize.Height * 0.42);
        bool overflow = needed > maxH;
        _valuesFlow.AutoScroll = overflow;
        _valuesFlow.WrapContents = !overflow;   // 溢出时改单列纵向滚动

        if (_valuesPanel is not null)
        {
            int h = Math.Max(140, Math.Min(needed, maxH));
            if (_valuesPanel.Height != h) _valuesPanel.Height = h;
        }
    }

    // ------------------------------------------------------------ 子面板

    private Panel BuildValuesPanel(out Label adminHint)
    {
        var panel = new Panel { Padding = new Padding(14, 10, 14, 4) };
        adminHint = new Label
        {
            Dock = DockStyle.Bottom,
            Height = 24,
            ForeColor = Color.FromArgb(255, 170, 90),
            Text = "※ 以管理员身份运行本程序可显示 CPU / 主板温度（WMI 热区需要管理员权限）",
            TextAlign = ContentAlignment.MiddleLeft,
        };
        var flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            WrapContents = true,
            AutoScroll = false,
            Padding = new Padding(0, 0, 0, 2),
        };
        _valuesFlow = flow;

        var defs = new (string key, string title, Color color)[]
        {
            ("cpu", "CPU 占用", Color.FromArgb(79, 195, 247)),
            ("cput", "CPU 温度", Color.FromArgb(239, 154, 154)),
            ("brdt", "主板温度", Color.FromArgb(186, 104, 200)),
            ("gpu", "GPU 占用", Color.FromArgb(129, 199, 132)),
            ("gtmp", "GPU 温度", Color.FromArgb(239, 154, 154)),
            ("down", "下载", Color.FromArgb(149, 117, 205)),
            ("up", "上传", Color.FromArgb(121, 134, 203)),
        };

        foreach (var (key, title, color) in defs)
        {
            var card = new Panel { Size = new Size(128, 84), Margin = new Padding(4) };
            var titleL = new Label { Text = title, ForeColor = color, Font = new Font(Font, FontStyle.Bold), AutoSize = false, Size = new Size(120, 22), Location = new Point(6, 6) };
            var valL = new Label { Text = "--", ForeColor = Color.Gainsboro, Font = new Font("Consolas", 12f, FontStyle.Bold), AutoSize = false, Size = new Size(120, 30), Location = new Point(6, 30) };
            card.Controls.Add(titleL);
            card.Controls.Add(valL);
            card.Paint += (_, e) => e.Graphics.DrawRectangle(new Pen(Color.FromArgb(60, 255, 255, 255)), 0, 0, card.Width - 1, card.Height - 1);
            flow.Controls.Add(card);
            _vals[key] = valL;
        }

        // 内存 / 显存：带进度条的卡片（参照截图样式：标题彩色 + 进度条 + x/y 文字）
        _barRam = new BarCard("内存", Color.FromArgb(79, 195, 247), Color.FromArgb(255, 183, 77));
        _barGmem = new BarCard("显存", Color.FromArgb(77, 208, 225), Color.FromArgb(77, 208, 225));
        flow.Controls.Add(_barRam);
        flow.Controls.Add(_barGmem);

        panel.Controls.Add(flow);
        panel.Controls.Add(adminHint);
        return panel;
    }

    private BarCard? _barRam;
    private BarCard? _barGmem;

    /// <summary>带进度条的数值卡：彩色标题 + 圆角进度条 + "x/y" 文字。</summary>
    private sealed class BarCard : Panel
    {
        private readonly string _title;
        private readonly Color _titleColor;
        private readonly Color _barColor;

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public string ValueText { get; set; } = "--";
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public double Ratio { get; set; }   // 0..1

        public BarCard(string title, Color titleColor, Color barColor)
        {
            _title = title;
            _titleColor = titleColor;
            _barColor = barColor;
            Size = new Size(128, 84);
            Margin = new Padding(4);
            DoubleBuffered = true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            using var border = new Pen(Color.FromArgb(60, 255, 255, 255));
            g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);

            using var tf = new Font("Microsoft YaHei UI", 9f, FontStyle.Bold);
            using var tb = new SolidBrush(_titleColor);
            g.DrawString(_title, tf, tb, 6, 5);

            // 进度条
            var track = new Rectangle(8, 34, Width - 16, 12);
            using (var path = Rounded(track, 6))
            using (var fill = new SolidBrush(Color.FromArgb(45, 45, 52)))
                g.FillPath(fill, path);

            double r = Math.Clamp(Ratio, 0, 1);
            if (r > 0.01)
            {
                var bar = new Rectangle(8, 34, Math.Max((int)((Width - 16) * r), 12), 12);
                using var path2 = Rounded(bar, 6);
                using var fb = new SolidBrush(_barColor);
                g.FillPath(fb, path2);
            }

            using var vf = new Font("Consolas", 10f, FontStyle.Bold);
            using var vb = new SolidBrush(Color.Gainsboro);
            g.DrawString(ValueText, vf, vb, 6, 54);
        }

        private static System.Drawing.Drawing2D.GraphicsPath Rounded(Rectangle r, int radius)
        {
            var p = new System.Drawing.Drawing2D.GraphicsPath();
            int d = radius * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            // 必须清底，否则每秒刷新的文字和进度条会叠在上一帧上（花屏）
            using var b = new SolidBrush(Color.FromArgb(24, 26, 32));
            e.Graphics.FillRectangle(b, 0, 0, Width, Height);
        }
    }

    private Panel BuildSettingsPanel(out CheckedListBox list, out NumericUpDown interval,
        out CheckBox clickThrough, out CheckBox minToTray)
    {
        var panel = new Panel { Padding = new Padding(14, 8, 14, 10) };

        var title = new Label
        {
            Text = "悬浮条显示内容（勾选后立即生效）",
            Dock = DockStyle.Top,
            Height = 26,
            ForeColor = Color.FromArgb(64, 255, 112),
            Font = new Font(Font, FontStyle.Bold),
        };

        list = new CheckedListBox
        {
            Dock = DockStyle.Left,
            Width = 250,
            CheckOnClick = false,   // 点文字行只选中，不切换勾选；勾选只在点方框时发生
            BackColor = Color.FromArgb(30, 33, 40),
            ForeColor = Color.Gainsboro,
            BorderStyle = BorderStyle.FixedSingle,
            IntegralHeight = false,
        };
        foreach (var (label, _) in ItemDefs) list.Items.Add(label, false);

        // 判断点击是否落在复选框方框内（WinForms CheckedListBox 的方框固定在左侧约 3~18px）
        // 用 s（事件发送者）而非捕获 out 参数 list——C# 不允许 lambda 捕获 ref/out/in 参数
        list.MouseDown += (s, e) =>
        {
            var cb = (CheckedListBox)s!;
            int idx = cb.IndexFromPoint(e.Location);
            float scale = cb.DeviceDpi / 96f;
            _checkAllowed = idx >= 0 && e.X >= 2 * scale && e.X <= 20 * scale;
        };
        // 键盘空格键是合法的勾选方式，一并放行
        list.KeyDown += (_, e) => { if (e.KeyCode == Keys.Space) _checkAllowed = true; };
        // 拦截勾选变化：来源不是点方框/空格时，还原原状态
        list.ItemCheck += (_, e) => { if (!_checkAllowed) e.NewValue = e.CurrentValue; };

        // 顺序调整按钮（在勾选列表左侧，调悬浮条内容前后顺序）
        var listBox = list;   // out 参数不能被 lambda 捕获，先拷贝到局部变量
        var movePanel = new Panel { Dock = DockStyle.Left, Width = 88, Padding = new Padding(4, 0, 0, 0) };
        var upBtn = new Button { Text = "▲ 上移", Width = 76, Height = 30, Location = new Point(4, 60) };
        var downBtn = new Button { Text = "▼ 下移", Width = 76, Height = 30, Location = new Point(4, 96) };
        upBtn.Click += (_, _) => MoveSelectedItem(listBox, -1);
        downBtn.Click += (_, _) => MoveSelectedItem(listBox, 1);
        var moveHint = new Label
        {
            Text = "选中一行\n调顺序",
            Location = new Point(4, 20),
            AutoSize = true,
            ForeColor = Color.FromArgb(140, 150, 165),
        };
        movePanel.Controls.AddRange([moveHint, upBtn, downBtn]);

        var right = new Panel { Dock = DockStyle.Fill, Padding = new Padding(16, 0, 12, 0) };

        // 顶部设置项：两行四列表格。标签右对齐、输入左对齐，四列各自成一条直线，
        // 不再像流式布局那样因标签宽度不同导致输入框起点参差。
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            ColumnCount = 4,
            RowCount = 2,
            Padding = new Padding(4, 10, 4, 4),
            Margin = Padding.Empty,
            CellBorderStyle = TableLayoutPanelCellBorderStyle.None,
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 112));  // 标签1：刷新间隔（毫秒）
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 84));   // 输入1：刷新间隔
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 94));   // 标签2：悬浮条字号/透明度/样式
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130));  // 输入2：字号/透明度/样式

        static Label SettingLabel(string text) => new()
        {
            Text = text,
            AutoSize = true,
            Anchor = AnchorStyles.Right,
            Margin = new Padding(0, 8, 0, 0),
        };
        static Control Fit(Control c) { c.Dock = DockStyle.Fill; c.Margin = new Padding(0, 4, 6, 4); return c; }

        interval = new NumericUpDown
        {
            Minimum = 500, Maximum = 5000, Increment = 500,
            Value = Math.Clamp(_cfg.IntervalMs, 500, 5000),
        };
        _fontSizeNum = new NumericUpDown
        {
            Minimum = 8, Maximum = 20, Increment = 0.5m, DecimalPlaces = 1,
            Value = (decimal)Math.Clamp(_cfg.FontSize, 8, 20),
        };
        _opacityNum = new NumericUpDown
        {
            Minimum = 0.3m, Maximum = 1m, Increment = 0.05m, DecimalPlaces = 2,
            Value = (decimal)Math.Clamp(_cfg.OverlayOpacity, 0.3, 1.0),
        };
        _styleCombo = new ComboBox
        {
            DropDownStyle = ComboBoxStyle.DropDownList,
            Items = { "文字（一行紧凑）", "卡片（带进度条）" },
            SelectedIndex = Math.Clamp(_cfg.OverlayStyle, 0, 1),
        };

        grid.Controls.Add(SettingLabel("刷新间隔（毫秒）"), 0, 0);
        grid.Controls.Add(Fit(interval), 1, 0);
        grid.Controls.Add(SettingLabel("悬浮条字号"), 2, 0);
        grid.Controls.Add(Fit(_fontSizeNum), 3, 0);
        grid.Controls.Add(SettingLabel("悬浮条透明度"), 0, 1);
        grid.Controls.Add(Fit(_opacityNum), 1, 1);
        grid.Controls.Add(SettingLabel("悬浮条样式"), 2, 1);
        grid.Controls.Add(Fit(_styleCombo), 3, 1);

        var checkFlow = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            WrapContents = true,
            Padding = new Padding(4, 4, 4, 0),
            Margin = Padding.Empty,
        };
        clickThrough = new CheckBox
        {
            Text = "悬浮条鼠标穿透（锁定后不挡游戏操作）",
            AutoSize = true,
            Margin = new Padding(0, 2, 24, 2),
            Checked = _cfg.ClickThrough,
        };
        minToTray = new CheckBox
        {
            Text = "关闭窗口时最小化到托盘",
            AutoSize = true,
            Margin = new Padding(0, 2, 0, 2),
            Checked = _cfg.MinimizeToTray,
        };
        // 开机自启状态直接读注册表：用户可能在别处删过启动项，不能只信 config
        _autoStart = new CheckBox
        {
            Text = "开机自动启动",
            AutoSize = true,
            Margin = new Padding(0, 2, 0, 2),
            Checked = AutoStart.IsEnabled,
        };
        checkFlow.Controls.Add(clickThrough);
        checkFlow.Controls.Add(minToTray);
        checkFlow.Controls.Add(_autoStart);

        var hint = new Label
        {
            Text = "悬浮条：未锁定时左键按住可拖动；右键 = 打开主面板 / 锁定 / 退出。\n拖到合适位置后位置会自动记住。时间固定显示在末尾。",
            Dock = DockStyle.Bottom,
            AutoSize = false,
            ForeColor = Color.FromArgb(140, 150, 165),
            TextAlign = ContentAlignment.TopLeft,
            Padding = new Padding(0, 6, 0, 0),
        };

        // 按右侧面板实际宽度换行，高度随行数变化，避免长句向右溢出压住别的控件
        void FitHintHeight()
        {
            int textW = Math.Max(80, right.ClientSize.Width - right.Padding.Left - 20);
            var size = TextRenderer.MeasureText(hint.Text, hint.Font, new Size(textW, int.MaxValue),
                TextFormatFlags.WordBreak | TextFormatFlags.GlyphOverhangPadding);
            hint.Height = Math.Max(44, size.Height + 10);
        }
        right.Resize += (_, _) => FitHintHeight();

        // 后加的控件先停靠：grid 在最上，checkFlow 次之，hint 贴底
        right.Controls.Add(hint);
        right.Controls.Add(checkFlow);
        right.Controls.Add(grid);
        FitHintHeight();

        panel.Controls.Add(right);
        panel.Controls.Add(list);
        panel.Controls.Add(movePanel);
        panel.Controls.Add(title);
        return panel;
    }

    private ContextMenuStrip BuildTrayMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("打开主面板", null, (_, _) => ShowFromTray());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("重新启动", null, (_, _) => RestartApp());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => { _exiting = true; Application.Exit(); });
        return menu;
    }

    /// <summary>
    /// 托盘「重新启动」：先起新进程（把旧 PID 传过去），再关掉自己。
    /// 新进程在 Program.Main 里等旧 PID 退出后才抢单实例 Mutex —— 否则它会把自己
    /// 判成"重复实例"，唤醒旧进程后自己退出，表面看就是程序被关掉了。
    /// </summary>
    private void RestartApp()
    {
        try
        {
            var exe = AutoStart.ExePath;
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
            {
                MessageBox.Show("找不到程序自身路径，无法自动重启。请手动退出后重新打开。",
                    "MonitorHUD", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = $"--restart-after {Environment.ProcessId}",
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? Environment.CurrentDirectory,
            });

            _exiting = true;      // 跳过"最小化到托盘"，让窗口真正关掉
            Application.Exit();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"重新启动失败：{ex.Message}", "MonitorHUD",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private bool _exiting;

    /// <summary>第二次双击 exe 时由新进程发来的唤醒消息，唤出本实例主面板。</summary>
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x8001) { ShowFromTray(); return; }
        base.WndProc(ref m);
    }

    public void ShowFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    // ------------------------------------------------------------ 勾选应用

    private void ApplyCheckedList(CheckedListBox list)
    {
        OverlayItem items = OverlayItem.None;
        var order = new List<int>();
        for (int i = 0; i < list.Items.Count; i++)
        {
            var label = (string)list.Items[i]!;
            var def = ItemDefs.FirstOrDefault(d => d.Label == label);
            if (string.IsNullOrEmpty(def.Label)) continue;
            if (list.GetItemChecked(i)) items |= def.Item;
            order.Add((int)def.Item);
        }
        _cfg.Items = items;
        _overlay.Items = items;
        _cfg.Order = order;
        _overlay.ItemOrder = order.Select(v => (OverlayItem)v).ToList();
        _overlay.Refresh();
        _cfg.Save();
    }

    /// <summary>上移/下移选中的显示条目（调整悬浮条前后顺序）。</summary>
    private void MoveSelectedItem(CheckedListBox list, int direction)
    {
        int idx = list.SelectedIndex;
        int target = idx + direction;
        if (idx < 0 || target < 0 || target >= list.Items.Count) return;

        var item = list.Items[idx];
        bool chk = list.GetItemChecked(idx);
        list.Items.RemoveAt(idx);
        list.Items.Insert(target, item);
        _checkAllowed = true;   // 程序化恢复原勾选状态，必须放行
        list.SetItemChecked(target, chk);
        list.SelectedIndex = target;
        ApplyCheckedList(list);   // 顺序与勾选一起落盘，悬浮条立即生效
    }

    // ------------------------------------------------------------ 数据回调

    private void OnSample(Sample s)
    {
        if (IsDisposed) return;
        try
        {
            BeginInvoke(() =>
            {
                _history.Add(s);
                if (_history.Count > HistoryMax) _history.RemoveAt(0);
                _chart.Invalidate();
                _overlay.SetSample(s);
                UpdateLabels(s);
            });
        }
        catch { }
    }

    private void UpdateLabels(Sample s)
    {
        _vals["cpu"].Text = $"{s.CpuPercent:0}%";
        _vals["cput"].Text = s.CpuTemp is { } t ? $"{t:0} °C" : "--";
        _vals["brdt"].Text = s.BoardTemp is { } bt ? $"{bt:0} °C" : "--";
        _vals["gpu"].Text = s.GpuUtil is { } g ? $"{g:0}%" : "--";
        _vals["gtmp"].Text = s.GpuTemp is { } gt ? $"{gt:0} °C" : "--";
        _vals["down"].Text = $"{FmtNet(s.NetDown)} MB/s";
        _vals["up"].Text = $"{FmtNet(s.NetUp)} MB/s";

        if (_barRam is { } ram)
        {
            ram.Ratio = s.RamPercent / 100.0;
            ram.ValueText = $"{s.RamUsedGb:0.0}/{s.RamTotalGb:0}G";
            ram.Invalidate();
        }
        if (_barGmem is { } gm && s.GpuMemUsedMb is { } used && s.GpuMemTotalMb > 0)
        {
            gm.Ratio = used / (double)s.GpuMemTotalMb;
            gm.ValueText = $"{used / 1024:0.0}/{s.GpuMemTotalMb / 1024:0.0}G";
            gm.Invalidate();
        }
        else if (_barGmem is { } g2)
        {
            g2.Ratio = 0;
            g2.ValueText = "--";
            g2.Invalidate();
        }
    }

    private static string FmtNet(double v) => v >= 100 ? v.ToString("0") : v.ToString("0.0");

    // ------------------------------------------------------------ 图表控件

    private sealed class ChartPanel : Panel
    {
        private readonly List<Sample> _history;

        public ChartPanel(List<Sample> history)
        {
            _history = history;
            DoubleBuffered = true;
            ResizeRedraw = true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            int w = Width, h = Height;
            const int pad = 8;

            // 网格
            using var gridPen = new Pen(Color.FromArgb(35, 255, 255, 255));
            for (int i = 0; i <= 4; i++)
            {
                int y = pad + (h - pad * 2) * i / 4;
                g.DrawLine(gridPen, pad, y, w - pad, y);
            }
            using var gridPenV = new Pen(Color.FromArgb(22, 255, 255, 255));
            for (int i = 1; i < 12; i++)
                g.DrawLine(gridPenV, pad + (w - pad * 2) * i / 12, pad, pad + (w - pad * 2) * i / 12, h - pad);

            if (_history.Count < 2)
            {
                using var dim = new SolidBrush(Color.FromArgb(120, 150, 165));
                g.DrawString("等待数据…", Font, dim, w / 2 - 40, h / 2 - 10);
                return;
            }

            // 曲线：CPU / 内存 / GPU
            DrawSeries(g, s => (float)s.CpuPercent, Color.FromArgb(79, 195, 247));
            DrawSeries(g, s => (float)s.RamPercent, Color.FromArgb(255, 183, 77));
            DrawSeries(g, s => s.GpuUtil is { } v ? (float)v : -1, Color.FromArgb(129, 199, 132));

            // 图例
            var legend = new (string t, Color c)[]
            {
                ("CPU", Color.FromArgb(79, 195, 247)),
                ("内存", Color.FromArgb(255, 183, 77)),
                ("GPU", Color.FromArgb(129, 199, 132)),
            };
            int lx = pad + 6;
            using var f = new Font(Font, FontStyle.Bold);
            foreach (var (t, c) in legend)
            {
                using var b = new SolidBrush(c);
                g.FillRectangle(b, lx, pad + 4, 10, 4);
                var size = g.MeasureString(t, f);
                using var tb = new SolidBrush(Color.Gainsboro);
                g.DrawString(t, f, tb, lx + 14, pad);
                lx += 14 + (int)size.Width + 14;
            }
        }

        private void DrawSeries(Graphics g, Func<Sample, float> pick, Color color)
        {
            int w = Width, h = Height;
            const int pad = 8;
            var pts = new List<PointF>();
            foreach (var s in _history)
            {
                float v = pick(s);
                if (v < 0) continue;
                float x = pad + (w - pad * 2) * (pts.Count / (float)Math.Max(_history.Count - 1, 1));
                float y = h - pad - (h - pad * 2) * Math.Clamp(v / 100f, 0, 1);
                pts.Add(new PointF(x, y));
            }
            if (pts.Count < 2) return;
            using var pen = new Pen(color, 1.8f);
            g.DrawLines(pen, pts.ToArray());
        }
    }

    // ------------------------------------------------------------ 配置同步

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);

        _itemList.ItemCheck += (_, e2) =>
            BeginInvoke(() => ApplyCheckedList(_itemList));

        _interval.ValueChanged += (_, _) =>
        {
            _cfg.IntervalMs = (int)_interval.Value;
            _service.IntervalMs = (int)_interval.Value;
            _cfg.Save();
        };

        if (_fontSizeNum is { } fsNum)
        {
            _overlay.FontSizePt = (double)fsNum.Value;   // 应用已保存的字号
            fsNum.ValueChanged += (_, _) =>
            {
                _cfg.FontSize = (double)fsNum.Value;
                _overlay.FontSizePt = (double)fsNum.Value;   // 背景框随字号自适应
                _cfg.Save();
            };
        }

        if (_opacityNum is { } opNum)
        {
            opNum.ValueChanged += (_, _) =>
            {
                _cfg.OverlayOpacity = (double)opNum.Value;
                _overlay.SetOpacitySafe((double)opNum.Value);   // 改透明度后补写穿透标志
                _cfg.Save();
            };
        }

        // 样式：应用保存值 + 切换即时生效
        _overlay.Style = (OverlayStyle)Math.Clamp(_cfg.OverlayStyle, 0, 1);
        if (_styleCombo is { } combo)
            combo.SelectedIndexChanged += (_, _) =>
            {
                _cfg.OverlayStyle = combo.SelectedIndex;
                _overlay.Style = (OverlayStyle)combo.SelectedIndex;
                _cfg.Save();
            };

        _clickThrough.CheckedChanged += (_, _) =>
        {
            _cfg.ClickThrough = _clickThrough.Checked;
            _overlay.SetClickThrough(_clickThrough.Checked);
            _cfg.Save();
        };

        _minToTray.CheckedChanged += (_, _) =>
        {
            _cfg.MinimizeToTray = _minToTray.Checked;
            _cfg.Save();
        };

        if (_autoStart is { } autoStart)
        {
            autoStart.CheckedChanged += (_, _) =>
            {
                if (_autoStartSyncing) return;
                if (AutoStart.Set(autoStart.Checked, out var err)) return;

                // 写注册表失败就把勾选拨回去，不能让界面显示与实际行为不一致
                _autoStartSyncing = true;
                autoStart.Checked = !autoStart.Checked;
                _autoStartSyncing = false;
                MessageBox.Show($"设置开机启动失败：{err}", "MonitorHUD",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            };
        }

        // 按保存的顺序重排勾选列表
        if (_cfg.Order.Count > 0)
        {
            var byItem = ItemDefs.ToDictionary(d => (int)d.Item, d => d.Label);
            var labels = _itemList.Items.Cast<string>().ToList();
            var ordered = new List<string>();
            foreach (var v in _cfg.Order)
                if (byItem.TryGetValue(v, out var lab) && labels.Remove(lab))
                    ordered.Add(lab);
            if (ordered.Count > 0)
            {
                _itemList.Items.Clear();
                _itemList.Items.AddRange(ordered.Concat(labels).ToArray());
            }
        }

        // 初始勾选状态
        for (int i = 0; i < ItemDefs.Length; i++)
            if (_cfg.Items.HasFlag(ItemDefs[i].Item))
                _itemList.SetItemChecked(i, true);

        ApplyCheckedList(_itemList);   // 初始化后落一次盘（勾选 + 顺序）

        // 悬浮条拖动后保存位置
        _overlay.LocationChanged += (_, _) => SaveOverlayPos();
    }

    // 悬浮条位置防抖保存：用 UI 线程 Timer，和其它配置保存同线程，避免并发写文件
    private System.Windows.Forms.Timer? _posSaveTimer;

    private void SaveOverlayPos()
    {
        if (_posSaveTimer is null)
        {
            _posSaveTimer = new System.Windows.Forms.Timer { Interval = 600 };
            _posSaveTimer.Tick += (_, _) =>
            {
                _posSaveTimer!.Stop();
                _cfg.OverlayX = _overlay.Left;
                _cfg.OverlayY = _overlay.Top;
                _cfg.Save();
            };
        }
        _posSaveTimer.Stop();
        _posSaveTimer.Start();
    }

    private Icon MakeIcon()
    {
        // 用户指定的卡通头像图标，已作为嵌入资源打包，单文件发布也能用
        using var stream = typeof(MainForm).Assembly.GetManifestResourceStream("MonitorHUD.app.ico");
        if (stream is not null)
        {
            try { return new Icon(stream); }
            catch { /* 资源损坏则回退到手画图标 */ }
        }

        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(Color.FromArgb(24, 26, 32));
            using var pen = new Pen(Color.FromArgb(64, 255, 112), 3f);
            g.DrawLines(pen, [new Point(4, 22), new Point(12, 10), new Point(18, 18), new Point(28, 5)]);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _service.Dispose();
            _tray.Visible = false;
            _tray.Dispose();
            _overlay.Dispose();
        }
        base.Dispose(disposing);
    }
}
