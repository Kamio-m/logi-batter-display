using System.Drawing.Drawing2D;

namespace LogiBatteryTray;

/// <summary>MainForm 需要的宿主能力（TrayApp 实现，预览时用假实现）。</summary>
internal interface IMainFormHost
{
    AppSettings Settings { get; }
    BatteryService Service { get; }
    LogiDevice DisplayDevice { get; }
    void RefreshNow();
    void OpenLogFolderPublic();
    void ExitAppPublic();
    void ApplySettingsFromWindow();
}

/// <summary>左键点托盘图标打开的设置窗口：左侧功能栏 + 右侧可滚动内容页。</summary>
internal sealed class MainForm : Form, IMessageFilter
{
    private readonly IMainFormHost _app;
    private readonly bool _light;

    private readonly Panel _sidebar;
    private readonly ScrollHost[] _pages;
    private readonly NavButton[] _navs;

    private Label _nameLabel;
    private Label _statusLabel;
    private Label _percentLabel;
    private Label _updatedLabel;
    private PictureBox _ringBox;
    private Panel _emptyState;
    private ScrollHost _devicePage;

    private ToggleSwitch _percentToggle;
    private ToggleSwitch _autostartToggle;
    private ToggleSwitch _notifyToggle;
    private ValueButton _shapeButton;
    private ValueButton _intervalButton;
    private ValueButton _offlineButton;

    private readonly Color _background;
    private readonly Color _sidebarColor;
    private readonly Color _cardColor;
    private readonly Color _border;
    private readonly Color _fore;
    private readonly Color _secondary;
    private static readonly Color Accent = Color.FromArgb(58, 130, 255);

    private bool _loading;

    private static readonly int[] IntervalValues = { 1, 2, 5, 10, 30, 60 };
    private static readonly string[] IntervalTexts = { "1 秒（实时）", "2 秒", "5 秒", "10 秒", "30 秒", "1 分钟" };
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
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = _background;
        ForeColor = _fore;
        Font = new Font("Segoe UI", 9f);
        DoubleBuffered = true;

