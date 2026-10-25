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

    private Label _nameLabel;
    private Label _statusLabel;
    private Label _percentLabel;
    private Label _updatedLabel;
    private PictureBox _ringBox;

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
    private static readonly Color Accent = Color.FromArgb(58, 130, 255);

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

        _background = _light ? Color.FromArgb(246, 247, 250) : Color.FromArgb(26, 26, 29);
        _sidebarColor = _light ? Color.FromArgb(252, 252, 254) : Color.FromArgb(33, 33, 37);
        _cardColor = _light ? Color.White : Color.FromArgb(40, 40, 45);
        _border = _light ? Color.FromArgb(227, 227, 233) : Color.FromArgb(56, 56, 62);
        _fore = _light ? Color.FromArgb(24, 24, 28) : Color.FromArgb(243, 243, 247);
        _secondary = _light ? Color.FromArgb(108, 108, 116) : Color.FromArgb(152, 152, 160);

        Text = "LogiBatteryTray";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = _background;
        ForeColor = _fore;
        Font = new Font("Segoe UI", 9f);
        ClientSize = new Size((int)S(812), (int)S(548));
        DoubleBuffered = true;

        // ---------- 左侧功能栏 ----------
        _sidebar = new Panel
        {
            Location = P(0, 0),
            Size = new Size((int)S(212), (int)S(548)),
            BackColor = _sidebarColor,
        };
        _sidebar.Paint += PaintSidebar;
        Controls.Add(_sidebar);

        float navTop = S(104);

        _navs = new[]
        {
            new NavButton("\uE83F", "设备", _fore, _secondary, Accent) { Location = P(14, navTop) },
            new NavButton("\uE713", "设置", _fore, _secondary, Accent) { Location = P(14, navTop + 46) },
            new NavButton("\uE946", "关于", _fore, _secondary, Accent) { Location = P(14, navTop + 92) },
        };

        for (int i = 0; i < _navs.Length; i++)
        {
            int index = i;
            _navs[i].Size = new Size((int)S(184), (int)S(40));
            _navs[i].Click += (_, _) => ShowPage(index);
            _sidebar.Controls.Add(_navs[i]);
        }

        var versionLabel = new Label
        {
            Text = $"v{typeof(MainForm).Assembly.GetName().Version?.ToString(3)}",
            AutoSize = true,
            Font = new Font("Segoe UI", 8f),
            ForeColor = _secondary,
            BackColor = Color.Transparent,
            Location = P(22, 510),
        };
        _sidebar.Controls.Add(versionLabel);

        // ---------- 右侧内容页 ----------
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
        Location = P(212, 0),
        Size = new Size((int)S(600), (int)S(548)),
        BackColor = _background,
        Visible = false,
    };

    // ---------- 设备页 ----------
    private Panel BuildDevicePage()
    {
        var page = NewPage();

        float y = PlaceTitle(page, "设备", "查看当前连接的罗技鼠标");
        y += S(8);

        var card = new Panel
        {
            Location = P(28, y),
            Size = new Size((int)S(544), (int)S(216)),
            BackColor = Color.Transparent,
        };
        card.Paint += (_, e) => PaintCard(e, card, S(14));
        page.Controls.Add(card);

        _ringBox = new PictureBox
        {
            Location = P(26, 28),
            Size = new Size((int)S(158), (int)S(158)),
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.Transparent,
        };
        card.Controls.Add(_ringBox);

        float infoY = S(30);
        _nameLabel = AddLabel(card, 206, infoY, "罗技鼠标", 14f, FontStyle.Bold, _fore);
        infoY = _nameLabel.Bottom + S(4);
        _statusLabel = AddLabel(card, 206, infoY, "使用中", 9.5f, FontStyle.Regular, _secondary);
        infoY = _statusLabel.Bottom + S(10);
        _percentLabel = AddLabel(card, 206, infoY, "--", 32f, FontStyle.Bold, _fore);
        infoY = _percentLabel.Bottom + S(4);
        _updatedLabel = AddLabel(card, 208, infoY, "", 8.5f, FontStyle.Regular, _secondary);

        float actionsY = y + S(238);
        AddLabel(page, 28, actionsY, "快捷操作", 9.5f, FontStyle.Bold, _fore);

        var refresh = MakeButton(28, actionsY + S(26), "立即刷新", primary: true);
        refresh.Parent = page;
        refresh.Click += (_, _) => _app.RefreshNow();

        var log = MakeButton(156, actionsY + S(26), "打开日志");
        log.Parent = page;
        log.Click += (_, _) => _app.OpenLogFolderPublic();

        return page;
    }

    // ---------- 设置页 ----------
    private Panel BuildSettingsPage()
    {
        var page = NewPage();
        float y = PlaceTitle(page, "设置", "所有修改都会立即生效");
        y += S(14);

        y = Section(page, "外观", y);
        _shapeButton = MakeValueButton(page, 28, y, "图标形状", "圆环");
        y += S(46);
        _percentCheck = MakeCheck(page, 28, y, "图标显示百分比（电池图标时生效）");
        y = _percentCheck.Bottom + S(18);

        y = Section(page, "行为", y);
        _intervalButton = MakeValueButton(page, 28, y, "刷新间隔", "5 分钟");
        y += S(46);
        _offlineButton = MakeValueButton(page, 28, y, "关机判定", "2 分钟");
        y += S(46);
        _autostartCheck = MakeCheck(page, 28, y, "开机自动启动");
        y = _autostartCheck.Bottom + S(10);
        _notifyCheck = MakeCheck(page, 28, y, "电量低于 20% 时通知");
        y = _notifyCheck.Bottom + S(10);
        AddLabel(page, 28, y, "点击右侧数值可切换", 8f, FontStyle.Regular, _secondary);

        _percentCheck.CheckedChanged += (_, _) => OnSettingChanged();
        _autostartCheck.CheckedChanged += (_, _) => OnSettingChanged();
        _notifyCheck.CheckedChanged += (_, _) => OnSettingChanged();

        _shapeButton.Click += (_, _) => Cycle(_shapeButton, ShapeValues, ShapeTexts, v => _app.Settings.IconShapeMode = v);
        _intervalButton.Click += (_, _) => Cycle(_intervalButton, IntervalValues, IntervalTexts, v => _app.Settings.IntervalSeconds = v);
        _offlineButton.Click += (_, _) => Cycle(_offlineButton, OfflineValues, OfflineTexts, v => _app.Settings.OfflineAsOffSeconds = v);

        return page;
    }

    // ---------- 关于页 ----------
    private Panel BuildAboutPage()
    {
        var page = NewPage();
        float y = PlaceTitle(page, "关于", "LogiBatteryTray");
        y += S(8);

        var card = new Panel
        {
            Location = P(28, y),
            Size = new Size((int)S(544), (int)S(168)),
            BackColor = Color.Transparent,
        };
        card.Paint += (_, e) => PaintCard(e, card, S(14));
        page.Controls.Add(card);

        float cy = S(26);
        var title = AddLabel(card, 28, cy, "LogiBatteryTray", 13f, FontStyle.Bold, _fore);
        cy = title.Bottom + S(4);
        var ver = AddLabel(card, 28, cy, $"版本 {typeof(MainForm).Assembly.GetName().Version?.ToString(3)}", 9f, FontStyle.Regular, _secondary);
        cy = ver.Bottom + S(12);
        var desc = AddLabel(card, 28, cy, "在任务栏显示罗技鼠标电量，直接读取 HID++ 2.0，不需要 G HUB。", 8.5f, FontStyle.Regular, _secondary);
        cy = desc.Bottom + S(6);
        AddLabel(card, 28, cy, @"数据目录 %APPDATA%\LogiBatteryTray", 8.5f, FontStyle.Regular, _secondary);

        float buttonsY = y + S(190);
        var log = MakeButton(28, buttonsY, "打开日志");
        log.Parent = page;
        log.Click += (_, _) => _app.OpenLogFolderPublic();

        var quit = MakeButton(156, buttonsY, "退出程序");
        quit.Parent = page;
        quit.Click += (_, _) => { _app.ExitAppPublic(); Close(); };

        return page;
    }

    /// <summary>画页面标题，返回内容起始 y。</summary>
    private float PlaceTitle(Panel page, string title, string subtitle)
    {
        var titleLabel = AddLabel(page, 28, 26, title, 17f, FontStyle.Bold, _fore);
        var subtitleLabel = AddLabel(page, 30, titleLabel.Bottom + S(2), subtitle, 8.5f, FontStyle.Regular, _secondary);
        return subtitleLabel.Bottom + S(10);
    }

    private float Section(Panel page, string text, float y)
    {
        var label = AddLabel(page, 28, y, text, 9.5f, FontStyle.Bold, _fore);
        return label.Bottom + S(10);
    }

    /// <summary>加一个自动大小的标签，返回它（Bottom 可用于继续排版）。</summary>
    private Label AddLabel(Control parent, float x, float y, string text, float size, FontStyle style, Color color)
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

    private CheckBox MakeCheck(Panel page, float x, float y, string text)
    {
        var check = new CheckBox
        {
            Text = text,
            AutoSize = true,
            Location = P(x, y),
            ForeColor = _fore,
            FlatStyle = FlatStyle.Flat,
        };
        page.Controls.Add(check);
        return check;
    }

    private Button MakeValueButton(Panel page, float x, float y, string caption, string value)
    {
        var captionLabel = AddLabel(page, x, y + S(8), caption, 9f, FontStyle.Regular, _secondary);

        var button = new Button
        {
            Text = value,
            Location = new Point(captionLabel.Right + (int)S(16), (int)(y - S(4))),
            Size = new Size((int)S(168), (int)S(34)),
            FlatStyle = FlatStyle.Flat,
            BackColor = _light ? Color.FromArgb(240, 240, 245) : Color.FromArgb(48, 48, 53),
            ForeColor = _fore,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding((int)S(10), 0, 0, 0),
        };
        button.FlatAppearance.BorderColor = _border;
        page.Controls.Add(button);
        return button;
    }

    private Button MakeButton(float x, float y, string text, bool primary = false)
    {
        var button = new Button
        {
            Text = text,
            Location = P(x, y),
            Size = new Size((int)S(116), (int)S(34)),
            FlatStyle = FlatStyle.Flat,
            BackColor = primary
                ? Color.FromArgb(10, 132, 255)
                : (_light ? Color.FromArgb(240, 240, 245) : Color.FromArgb(48, 48, 53)),
            ForeColor = primary ? Color.White : _fore,
        };
        button.FlatAppearance.BorderColor = _border;
        return button;
    }

    private void PaintSidebar(object sender, PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

        // 右分割线
        using (var pen = new Pen(_border, 1f))
            e.Graphics.DrawLine(pen, _sidebar.Width - 0.5f, 0, _sidebar.Width - 0.5f, _sidebar.Height);

        // 图标：圆环 + 中心点
        var logoRect = new RectangleF(S(22), S(24), S(28), S(28));
        using (var pen = new Pen(Accent, S(3.4f)))
            e.Graphics.DrawEllipse(pen, logoRect);
        using (var brush = new SolidBrush(Accent))
            e.Graphics.FillEllipse(brush, logoRect.X + logoRect.Width / 2 - S(3), logoRect.Y + logoRect.Height / 2 - S(3), S(6), S(6));

        using var titleFont = new Font("Segoe UI", 11.5f, FontStyle.Bold);
        using var titleBrush = new SolidBrush(_fore);
        e.Graphics.DrawString("LogiBatteryTray", titleFont, titleBrush, S(60), S(22));

        using var subFont = new Font("Segoe UI", 8f);
        using var subBrush = new SolidBrush(_secondary);
        e.Graphics.DrawString("罗技鼠标电量", subFont, subBrush, S(60), S(46));
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


        LayoutInfoColumn();

        int size = (int)S(158);
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

    /// <summary>文字高度会随内容和 DPI 变化，这里重新排一次，避免重叠。</summary>
    private void LayoutInfoColumn()
    {
        float y = S(30);
        _nameLabel.Top = (int)y;
        y = _nameLabel.Bottom + S(4);
        _statusLabel.Top = (int)y;
        y = _statusLabel.Bottom + S(10);
        _percentLabel.Top = (int)y;
        y = _percentLabel.Bottom + S(4);
        _updatedLabel.Top = (int)y;
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _ringBox.Image?.Dispose();
        base.OnFormClosed(e);
    }

    /// <summary>左侧功能栏按钮：图标 + 文字，选中即时刷新。</summary>
    private sealed class NavButton : Control
    {
        private readonly string _glyph;
        private readonly Color _fore;
        private readonly Color _muted;
        private readonly Color _accent;
        private bool _hover;
        private bool _selected;

        public NavButton(string glyph, string text, Color fore, Color muted, Color accent)
        {
            _glyph = glyph;
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
        public bool Selected
        {
            get => _selected;
            set
            {
                if (_selected == value) return;
                _selected = value;
                Invalidate();
            }
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            if (_selected || _hover)
            {
                var color = _selected ? _accent : Color.FromArgb(32, _accent);
                using var path = RoundedLocal(new RectangleF(0, 0, Width - 1, Height - 1), Height / 2f);
                using var brush = new SolidBrush(color);
                g.FillPath(brush, path);
            }

            using (var iconFont = new Font("Segoe MDL2 Assets", 11f))
            using (var iconBrush = new SolidBrush(_selected ? Color.White : (_hover ? _fore : _muted)))
            using (var iconFormat = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
            {
                g.DrawString(_glyph, iconFont, iconBrush, new RectangleF(12, 0, 24, Height), iconFormat);
            }

            using (var textFont = new Font("Segoe UI", 9.5f, _selected ? FontStyle.Bold : FontStyle.Regular))
            using (var textBrush = new SolidBrush(_selected ? Color.White : (_hover ? _fore : _muted)))
            using (var textFormat = new StringFormat { LineAlignment = StringAlignment.Center })
            {
                g.DrawString(Text, textFont, textBrush, new RectangleF(46, 0, Width - 50, Height), textFormat);
            }
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
