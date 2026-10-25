using System.Drawing.Drawing2D;

namespace LogiBatteryTray;

/// <summary>MainForm 需要的宿主能力（TrayApp 实现，预览时用假实现）。</summary>
internal interface IMainFormHost
{
    AppSettings Settings { get; }
    BatteryService Service { get; }
    void RefreshNow();
    void OpenLogFolderPublic();
    void ExitAppPublic();
    void ApplySettingsFromWindow();
}

/// <summary>左键点托盘图标打开的设置窗口：左侧功能栏 + 右侧内容页。</summary>
internal sealed class MainForm : Form
{
    private readonly IMainFormHost _app;
    private readonly bool _light;

    private readonly Panel _sidebar;
    private readonly Panel[] _pages;
    private readonly NavButton[] _navs;

    // 设备页
    private Label _nameLabel;
    private Label _statusLabel;
    private Label _percentLabel;
    private Label _updatedLabel;
    private PictureBox _ringBox;

    // 设置页
    private CheckBox _percentCheck;
    private CheckBox _autostartCheck;
    private CheckBox _notifyCheck;
    private Button _shapeButton;
    private Button _intervalButton;
    private Button _offlineButton;

    private readonly Color _background;
    private readonly Color _sidebarColor;
    private readonly Color _cardColor;
    private readonly Color _border;
    private readonly Color _fore;
    private readonly Color _secondary;
    private readonly Color _accent = Color.FromArgb(58, 130, 255);

    private bool _loading;

    private static readonly int[] IntervalValues = { 60, 300, 900, 1800, 3600 };
    private static readonly string[] IntervalTexts = { "1 分钟", "5 分钟", "15 分钟", "30 分钟", "1 小时" };
    private static readonly int[] ShapeValues = { 0, 1 };
    private static readonly string[] ShapeTexts = { "圆环", "电池图标" };
    private static readonly int[] OfflineValues = { 60, 120, 300, 600 };
    private static readonly string[] OfflineTexts = { "1 分钟", "2 分钟", "5 分钟", "10 分钟" };

    public MainForm(IMainFormHost app)
    {
        _app = app;
        _light = Theme.IsLightTaskbar();

        _background = _light ? Color.FromArgb(247, 247, 250) : Color.FromArgb(28, 28, 30);
        _sidebarColor = _light ? Color.White : Color.FromArgb(35, 35, 38);
        _cardColor = _light ? Color.White : Color.FromArgb(40, 40, 44);
        _border = _light ? Color.FromArgb(228, 228, 233) : Color.FromArgb(54, 54, 59);
        _fore = _light ? Color.FromArgb(25, 25, 29) : Color.FromArgb(242, 242, 246);
        _secondary = _light ? Color.FromArgb(110, 110, 118) : Color.FromArgb(154, 154, 162);

        Text = "LogiBatteryTray";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = _background;
        ForeColor = _fore;
        Font = new Font("Segoe UI", 9f);
        ClientSize = new Size((int)S(760), (int)S(520));

        _sidebar = new Panel
        {
            Location = P(0, 0),
            Size = new Size((int)S(184), (int)S(520)),
            BackColor = _sidebarColor,
        };
        _sidebar.Paint += PaintSidebar;
        Controls.Add(_sidebar);

        MakeLabel(22, 22, "LogiBatteryTray", 12f, FontStyle.Bold, _fore, _sidebar);
        MakeLabel(22, 46, "罗技鼠标电量", 8.5f, FontStyle.Regular, _secondary, _sidebar);

        _navs = new[]
        {
            new NavButton("设备", _fore, _secondary, _accent) { Location = P(14, 88), Size = new Size((int)S(156), (int)S(36)) },
            new NavButton("设置", _fore, _secondary, _accent) { Location = P(14, 130), Size = new Size((int)S(156), (int)S(36)) },
            new NavButton("关于", _fore, _secondary, _accent) { Location = P(14, 172), Size = new Size((int)S(156), (int)S(36)) },
        };

        for (int i = 0; i < _navs.Length; i++)
        {
            int index = i;
            _navs[i].Click += (_, _) => ShowPage(index);
            _sidebar.Controls.Add(_navs[i]);
        }

        MakeLabel(22, 480, $"v{typeof(MainForm).Assembly.GetName().Version?.ToString(3)}",
            8f, FontStyle.Regular, _secondary, _sidebar);

        _pages = new[] { BuildDevicePage(), BuildSettingsPage(), BuildAboutPage() };
        foreach (var page in _pages) Controls.Add(page);

        ShowPage(0);
        LoadFromSettings();
        UpdateView();
    }