        var area = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 800);
        int startWidth = Math.Min((int)S(860), area.Width - (int)S(40));
        int startHeight = Math.Min((int)S(620), area.Height - (int)S(60));
        ClientSize = new Size(startWidth, startHeight);
        MinimumSize = new Size((int)S(720), (int)S(460));

        // ---------- 左侧功能栏 ----------
        _sidebar = new Panel
        {
            Location = P(0, 0),
            Size = new Size((int)S(212), ClientSize.Height),
            BackColor = _sidebarColor,
        };
        _sidebar.Paint += PaintSidebar;
        _sidebar.Resize += (_, _) => _sidebar.Invalidate();
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

        _sidebar.Controls.Add(new Label
        {
            Text = $"v{typeof(MainForm).Assembly.GetName().Version?.ToString(3)}",
            AutoSize = true,
            Font = new Font("Segoe UI", 8f),
            ForeColor = _secondary,
            BackColor = Color.Transparent,
            Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
            Location = P(22, ClientSize.Height - (int)S(38)),
        });

        // ---------- 右侧内容页 ----------
        _pages = new[] { BuildDevicePage(), BuildSettingsPage(), BuildAboutPage() };
        foreach (var page in _pages) Controls.Add(page);

        ShowPage(0);
        LoadFromSettings();
        UpdateView();
        Application.AddMessageFilter(this);
    }

    private float S(float value) => value * DeviceDpi / 96f;

    private Point P(float x, float y) => new((int)S(x), (int)S(y));

    private Color ButtonBack => _light ? Color.FromArgb(243, 243, 247) : Color.FromArgb(48, 48, 54);
    private Color ButtonHover => _light ? Color.FromArgb(233, 233, 240) : Color.FromArgb(56, 56, 63);
    private Color ToggleOff => _light ? Color.FromArgb(214, 214, 221) : Color.FromArgb(74, 74, 82);

    private int SidebarWidth => (int)S(212);

    private ScrollHost NewPage() => new(_background)
    {
        Location = P(212, 0),
        Size = new Size(ClientSize.Width - SidebarWidth, ClientSize.Height),
        Visible = false,
    };

    // ---------- 设备页 ----------
    private ScrollHost BuildDevicePage()
    {
        var page = NewPage();
        _devicePage = page;

        float y = PlaceTitle(page.Content, "设备", "查看当前连接的罗技鼠标");
        y += S(10);

        var card = NewCard(CardWidth(page), (int)S(216));
        card.Location = P(28, y);
        page.Content.Controls.Add(card);

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
        AddLabel(page.Content, 28, actionsY, "快捷操作", 9.5f, FontStyle.Bold, _fore);

        var refresh = MakeButton(28, actionsY + S(26), "立即刷新", S(116), true);
        refresh.Parent = page.Content;
        refresh.Click += (_, _) => _app.RefreshNow();

        var log = MakeButton(156, actionsY + S(26), "打开日志", S(116));
        log.Parent = page.Content;
        log.Click += (_, _) => _app.OpenLogFolderPublic();

        // 未连接时覆盖整页
        _emptyState = new Panel { Dock = DockStyle.Fill, BackColor = _background, Visible = false };
        page.Controls.Add(_emptyState);

        _emptyState.Controls.Add(new Label
        {
            Text = "连接设备",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
            Font = new Font("Segoe UI", 20f, FontStyle.Bold),
            ForeColor = _fore,
            BackColor = Color.Transparent,
        });

        _emptyState.Controls.Add(new Label
        {
            Text = "请插好罗技接收器或数据线",
            Dock = DockStyle.Bottom,
            Height = (int)S(56),
            TextAlign = ContentAlignment.TopCenter,
            Font = new Font("Segoe UI", 9f),
            ForeColor = _secondary,
            BackColor = Color.Transparent,
        });

        _emptyState.BringToFront();
        return page;
    }

    // ---------- 设置页 ----------
    private ScrollHost BuildSettingsPage()
    {
        var page = NewPage();
        float y = PlaceTitle(page.Content, "设置", "所有修改都会立即生效");
        y += S(14);

        var appearance = AddCard(page.Content, "外观", 2, y);
        float row = S(46);
        row = AddValueRow(appearance, row, "图标形状", out _shapeButton, "圆环");
        AddToggleRow(appearance, row, "图标显示百分比（电池图标时生效）", out _percentToggle);

        y += appearance.Height + S(16);

        var behavior = AddCard(page.Content, "行为", 4, y);
        row = S(46);
        row = AddValueRow(behavior, row, "刷新间隔", out _intervalButton, "2 秒");
        row = AddValueRow(behavior, row, "关机判定", out _offlineButton, "2 分钟");
        row = AddToggleRow(behavior, row, "开机自动启动", out _autostartToggle);
        AddToggleRow(behavior, row, "电量低于 20% 时通知", out _notifyToggle);

        _percentToggle.CheckedChanged += (_, _) => OnSettingChanged();
        _autostartToggle.CheckedChanged += (_, _) => OnSettingChanged();
        _notifyToggle.CheckedChanged += (_, _) => OnSettingChanged();

        _shapeButton.Click += (_, _) => Cycle(_shapeButton, ShapeValues, ShapeTexts, v => _app.Settings.IconShapeMode = v);
        _intervalButton.Click += (_, _) => Cycle(_intervalButton, IntervalValues, IntervalTexts, v => _app.Settings.IntervalSeconds = v);
        _offlineButton.Click += (_, _) => Cycle(_offlineButton, OfflineValues, OfflineTexts, v => _app.Settings.OfflineAsOffSeconds = v);

        return page;
    }

    // ---------- 关于页 ----------
    private ScrollHost BuildAboutPage()
    {
        var page = NewPage();
        float y = PlaceTitle(page.Content, "关于", "LogiBatteryTray");
        y += S(10);

        var card = NewCard(CardWidth(page), (int)S(170));
        card.Location = P(28, y);
        page.Content.Controls.Add(card);

        float cy = S(26);
        var title = AddLabel(card, 28, cy, "LogiBatteryTray", 13f, FontStyle.Bold, _fore);
        cy = title.Bottom + S(4);
        var ver = AddLabel(card, 28, cy, $"版本 {typeof(MainForm).Assembly.GetName().Version?.ToString(3)}", 9f, FontStyle.Regular, _secondary);
        cy = ver.Bottom + S(12);
        var desc = AddLabel(card, 28, cy, "在任务栏显示罗技鼠标电量，直接读取 HID++ 2.0，不需要 G HUB。", 8.5f, FontStyle.Regular, _secondary);
        cy = desc.Bottom + S(6);
        AddLabel(card, 28, cy, @"数据目录 %APPDATA%\LogiBatteryTray", 8.5f, FontStyle.Regular, _secondary);

        float buttonsY = y + S(192);
        var log = MakeButton(28, buttonsY, "打开日志", S(116));
        log.Parent = page.Content;
        log.Click += (_, _) => _app.OpenLogFolderPublic();

        var quit = MakeButton(156, buttonsY, "退出程序", S(116));
        quit.Parent = page.Content;
        quit.Click += (_, _) => { _app.ExitAppPublic(); Close(); };

        return page;
    }

    private CardPanel NewCard(int width, int height) => new()
    {
        Size = new Size(width, height),
        Radius = S(14),
        Surface = _cardColor,
        Outline = _border,
        Background = _background,
        Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
    };
    private int CardWidth(ScrollHost page) => Math.Max((int)S(420), page.Content.Width - (int)S(56));

    // ---------- 卡片与行 ----------
    private Panel AddCard(Control parent, string title, int rows, float y)
    {
        int height = (int)S(46 + rows * 52 + 8);
        var card = NewCard(Math.Max((int)S(420), parent.Width - (int)S(56)), height);
        card.Location = P(28, y);
        parent.Controls.Add(card);

        card.Controls.Add(new Label
        {
            Text = title,
            AutoSize = true,
            Location = P(20, 16),
            Font = new Font("Segoe UI", 10f, FontStyle.Bold),
            ForeColor = _fore,
            BackColor = Color.Transparent,
        });

        return card;
    }

    private float AddValueRow(Panel card, float y, string label, out ValueButton button, string value)
    {
        AddRowLabel(card, y, label);

        button = new ValueButton(_fore, ButtonBack, _border, ButtonHover)
        {
            Text = value,
            Size = new Size((int)S(168), (int)S(34)),
            Location = new Point(card.Width - (int)S(168) - (int)S(20), (int)(y + (S(52) - S(34)) / 2)),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
        };
        card.Controls.Add(button);

        return y + S(52);
    }

    private float AddToggleRow(Panel card, float y, string label, out ToggleSwitch toggle)
    {
        AddRowLabel(card, y, label);

        toggle = new ToggleSwitch(Accent, ToggleOff)
        {
            Size = new Size((int)S(46), (int)S(26)),
            Location = new Point(card.Width - (int)S(46) - (int)S(20), (int)(y + (S(52) - S(26)) / 2)),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
        };
        card.Controls.Add(toggle);

        return y + S(52);
    }

    private void AddRowLabel(Panel card, float y, string text)
    {
        card.Controls.Add(new Label
        {
            Text = text,
            AutoSize = true,
            Font = new Font("Segoe UI", 9.5f),
            ForeColor = _fore,
            BackColor = Color.Transparent,
            Location = new Point((int)S(20), (int)(y + (S(52) - S(22)) / 2)),
        });
    }

    private float PlaceTitle(Control parent, string title, string subtitle)
    {
        var titleLabel = AddLabel(parent, 28, 26, title, 17f, FontStyle.Bold, _fore);
        var subtitleLabel = AddLabel(parent, 30, titleLabel.Bottom + S(2), subtitle, 8.5f, FontStyle.Regular, _secondary);
        return subtitleLabel.Bottom + S(10);
    }

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

    private Button MakeButton(float x, float y, string text, float width, bool primary = false)
    {
        var button = new Button
        {
            Text = text,
            Location = P(x, y),
            Size = new Size((int)width, (int)S(34)),
            FlatStyle = FlatStyle.Flat,
            BackColor = primary ? Color.FromArgb(10, 132, 255) : ButtonBack,
            ForeColor = primary ? Color.White : _fore,
        };
        button.FlatAppearance.BorderColor = _border;
        return button;
    }

    private void PaintSidebar(object sender, PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

        using (var pen = new Pen(_border, 1f))
            e.Graphics.DrawLine(pen, _sidebar.Width - 0.5f, 0, _sidebar.Width - 0.5f, _sidebar.Height);

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

    private void Cycle(ValueButton button, int[] values, string[] texts, Action<int> apply)
    {
        if (_loading) return;

        int index = Array.IndexOf(texts, button.Text);
        if (index < 0) index = 0;

        int next = (index + 1) % values.Length;
        button.Text = texts[next];
        button.Invalidate();
        apply(values[next]);

        _app.ApplySettingsFromWindow();
        UpdateView();
    }

    private void LoadFromSettings()
    {
        _loading = true;

        var settings = _app.Settings;
        _autostartToggle.Checked = AppSettings.IsAutostartEnabled();
        _notifyToggle.Checked = settings.LowBatteryNotify;
        _percentToggle.Checked = settings.ShowPercentInIcon;

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
        settings.LowBatteryNotify = _notifyToggle.Checked;
        settings.ShowPercentInIcon = _percentToggle.Checked;

        AppSettings.SetAutostart(_autostartToggle.Checked);
        settings.Autostart = _autostartToggle.Checked;

        _app.ApplySettingsFromWindow();
        UpdateView();
    }

    public void SelectPage(int index) => ShowPage(index);

    public void UpdateView()
    {
        bool receiverConnected = _app.Service.ReceiverConnected;
        _devicePage.Content.Visible = receiverConnected;
        _emptyState.Visible = !receiverConnected;

        var device = _app.DisplayDevice;
        var battery = device?.Battery;
        bool online = device != null && device.Online && battery != null;

        string offlineLabel = device?.PoweredOff == true ? "停用" : "休眠";
        string status = online
            ? battery!.StatusText
            : battery?.Percent != null ? $"{offlineLabel} · 上次 {battery.Percent}%"
            : receiverConnected ? "未连接" : "请连接设备";

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

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (_sidebar == null || _pages == null) return;

        _sidebar.Height = ClientSize.Height;

        foreach (var page in _pages)
        {
            page.Location = new Point(SidebarWidth, 0);
            page.Size = new Size(ClientSize.Width - SidebarWidth, ClientSize.Height);
        }
    }

    /// <summary>把滚轮接管到当前可见页面。</summary>
    public bool PreFilterMessage(ref Message m)
    {
        const int WmMouseWheel = 0x020A;
        if (m.Msg != WmMouseWheel || !Visible) return false;
        if (!ClientRectangle.Contains(PointToClient(Cursor.Position))) return false;

        var page = _pages.FirstOrDefault(p => p.Visible);
        if (page == null) return false;

        int delta = (short)((m.WParam.ToInt64() >> 16) & 0xFFFF);
        if (delta == 0) return false;

        page.ScrollBy(delta > 0 ? -(int)S(90) : (int)S(90));
        return true;
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        Application.RemoveMessageFilter(this);
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
