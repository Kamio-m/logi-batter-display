using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

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

    private readonly List<DeviceCard> _deviceCards = new();
    private Panel _deviceList;
    private Panel _actionsPanel;
    private Panel _emptyState;
    private ScrollHost _devicePage;
    private string _cardsSignature = "";

    private ToggleSwitch _autostartToggle;
    private ToggleSwitch _notifyToggle;
    private ValueButton _shapeButton;
    private ValueButton _intervalButton;
    private ValueButton _offlineButton;
    private ValueButton _deviceButton;
    private readonly List<(string Key, string Label)> _deviceOptions = new();
    private int _deviceIndex;

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
    private static readonly int[] ShapeValues = { 0, 1, 2 };
    private static readonly string[] ShapeTexts = { "圆环", "电池图标", "百分比" };
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

        Text = "";
        ShowIcon = false;
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = _background;
        ForeColor = _fore;
        Font = new Font("Segoe UI", 9f);
        AutoScaleMode = AutoScaleMode.None;   // 位置由 S() 手动按 DPI 计算，不能再让 WinForms 缩放一次
        AutoScaleDimensions = new SizeF(96f, 96f);
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

    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmUseImmersiveDarkModeLegacy = 19;
    private const int DwmBorderColor = 34;

    [DllImport("dwmapi.dll", SetLastError = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
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

        float y = PlaceTitle(page.Content, "设备", "查看所有已连接的罗技设备，点卡片可切换托盘显示");
        y += S(12);

        _deviceList = new Panel
        {
            Location = P(0, y),
            Size = new Size(page.Content.Width, (int)S(10)),
            BackColor = Color.Transparent,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };
        page.Content.Controls.Add(_deviceList);

        _actionsPanel = new Panel
        {
            Location = P(0, y),
            Size = new Size(page.Content.Width, (int)S(78)),
            BackColor = Color.Transparent,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };
        page.Content.Controls.Add(_actionsPanel);

        AddLabel(_actionsPanel, 28, 0, "快捷操作", 9.5f, FontStyle.Bold, _fore);

        var refresh = MakeButton(28, S(26), "立即刷新", S(116), true);
        refresh.Parent = _actionsPanel;
        refresh.Click += (_, _) => _app.RefreshNow();

        var log = MakeButton(156, S(26), "打开日志", S(116));
        log.Parent = _actionsPanel;
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

    private static string DeviceKeyOf(LogiDevice device) => device.Path + "#" + device.Name;

    /// <summary>设备列表变化时重建卡片。</summary>
    private void RebuildDeviceCards(IReadOnlyList<LogiDevice> devices)
    {
        foreach (var card in _deviceCards)
        {
            _deviceList.Controls.Remove(card);
            card.Dispose();
        }
        _deviceCards.Clear();

        float top = 0;
        int cardWidth = Math.Max((int)S(420), _deviceList.Width - (int)S(56));

        foreach (var device in devices)
        {
            var card = new DeviceCard(_cardColor, _border, _background, _fore, _secondary, _light)
            {
                Location = new Point((int)S(28), (int)top),
                Size = new Size(cardWidth, (int)S(152)),
                Radius = S(14),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            };

            card.SetDevice(device);
            card.Selected = DeviceKeyOf(device) == SelectedKey();

            string key = DeviceKeyOf(device);
            void Choose(object _, EventArgs __) => ChooseDevice(key);

            card.Click += Choose;
            foreach (Control child in card.Controls)
                child.Click += Choose;

            _deviceList.Controls.Add(card);
            _deviceCards.Add(card);

            top += S(152) + S(12);
        }

        _deviceList.Height = (int)Math.Max(top, S(10));

        if (_actionsPanel != null)
        {
            _actionsPanel.Top = _deviceList.Bottom + (int)S(18);
        }
    }

    private string SelectedKey()
    {
        string key = _app.Settings.SelectedDeviceKey ?? "";
        return key.Length > 0 ? key : DeviceKeyOf(_app.DisplayDevice ?? new LogiDevice { Path = "", Name = "" });
    }

    private void ChooseDevice(string key)
    {
        if (string.IsNullOrEmpty(key)) return;

        _app.Settings.SelectedDeviceKey = key;
        _app.ApplySettingsFromWindow();
        UpdateView();
    }

    // ---------- 设置页 ----------
    private ScrollHost BuildSettingsPage()
    {
        var page = NewPage();
        float y = PlaceTitle(page.Content, "设置", "所有修改都会立即生效");
        y += S(14);

        var tray = AddCard(page.Content, "托盘", 1, y);
        AddValueRow(tray, S(46), "显示设备", out _deviceButton, "—");
        y += tray.Height + S(16);

        var appearance = AddCard(page.Content, "外观", 1, y);
        float row = S(46);
        AddValueRow(appearance, row, "图标形状", out _shapeButton, "圆环");

        y += appearance.Height + S(16);

        var behavior = AddCard(page.Content, "行为", 4, y);
        row = S(46);
        row = AddValueRow(behavior, row, "刷新间隔", out _intervalButton, "2 秒");
        row = AddValueRow(behavior, row, "关机判定", out _offlineButton, "2 分钟");
        row = AddToggleRow(behavior, row, "开机自动启动", out _autostartToggle);
        AddToggleRow(behavior, row, "电量低于 20% 时通知", out _notifyToggle);

        _deviceButton.Click += (_, _) => CycleDevice();

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

        var card = NewCard(Math.Max((int)S(420), page.Content.Width - (int)S(56)), (int)S(170));
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
        quit.Click += (_, _) => _app.ExitAppPublic();
        quit.Click += (_, _) => Close();

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

    private void RebuildDeviceOptions()
    {
        var devices = _app.Service.Devices;
        string selected = _app.Settings.SelectedDeviceKey ?? "";

        _deviceOptions.Clear();

        if (devices.Count == 0)
        {
            _deviceOptions.Add((string.Empty, "未检测到设备"));
        }
        else if (devices.Count == 1)
        {
            _deviceOptions.Add((DeviceKeyOf(devices[0]), devices[0].Name));
        }
        else
        {
            _deviceOptions.Add(("auto", "自动（电量最低）"));
            foreach (var device in devices)
                _deviceOptions.Add((DeviceKeyOf(device), device.Name));
        }

        int index = _deviceOptions.FindIndex(option => option.Key == selected);
        if (index < 0) index = 0;

        _deviceIndex = index;
        _deviceButton.Text = _deviceOptions[index].Label;
        _deviceButton.Invalidate();
    }

    private void CycleDevice()
    {
        if (_loading || _deviceOptions.Count <= 1) return;

        _deviceIndex = (_deviceIndex + 1) % _deviceOptions.Count;
        var option = _deviceOptions[_deviceIndex];

        _deviceButton.Text = option.Label;
        _deviceButton.Invalidate();

        _app.Settings.SelectedDeviceKey = option.Key;
        _app.ApplySettingsFromWindow();
        UpdateView();
    }

    private void LoadFromSettings()
    {
        _loading = true;

        var settings = _app.Settings;
        _autostartToggle.Checked = AppSettings.IsAutostartEnabled();
        _notifyToggle.Checked = settings.LowBatteryNotify;

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

        var devices = _app.Service.Devices;

        string signature = string.Join("|", devices.Select(DeviceKeyOf));
        if (signature != _cardsSignature)
        {
            _cardsSignature = signature;
            RebuildDeviceCards(devices);
        }

        foreach (var card in _deviceCards)
        {
            var device = devices.FirstOrDefault(d => DeviceKeyOf(d) == card.Key);
            if (device != null) card.SetDevice(device);
            card.Selected = card.Key == SelectedKey();
        }

        if (_deviceButton != null) RebuildDeviceOptions();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyTitleBarTheme();
    }

    /// <summary>让标题栏跟随界面深/浅色，边框色也与卡片一致。</summary>
    private void ApplyTitleBarTheme()
    {
        if (Handle == IntPtr.Zero) return;

        int dark = _light ? 0 : 1;
        if (DwmSetWindowAttribute(Handle, DwmUseImmersiveDarkMode, ref dark, sizeof(int)) != 0)
            DwmSetWindowAttribute(Handle, DwmUseImmersiveDarkModeLegacy, ref dark, sizeof(int));

        int border = ColorTranslator.ToWin32(_background);
        DwmSetWindowAttribute(Handle, DwmBorderColor, ref border, sizeof(int));
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
        base.OnFormClosed(e);
    }

    /// <summary>设备页里的单台设备卡片。</summary>
    private sealed class DeviceCard : CardPanel
    {
        private readonly Label _name;
        private readonly Label _status;
        private readonly Label _percent;
        private readonly Label _updated;
        private readonly Label _badge;
        private readonly PictureBox _ring;
        private readonly Label _bigPercent;
        private readonly Color _fore;
        private readonly Color _secondary;
        private readonly Color _accentColor = Accent;
        private readonly bool _light;
        private BatteryReading _battery;
        private bool _online;
        private bool _selected;
        private bool _percentMode;

        public DeviceCard(Color surface, Color outline, Color background, Color fore, Color secondary, bool light)
        {
            Surface = surface;
            Outline = outline;
            Background = background;
            _fore = fore;
            _secondary = secondary;
            _light = light;

            _ring = new PictureBox
            {
                Location = new Point((int)(22 * Dpi), (int)(22 * Dpi)),
                Size = new Size((int)(88 * Dpi), (int)(88 * Dpi)),
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = Color.Transparent,
            };
            Controls.Add(_ring);

            _bigPercent = new Label
            {
                AutoSize = false,
                Location = _ring.Location,
                Size = _ring.Size,
                TextAlign = ContentAlignment.MiddleCenter,
                Font = new Font("Segoe UI", 32f, FontStyle.Bold),
                ForeColor = fore,
                BackColor = Color.Transparent,
                Visible = false,
            };
            Controls.Add(_bigPercent);

            _name = MakeLabel(126, 18, 12.5f, FontStyle.Bold, fore);
            _status = MakeLabel(126, 42, 9f, FontStyle.Regular, secondary);
            _percent = MakeLabel(126, 62, 22f, FontStyle.Bold, fore);
            _updated = MakeLabel(126, 98, 8f, FontStyle.Regular, secondary);

            LayoutLabels();

            _badge = new Label
            {
                AutoSize = true,
                Font = new Font("Segoe UI", 8f, FontStyle.Bold),
                ForeColor = Color.White,
                BackColor = _accentColor,
                Padding = new Padding((int)(6 * Dpi), (int)(2 * Dpi), (int)(6 * Dpi), (int)(2 * Dpi)),
                Location = new Point(Width - (int)(96 * Dpi), (int)(16 * Dpi)),
                Visible = false,
                Text = "托盘显示",
            };
            Controls.Add(_badge);

            foreach (Control child in Controls) child.Cursor = Cursors.Hand;
            Cursor = Cursors.Hand;
        }

        [System.ComponentModel.Browsable(false)]
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public string Key { get; set; } = "";

        private float Dpi => DeviceDpi / 96f;

        private Label MakeLabel(float x, float y, float size, FontStyle style, Color color)
        {
            var label = new Label
            {
                AutoSize = true,
                Location = new Point((int)(x * Dpi), (int)(y * Dpi)),
                Font = new Font("Segoe UI", size, style),
                ForeColor = color,
                BackColor = Color.Transparent,
            };
            Controls.Add(label);
            return label;
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
                Outline = value ? _accentColor : Outline;
                Invalidate();
                _badge.Visible = value;
                _badge.Location = new Point(Width - _badge.Width - (int)(16 * Dpi), (int)(16 * Dpi));
            }
        }

        public void SetDevice(LogiDevice device)
        {
            if (Key.Length == 0) Key = device.Path + "#" + device.Name;

            _battery = device.Battery;
            _online = device.Online && _battery != null;

            _name.Text = device.Name;
            _name.MaximumSize = new Size(Math.Max((int)(80 * Dpi), Width - (int)(126 * Dpi) - (int)(16 * Dpi)), 0);

            string offlineLabel = device.PoweredOff ? "停用" : "休眠";
            _status.Text = _online
                ? _battery!.StatusText
                : _battery?.Percent != null ? $"{offlineLabel} · 上次 {_battery.Percent}%"
                : "未连接";

            _percent.Text = _battery?.Percent is int percent ? $"{percent}%" : "--";
            _percent.ForeColor = _online ? IconFactory.LevelColor(_battery!) : _secondary;

            // 百分比样式下，左侧用文字标签显示大号数字（和界面字体一致），右侧不再重复
            bool percentShape = IconFactory.Shape == IconShape.Percent;
            _percentMode = percentShape;
            _percent.Visible = !percentShape;
            _ring.Visible = !percentShape;
            _bigPercent.Visible = percentShape;

            if (percentShape)
            {
                _bigPercent.Text = _battery?.Percent is int bigValue ? bigValue.ToString() : "--";
                _bigPercent.ForeColor = _online ? IconFactory.LevelColor(_battery!) : _secondary;
            }

            _updated.Text = device.UpdatedAt is DateTime updatedAt ? $"更新于 {updatedAt:HH:mm:ss}" : "";

            int size = (int)(88 * Dpi);
            var previous = _ring.Image;
            try
            {
                using var icon = IconFactory.Create(size, _battery, _light, device.Online == false);
                _ring.Image = icon.ToBitmap();
            }
            catch
            {
                // 保留原图
            }
            previous?.Dispose();

            LayoutLabels();
        }

        /// <summary>按真实文字高度依次往下排，避免不同 DPI 下重叠。</summary>
        private void LayoutLabels()
        {
            if (_name == null) return;

            int x = (int)(126 * Dpi);
            float y = 18 * Dpi;

            _name.Location = new Point(x, (int)y);
            y = _name.Bottom + 2 * Dpi;

            _status.Location = new Point(x, (int)y);
            y = _status.Bottom + 2 * Dpi;

            if (!_percentMode)
            {
                _percent.Location = new Point(x, (int)y);
                y = _percent.Bottom + 2 * Dpi;
            }
            else
            {
                y += 2 * Dpi;
            }

            _updated.Location = new Point(x, (int)y);
            _updated.Text = string.IsNullOrEmpty(_updated.Text) && _online ? "更新于 --:--:--" : _updated.Text;

            // 标签移动后整块重绘，否则会留下旧文字的残影
            Invalidate(true);
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (Visible) LayoutLabels();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            LayoutLabels();

            if (_badge != null)
                _badge.Location = new Point(Width - _badge.Width - (int)(16 * Dpi), (int)(16 * Dpi));
            if (_name != null)
                _name.MaximumSize = new Size(Math.Max((int)(80 * Dpi), Width - (int)(126 * Dpi) - (int)(16 * Dpi)), 0);
        }
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
        private bool _percentMode;

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
            {
                TextRenderer.DrawText(g, _glyph, iconFont, new Rectangle(12, 0, 24, Height), iconBrush.Color,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }

            using (var textFont = new Font("Segoe UI", 9.5f, _selected ? FontStyle.Bold : FontStyle.Regular))
            {
                var color = _selected ? Color.White : (_hover ? _fore : _muted);
                TextRenderer.DrawText(g, Text, textFont, new Rectangle(46, 0, Width - 50, Height), color,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
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