    private float S(float value) => value * DeviceDpi / 96f;

    private Point P(float x, float y) => new((int)S(x), (int)S(y));

    private Panel NewPage() => new()
    {
        Location = P(184, 0),
        Size = new Size((int)S(576), (int)S(520)),
        BackColor = _background,
        Visible = false,
    };

    private Panel BuildDevicePage()
    {
        var page = NewPage();

        var card = new Panel
        {
            Location = P(24, 24),
            Size = new Size((int)S(528), (int)S(196)),
            BackColor = Color.Transparent,
        };
        card.Paint += (_, e) => PaintCard(e, card, S(14));
        page.Controls.Add(card);

        _ringBox = new PictureBox
        {
            Location = P(40, 44),
            Size = new Size((int)S(124), (int)S(124)),
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.Transparent,
        };
        card.Controls.Add(_ringBox);

        _nameLabel = MakeLabel(200, 40, "罗技鼠标", 14f, FontStyle.Bold, _fore, card);
        _statusLabel = MakeLabel(200, 70, "使用中", 9.5f, FontStyle.Regular, _secondary, card);
        _percentLabel = MakeLabel(200, 94, "--", 38f, FontStyle.Bold, _fore, card);
        _updatedLabel = MakeLabel(202, 158, "", 8.5f, FontStyle.Regular, _secondary, card);

        MakeLabel(24, 252, "快捷操作", 9.5f, FontStyle.Bold, _fore, page);

        var refresh = MakeButton(24, 282, "立即刷新", primary: true);
        refresh.Parent = page;
        refresh.Click += (_, _) => _app.RefreshNow();

        var log = MakeButton(152, 282, "打开日志");
        log.Parent = page;
        log.Click += (_, _) => _app.OpenLogFolderPublic();

        return page;
    }

    private Panel BuildSettingsPage()
    {
        var page = NewPage();

        MakeLabel(24, 24, "外观", 9.5f, FontStyle.Bold, _fore, page);
        _shapeButton = MakeValueButton(24, 54, "图标形状", "圆环", page);
        _percentCheck = MakeCheck(24, 98, "图标显示百分比（仅电池图标）", page);

        MakeLabel(24, 144, "行为", 9.5f, FontStyle.Bold, _fore, page);
        _intervalButton = MakeValueButton(24, 174, "刷新间隔", "5 分钟", page);
        _offlineButton = MakeValueButton(24, 216, "关机判定", "2 分钟", page);
        _autostartCheck = MakeCheck(24, 260, "开机自动启动", page);
        _notifyCheck = MakeCheck(24, 292, "电量低于 20% 时通知", page);

        MakeLabel(24, 334, "点击右侧数值可切换", 8f, FontStyle.Regular, _secondary, page);

        _percentCheck.CheckedChanged += (_, _) => OnSettingChanged();
        _autostartCheck.CheckedChanged += (_, _) => OnSettingChanged();
        _notifyCheck.CheckedChanged += (_, _) => OnSettingChanged();

        _shapeButton.Click += (_, _) => Cycle(_shapeButton, ShapeValues, ShapeTexts, v => _app.Settings.IconShapeMode = v);
        _intervalButton.Click += (_, _) => Cycle(_intervalButton, IntervalValues, IntervalTexts, v => _app.Settings.IntervalSeconds = v);
        _offlineButton.Click += (_, _) => Cycle(_offlineButton, OfflineValues, OfflineTexts, v => _app.Settings.OfflineAsOffSeconds = v);

        return page;
    }

    private Panel BuildAboutPage()
    {
        var page = NewPage();

        var card = new Panel
        {
            Location = P(24, 24),
            Size = new Size((int)S(528), (int)S(156)),
            BackColor = Color.Transparent,
        };
        card.Paint += (_, e) => PaintCard(e, card, S(14));
        page.Controls.Add(card);

        MakeLabel(28, 26, "LogiBatteryTray", 14f, FontStyle.Bold, _fore, card);
        MakeLabel(28, 56, $"版本 {typeof(MainForm).Assembly.GetName().Version?.ToString(3)}", 9f, FontStyle.Regular, _secondary, card);
        MakeLabel(28, 84, "在任务栏显示罗技鼠标电量，直接读取 HID++ 2.0，不需要 G HUB。", 8.5f, FontStyle.Regular, _secondary, card);
        MakeLabel(28, 108, @"数据目录 %APPDATA%\LogiBatteryTray", 8.5f, FontStyle.Regular, _secondary, card);

        var log = MakeButton(24, 200, "打开日志");
        log.Parent = page;
        log.Click += (_, _) => _app.OpenLogFolderPublic();

        var quit = MakeButton(152, 200, "退出程序");
        quit.Parent = page;
        quit.Click += (_, _) => { _app.ExitAppPublic(); Close(); };

        return page;
    }

    private void PaintSidebar(object sender, PaintEventArgs e)
    {
        using var pen = new Pen(_border, 1f);
        e.Graphics.DrawLine(pen, _sidebar.Width - 0.5f, 0, _sidebar.Width - 0.5f, _sidebar.Height);
    }

    private void PaintCard(PaintEventArgs e, Control card, float radius)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Rounded(new RectangleF(0, 0, card.Width, card.Height), radius);
        using (var fill = new SolidBrush(_cardColor)) e.Graphics.FillPath(fill, path);
        using (var pen = new Pen(_border, 1f)) e.Graphics.DrawPath(pen, path);
    }

    private static GraphicsPath Rounded(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        float diameter = Math.Min(radius * 2f, Math.Min(rect.Width, rect.Height));
        if (diameter <= 1f) { path.AddRectangle(rect); return path; }

        var arc = new RectangleF(rect.X, rect.Y, diameter, diameter);
        path.AddArc(arc, 180, 90);
        arc.X = rect.Right - diameter;
        path.AddArc(arc, 270, 90);
        arc.Y = rect.Bottom - diameter;
        path.AddArc(arc, 0, 90);
        arc.X = rect.X;
        path.AddArc(arc, 90, 90);
        path.CloseFigure();
        return path;
    }

    private void ShowPage(int index)
    {
        for (int i = 0; i < _pages.Length; i++)
            _pages[i].Visible = i == index;

        for (int i = 0; i < _navs.Length; i++)
            _navs[i].Selected = i == index;
    }

    private Label MakeLabel(float x, float y, string text, float size, FontStyle style, Color color, Control parent)
    {
        var label = new Label
        {
            Text = text,
            AutoSize = true,
            Location = P(x, y),
            Font = new Font("Segoe UI", size, style),
            ForeColor = color,
            BackColor = Color.Transparent,
        };
        parent.Controls.Add(label);
        return label;
    }

    private CheckBox MakeCheck(float x, float y, string text, Control parent)
    {
        var check = new CheckBox
        {
            Text = text,
            AutoSize = true,
            Location = P(x, y),
            ForeColor = _fore,
            FlatStyle = FlatStyle.Flat,
        };
        parent.Controls.Add(check);
        return check;
    }

    private Button MakeValueButton(float x, float y, string caption, string value, Control parent)
    {
        MakeLabel(x, y + S(7), caption, 9f, FontStyle.Regular, _secondary, parent);

        var button = new Button
        {
            Text = value,
            Location = P(x + 110, y),
            Size = new Size((int)S(170), (int)S(32)),
            FlatStyle = FlatStyle.Flat,
            BackColor = _light ? Color.FromArgb(240, 240, 245) : Color.FromArgb(48, 48, 52),
            ForeColor = _fore,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding((int)S(10), 0, 0, 0),
        };
        button.FlatAppearance.BorderColor = _border;
        parent.Controls.Add(button);
        return button;
    }

    private Button MakeButton(float x, float y, string text, bool primary = false)
    {
        var button = new Button
        {
            Text = text,
            Location = P(x, y),
            Size = new Size((int)S(112), (int)S(34)),
            FlatStyle = FlatStyle.Flat,
            BackColor = primary
                ? Color.FromArgb(10, 132, 255)
                : (_light ? Color.FromArgb(240, 240, 245) : Color.FromArgb(48, 48, 52)),
            ForeColor = primary ? Color.White : _fore,
        };
        button.FlatAppearance.BorderColor = _border;
        return button;
    }

    private void Cycle(Button button, int[] values, string[] texts, Action<int> apply)
    {
        if (_loading) return;

        int index = Array.IndexOf(texts, button.Text);
        if (index < 0) index = 0;

        int next = (index + 1) % values.Length;
        button.Text = texts[next];
        apply(values[next]);

        _app.ApplySettingsFromWindow();
        UpdateView();
    }

    private void LoadFromSettings()
    {
        _loading = true;

        var settings = _app.Settings;
        _autostartCheck.Checked = AppSettings.IsAutostartEnabled();
        _notifyCheck.Checked = settings.LowBatteryNotify;
        _percentCheck.Checked = settings.ShowPercentInIcon;
        _percentCheck.Enabled = settings.IconShapeMode == (int)IconShape.Battery;

        _intervalButton.Text = IntervalTexts[IndexOr(IntervalValues, settings.IntervalSeconds, 1)];
        _shapeButton.Text = ShapeTexts[IndexOr(ShapeValues, settings.IconShapeMode, 0)];
        _offlineButton.Text = OfflineTexts[IndexOr(OfflineValues, settings.OfflineAsOffSeconds, 1)];

        _loading = false;
    }

    private static int IndexOr(int[] values, int value, int fallback)
    {
        int index = Array.IndexOf(values, value);
        return index >= 0 ? index : fallback;
    }

    private void OnSettingChanged()
    {
        if (_loading) return;

        var settings = _app.Settings;
        settings.LowBatteryNotify = _notifyCheck.Checked;
        settings.ShowPercentInIcon = _percentCheck.Checked;

        AppSettings.SetAutostart(_autostartCheck.Checked);
        settings.Autostart = _autostartCheck.Checked;

        _percentCheck.Enabled = settings.IconShapeMode == (int)IconShape.Battery;

        _app.ApplySettingsFromWindow();
        UpdateView();
    }

    public void SelectPage(int index) => ShowPage(index);

    public void UpdateView()
    {
        var device = _app.Service.Primary;
        var battery = device?.Battery;
        bool online = device != null && device.Online && battery != null;

        string offlineLabel = device?.PoweredOff == true ? "停用" : "休眠";
        string status = online
            ? battery!.StatusText
            : battery?.Percent != null ? $"{offlineLabel} · 上次 {battery.Percent}%" : "未连接";

        _nameLabel.Text = device?.Name ?? _app.Settings.LastDeviceName;
        _statusLabel.Text = status;
        _percentLabel.Text = battery?.Percent is int percent ? $"{percent}%" : "--";
        _percentLabel.ForeColor = online ? IconFactory.LevelColor(battery!) : _secondary;
        _updatedLabel.Text = device?.UpdatedAt is DateTime time
            ? $"更新于 {time:HH:mm:ss}"
            : (_app.Service.Problem ?? "尚未读取到数据");

        _percentCheck.Enabled = _app.Settings.IconShapeMode == (int)IconShape.Battery;

        int size = (int)S(124);
        var previous = _ringBox.Image;
        try
        {
            using var icon = IconFactory.Create(size, battery, _app.Settings.ShowPercentInIcon, _light,
                device != null && !online);
            _ringBox.Image = icon.ToBitmap();
        }
        catch
        {
            // 绘制失败时保留原图
        }
        previous?.Dispose();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _ringBox.Image?.Dispose();
        base.OnFormClosed(e);
    }

    /// <summary>左侧功能栏按钮（圆角高亮 + 悬停效果）。</summary>
    private sealed class NavButton : Control
    {
        private readonly Color _fore;
        private readonly Color _muted;
        private readonly Color _accent;
        private bool _hover;

        public NavButton(string text, Color fore, Color muted, Color accent)
        {
            Text = text;
            _fore = fore;
            _muted = muted;
            _accent = accent;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
            Cursor = Cursors.Hand;
            BackColor = Color.Transparent;
        }

        [System.ComponentModel.Browsable(false)]
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public bool Selected { get; set; }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            if (Selected || _hover)
            {
                var color = Selected ? _accent : Color.FromArgb(30, _accent);
                using var path = RoundedLocal(new RectangleF(0, 0, Width - 1, Height - 1), Height / 2f);
                using var brush = new SolidBrush(color);
                g.FillPath(brush, path);
            }

            using var font = new Font("Segoe UI", 9.5f, Selected ? FontStyle.Bold : FontStyle.Regular);
            using var textBrush = new SolidBrush(Selected ? Color.White : (_hover ? _fore : _muted));
            using var format = new StringFormat { LineAlignment = StringAlignment.Center };
            g.DrawString(Text, font, textBrush, new RectangleF(16, 0, Width - 20, Height), format);
        }

        private static GraphicsPath RoundedLocal(RectangleF rect, float radius)
        {
            var path = new GraphicsPath();
            float diameter = Math.Min(radius * 2f, Math.Min(rect.Width, rect.Height));
            if (diameter <= 1f) { path.AddRectangle(rect); return path; }

            var arc = new RectangleF(rect.X, rect.Y, diameter, diameter);
            path.AddArc(arc, 180, 90);
            arc.X = rect.Right - diameter;
            path.AddArc(arc, 270, 90);
            arc.Y = rect.Bottom - diameter;
            path.AddArc(arc, 0, 90);
            arc.X = rect.X;
            path.AddArc(arc, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
