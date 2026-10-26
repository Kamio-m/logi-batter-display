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
    void ReopenWindow();
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
    private Panel _manualList;
    private Button _addManualButton;
    private readonly List<ManualCard> _manualCards = new();
    private string _manualSignature = "";
    private Panel _actionsPanel;
    private Panel _emptyState;
    private ScrollHost _devicePage;
    private string _cardsSignature = "";
    private int _listTopY;
    private IconShape _lastShape = IconFactory.Shape;

    private ToggleSwitch _autostartToggle;
    private ToggleSwitch _notifyToggle;
    private ValueButton _shapeButton;
    private ValueButton _intervalButton;
    private ValueButton _offlineButton;
    private ValueButton _deviceButton;
    private ValueButton _themeButton;
    private ValueButton _opacityButton;
    private ValueButton _cornerButton;
    private ValueButton _accentButton;
    private readonly List<(string Key, string Label)> _deviceOptions = new();
    private int _deviceIndex;

    private readonly Color _background;
    private readonly Color _sidebarColor;
    private readonly Color _cardColor;
    private readonly Color _border;
    private readonly Color _fore;
    private readonly Color _secondary;
    private Color _accent;
    private readonly List<Button> _accentButtons = new();

    private bool _loading;

    private static readonly int[] IntervalValues = { 1, 2, 5, 10, 30, 60 };
    private static readonly string[] IntervalTexts = { "1 秒（实时）", "2 秒", "5 秒", "10 秒", "30 秒", "1 分钟" };
    private static readonly int[] ShapeValues = { 0, 1, 2 };
    private static readonly string[] ShapeTexts = { "圆环", "电池图标", "百分比" };
    private static readonly int[] ThemeValues = { 0, 1, 2 };
    private static readonly string[] ThemeTexts = { "跟随系统", "黑色", "白色" };
    private static readonly int[] OpacityValues = { 100, 95, 90, 85 };
    private static readonly string[] OpacityTexts = { "100%", "95%", "90%", "85%" };
    private static readonly int[] CornerValues = { 8, 12, 16 };
    private static readonly string[] CornerTexts = { "小", "中", "大" };
    private static readonly int[] OfflineValues = { 0, 30, 60, 120, 300, 600 };
    private static readonly string[] OfflineTexts = { "立即", "30 秒", "1 分钟", "2 分钟", "5 分钟", "10 分钟" };

    public MainForm(IMainFormHost app)
    {
        _app = app;
        _accent = Theme.AccentFor(app.Settings.AccentIndex);
        _light = app.Settings.ThemeMode switch
        {
            1 => false,
            2 => true,
            _ => Theme.IsLightTaskbar(),
        };

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
            new NavButton("\uE83F", "设备", _fore, _secondary, _accent) { Location = P(14, navTop) },
            new NavButton("\uE713", "设置", _fore, _secondary, _accent) { Location = P(14, navTop + 46) },
            new NavButton("\uE946", "关于", _fore, _secondary, _accent) { Location = P(14, navTop + 92) },
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

    /// <summary>卡片之间的统一间距。</summary>
    private int CardGap => (int)S(12);

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

        float y = PlaceTitle(page.Content, "设备", "点设备卡片可切换图标样式（圆环 / 电池图标 / 百分比）");
        y += S(12);

        _listTopY = (int)y;
        _deviceList = new Panel
        {
            Location = P(0, y),
            Size = new Size(page.Content.Width, (int)S(10)),
            BackColor = Color.Transparent,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };
        page.Content.Controls.Add(_deviceList);

        _manualList = new Panel
        {
            Location = P(0, y),
            Size = new Size(page.Content.Width, (int)S(10)),
            BackColor = Color.Transparent,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
        };
        page.Content.Controls.Add(_manualList);

        _addManualButton = new Button
        {
            Text = "＋ 添加估算设备",
            Size = new Size((int)S(148), (int)S(36)),
            FlatStyle = FlatStyle.Flat,
            BackColor = _cardColor,
            ForeColor = _accent,
            Visible = false,
        };
        _addManualButton.FlatAppearance.BorderColor = _border;
        _addManualButton.Click += (_, _) => AddManualDevice();
        page.Content.Controls.Add(_addManualButton);

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
            var card = new DeviceCard(_cardColor, _border, _background, _fore, _secondary, _light, _accent)
            {
                Location = new Point((int)S(28), (int)top),
                Size = new Size(cardWidth, (int)S(152)),
                Radius = S(14),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            };

            card.SetDevice(device);
            card.Selected = MatchesKey(device, SelectedKey());

            void CycleStyle(object _, EventArgs __) => CycleCardShape();

            card.Click += CycleStyle;
            foreach (Control child in card.Controls)
                child.Click += CycleStyle;

            _deviceList.Controls.Add(card);
            _deviceCards.Add(card);

            top += S(152) + S(12);
        }

        _deviceList.Height = (int)Math.Max(top, S(10));

        if (_manualList != null)
        {
            _manualList.Top = _deviceList.Bottom + CardGap;
            _manualList.Height = Math.Max((int)S(10), _manualList.Height);
        }

        if (_actionsPanel != null)
        {
            _actionsPanel.Top = (_manualList == null ? _deviceList.Bottom : _manualList.Bottom) + CardGap;
        }
    }

    private string SelectedKey()
    {
        string key = _app.Settings.SelectedDeviceKey ?? "";
        return key.Length > 0 ? key : DeviceKeyOf(_app.DisplayDevice ?? new LogiDevice { Path = "", Name = "" });
    }

    /// <summary>点设备卡片循环切换图标样式，所有设备一起变。</summary>
    private void CycleCardShape()
    {
        int next = IconFactory.Shape switch
        {
            IconShape.Ring => 1,
            IconShape.Battery => 2,
            _ => 0,
        };

        _app.Settings.IconShapeMode = next;
        IconFactory.Shape = (IconShape)next;

        if (_shapeButton != null)
        {
            _shapeButton.Text = ShapeTexts[IndexOr(ShapeValues, next, 0)];
            _shapeButton.Invalidate();
        }

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

        var appearance = AddCard(page.Content, "外观", 3, y);
        float row = S(46);
        row = AddValueRow(appearance, row, "主题", out _themeButton, "跟随系统");
        row = AddValueRow(appearance, row, "图标形状", out _shapeButton, "圆环");
        AddValueRow(appearance, row, "主题色", out _accentButton, "蓝色");

        y += appearance.Height + S(16);

        var menuCard = AddCard(page.Content, "右键菜单", 2, y);
        float menuRow = S(46);
        menuRow = AddValueRow(menuCard, menuRow, "不透明度", out _opacityButton, "100%");
        AddValueRow(menuCard, menuRow, "圆角", out _cornerButton, "中");

        y += menuCard.Height + S(16);

        var behavior = AddCard(page.Content, "行为", 4, y);
        row = S(46);
        row = AddValueRow(behavior, row, "刷新间隔", out _intervalButton, "2 秒");
        row = AddValueRow(behavior, row, "关机判定", out _offlineButton, "2 分钟");
        row = AddToggleRow(behavior, row, "开机自动启动", out _autostartToggle);
        AddToggleRow(behavior, row, "电量低于 20% 时通知", out _notifyToggle);

        _deviceButton.Click += (_, _) => CycleDevice();

        _accentButton.Click += (_, _) => CycleAccent();

        _themeButton.Click += (_, _) =>
        {
            int next = (_app.Settings.ThemeMode + 1) % ThemeValues.Length;
            _themeButton.Text = ThemeTexts[next];
            _app.Settings.ThemeMode = next;
            _app.ApplySettingsFromWindow();
            _app.ReopenWindow();   // 黑白配色在构造时确定，重开一次窗口
        };
        _opacityButton.Click += (_, _) => Cycle(_opacityButton, OpacityValues, OpacityTexts, v => _app.Settings.MenuOpacity = v);
        _cornerButton.Click += (_, _) => Cycle(_cornerButton, CornerValues, CornerTexts, v => _app.Settings.MenuCorner = v);

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

        string buildText = "";
        try { buildText = $"构建 {System.IO.File.GetLastWriteTime(Application.ExecutablePath):MM-dd HH:mm}"; } catch { }
        var build = AddLabel(card, 28, ver.Bottom - S(2), buildText, 8f, FontStyle.Regular, _secondary);
        build.Location = new Point((int)(28 + ver.Width + (int)S(12)), build.Left);

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

        toggle = new ToggleSwitch(_accent, ToggleOff)
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
            BackColor = primary ? _accent : ButtonBack,
            ForeColor = primary ? Color.White : _fore,
        };
        button.FlatAppearance.BorderColor = _border;
        if (primary) _accentButtons.Add(button);
        return button;
    }

    private void PaintSidebar(object sender, PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;

        using (var pen = new Pen(_border, 1f))
            e.Graphics.DrawLine(pen, _sidebar.Width - 0.5f, 0, _sidebar.Width - 0.5f, _sidebar.Height);

        var logoRect = new RectangleF(S(22), S(24), S(28), S(28));
        using (var pen = new Pen(_accent, S(3.4f)))
            e.Graphics.DrawEllipse(pen, logoRect);
        using (var brush = new SolidBrush(_accent))
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

    /// <summary>把「托盘正在显示的那一组」排到最上面。</summary>
    /// <summary>匹配设备 key：优先完全相等，路径变化时退化为按名字匹配。</summary>
    private static bool MatchesKey(LogiDevice device, string key)
    {
        if (string.IsNullOrEmpty(key)) return false;
        if (DeviceKeyOf(device) == key) return true;

        int hash = key.LastIndexOf('#');
        return hash >= 0 && hash + 1 < key.Length && device.Name == key[(hash + 1)..];
    }
    private void ArrangeLists()
    {
        if (_deviceList == null || _manualList == null) return;

        // 每次按实际卡片算高度，杜绝残留状态
        _manualList.Height = _manualCards.Count == 0 ? 0 : _manualCards.Max(c => c.Bottom);
        _deviceList.Height = _deviceCards.Count == 0 ? 0 : _deviceCards.Max(c => c.Bottom);

        bool manualFirst = (_app.Settings.SelectedDeviceKey ?? "").StartsWith("manual:", StringComparison.OrdinalIgnoreCase)
                           && _manualCards.Count > 0;

        int gap = CardGap;
        int top = _listTopY;
        bool hasDevice = _deviceCards.Count > 0;
        bool hasManual = _manualCards.Count > 0;

        if (manualFirst)
        {
            _manualList.Top = top;
            if (hasDevice) _deviceList.Top = _manualList.Bottom + gap;
            else _deviceList.Top = top;
        }
        else
        {
            _deviceList.Top = top;
            if (hasManual) _manualList.Top = hasDevice ? _deviceList.Bottom + gap : top;
            else _manualList.Top = top;
        }

        int bottom = Math.Max(hasManual ? _manualList.Bottom : top, hasDevice ? _deviceList.Bottom : top);

        if (_addManualButton != null)
            _addManualButton.Top = bottom + gap;

        if (_actionsPanel != null)
            _actionsPanel.Top = (_addManualButton == null ? bottom : _addManualButton.Bottom) + gap;

        _devicePage?.PerformLayout();
    }
    /// <summary>重建「估算设备」卡片（硬件读不到电量的设备）。</summary>
    private void RebuildManualCards()
    {
        if (_manualList == null) return;

        var devices = _app.Settings.ManualDevices ?? new List<ManualBatteryDevice>();
        string selectedManualKey = _app.Settings.SelectedDeviceKey ?? "";

        // 选中的估算设备排到最前面
        var ordered = devices
            .OrderByDescending(d => selectedManualKey == "manual:" + d.Name)
            .ToList();

        string signature = string.Join("|", ordered.Select(d => $"{d.Name}#{d.LifeDays}#{d.LastChargedUtc.Ticks}"))
                           + "|sel:" + selectedManualKey;

        if (signature != _manualSignature)
        {
            _manualSignature = signature;

            foreach (var card in _manualCards) { _manualList.Controls.Remove(card); card.Dispose(); }
            _manualCards.Clear();

            string selectedManual = selectedManualKey;
            float top = 0;
            foreach (var device in ordered)
            {
                var card = new ManualCard(_cardColor, _border, _background, _fore, _secondary, _accent, _light, device)
                {
                    Location = new Point((int)S(28), (int)top),
                    Size = new Size(Math.Max((int)S(420), _manualList.Width - (int)S(56)), (int)S(152)),
                    Radius = S(14),
                    Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                    Selected = selectedManual == "manual:" + device.Name,
                };

                card.RefreshRequested += (_, _) =>
                {
                    device.LastChargedUtc = DateTime.UtcNow;
                    _app.ApplySettingsFromWindow();
                    _manualSignature = "";
                    UpdateView();
                };

                card.DeleteRequested += (_, _) =>
                {
                    _app.Settings.ManualDevices.Remove(device);
                    _app.ApplySettingsFromWindow();
                    _manualSignature = "";
                    UpdateView();
                };

                _manualList.Controls.Add(card);
                _manualCards.Add(card);
                top += S(152) + CardGap;
            }

            _manualList.Height = (int)Math.Max(top, S(10));

            if (_addManualButton != null)
            {
                _addManualButton.Location = new Point((int)S(28), _manualList.Bottom + CardGap);
                _addManualButton.Visible = true;
            }

            if (_actionsPanel != null)
                _actionsPanel.Top = (_addManualButton == null ? _manualList.Bottom : _addManualButton.Bottom) + CardGap;

            _manualList.Parent?.PerformLayout();
        }

        string currentManualKey = _app.Settings.SelectedDeviceKey ?? "";
        foreach (var card in _manualCards)
        {
            card.Selected = currentManualKey == "manual:" + card.DeviceName;
            card.RefreshNow();
        }
    }

    /// <summary>添加一台只能在软件里估算电量的设备。</summary>
    private void AddManualDevice()
    {
        using var dialog = new Form
        {
            Text = "添加估算设备",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            ClientSize = new Size(360, 190),
            BackColor = _background,
            ForeColor = _fore,
            Font = new Font("Segoe UI", 9f),
            AutoScaleMode = AutoScaleMode.None,
            MaximizeBox = false,
            MinimizeBox = false,
        };

        var nameLabel = new Label { Text = "设备名称", AutoSize = true, Location = new Point(20, 22), ForeColor = _secondary, BackColor = Color.Transparent };
        var nameBox = new TextBox { Text = "键盘", Location = new Point(20, 44), Width = 310 };

        var daysLabel = new Label { Text = "续航天数（充满到没电）", AutoSize = true, Location = new Point(20, 82), ForeColor = _secondary, BackColor = Color.Transparent };
        var daysBox = new NumericUpDown { Minimum = 1, Maximum = 3650, Value = 60, Location = new Point(20, 104), Width = 120 };

        var ok = new Button { Text = "添加", Location = new Point(170, 145), Size = new Size(76, 30), DialogResult = DialogResult.OK, FlatStyle = FlatStyle.Flat };
        var cancel = new Button { Text = "取消", Location = new Point(254, 145), Size = new Size(76, 30), DialogResult = DialogResult.Cancel, FlatStyle = FlatStyle.Flat };

        dialog.Controls.AddRange(new Control[] { nameLabel, nameBox, daysLabel, daysBox, ok, cancel });
        dialog.AcceptButton = ok;
        dialog.CancelButton = cancel;

        if (dialog.ShowDialog(this) != DialogResult.OK) return;

        string name = nameBox.Text.Trim();
        if (name.Length == 0) name = "设备";

        _app.Settings.ManualDevices.Add(new ManualBatteryDevice
        {
            Name = name,
            LifeDays = (int)daysBox.Value,
            LastChargedUtc = DateTime.UtcNow,
        });

        if (_app.Service.Devices.Count == 0)
            _app.Settings.SelectedDeviceKey = "manual:" + name;
        _app.ApplySettingsFromWindow();
        _manualSignature = "";
        UpdateView();
    }

    /// <summary>估算设备卡片：结构和设备卡完全一致。</summary>
    private sealed class ManualCard : CardPanel
    {
        private readonly Label _name;
        private readonly Label _status;
        private readonly Label _percent;
        private readonly Label _detail;
        private readonly Label _badge;
        private readonly PictureBox _icon;
        private readonly ManualBatteryDevice _device;
        private readonly Color _accent;
        private readonly Color _borderRestore;
        private readonly bool _light;
        private bool _selected;
        private bool _percentShape;
        private Action _relayout;

        public event EventHandler RefreshRequested;
        public event EventHandler DeleteRequested;

        public string DeviceName => _device.Name;

        public ManualCard(Color surface, Color outline, Color background, Color fore, Color secondary,
            Color accent, bool light, ManualBatteryDevice device)
        {
            Surface = surface;
            Outline = outline;
            Background = background;
            _device = device;
            _accent = accent;
            _borderRestore = outline;
            _light = light;

            _icon = new PictureBox
            {
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = surface,
            };

            _name = new Label
            {
                AutoSize = false,
                AutoEllipsis = true,
                Font = new Font("Segoe UI", 12.5f, FontStyle.Bold),
                ForeColor = fore,
                BackColor = Color.Transparent,
            };

            _status = new Label
            {
                AutoSize = false,
                AutoEllipsis = true,
                Font = new Font("Segoe UI", 9f),
                ForeColor = secondary,
                BackColor = Color.Transparent,
            };

            _percent = new Label
            {
                AutoSize = true,
                Font = new Font("Segoe UI", 22f, FontStyle.Bold),
                ForeColor = accent,
                BackColor = Color.Transparent,
            };

            _detail = new Label
            {
                AutoSize = false,
                AutoEllipsis = true,
                Font = new Font("Segoe UI", 8f),
                ForeColor = secondary,
                BackColor = Color.Transparent,
            };

            var refresh = new Button
            {
                Text = "刚充满",
                Size = new Size(84, 32),   // 实际尺寸在 Layout 里按 DPI 设置
                FlatStyle = FlatStyle.Flat,
                BackColor = accent,
                ForeColor = Color.White,
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
            };
            refresh.FlatAppearance.BorderColor = accent;
            refresh.Click += (_, _) => RefreshRequested?.Invoke(this, EventArgs.Empty);

            var remove = new Button
            {
                Text = "删除",
                Size = new Size(64, 32),
                FlatStyle = FlatStyle.Flat,
                ForeColor = fore,
                BackColor = light ? Color.FromArgb(243, 243, 247) : Color.FromArgb(48, 48, 54),
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
            };
            remove.FlatAppearance.BorderColor = outline;
            remove.Click += (_, _) => DeleteRequested?.Invoke(this, EventArgs.Empty);

            _badge = new Label
            {
                AutoSize = true,
                Font = new Font("Segoe UI", 8f, FontStyle.Bold),
                ForeColor = Color.White,
                BackColor = _accent,
                Padding = new Padding(6, 2, 6, 2),
                Visible = false,
                Text = "托盘显示",
            };

            Controls.Add(_icon);
            Controls.Add(_name);
            Controls.Add(_status);
            Controls.Add(_percent);
            Controls.Add(_detail);
            Controls.Add(refresh);
            Controls.Add(remove);
            Controls.Add(_badge);

            void Layout()
            {
                float dpi = DeviceDpi / 96f;
                int textX = (int)(126 * dpi);
                int reserve = (int)(84 + 16 + 64 + 8 + 16);
                int maxWidth = Math.Max((int)(120 * dpi), Width - textX - reserve);

                int iconSize = (int)((_selected ? 88 : 64) * dpi);
                int iconInset = (int)((_selected ? 22 : 16) * dpi);
                _icon.Location = new Point(iconInset, iconInset);
                _icon.Size = new Size(iconSize, iconSize);

                refresh.Size = new Size((int)(84 * dpi), (int)(32 * dpi));
                remove.Size = new Size((int)(64 * dpi), (int)(32 * dpi));
                refresh.Font = new Font("Segoe UI", 9f);
                remove.Font = new Font("Segoe UI", 9f);

                _name.Size = new Size(maxWidth, (int)(24 * dpi));
                _status.Size = new Size(maxWidth, (int)(18 * dpi));
                _detail.Size = new Size(maxWidth, (int)(16 * dpi));

                bool percentShape = IconFactory.Shape == IconShape.Percent;
                _percentShape = percentShape;

                int columnX = percentShape ? (int)(22 * dpi) : textX;
                _name.Location = new Point(columnX, (int)(18 * dpi));
                _status.Location = new Point(columnX, _name.Bottom + (int)(2 * dpi));

                if (percentShape)
                {
                    // 百分比样式：隐藏图标，数字与文字同一列
                    _icon.Visible = false;
                    _percent.Font = new Font("Segoe UI", _selected ? 30f : 26f, FontStyle.Bold);
                    _percent.Location = new Point((int)(22 * dpi), _status.Bottom + (int)(2 * dpi));
                    _detail.Location = new Point((int)(22 * dpi), _percent.Bottom + (int)(2 * dpi));
                }
                else
                {
                    _icon.Visible = true;
                    _percent.Font = new Font("Segoe UI", 22f, FontStyle.Bold);
                    _percent.Location = new Point(textX, _status.Bottom + (int)(2 * dpi));
                    _detail.Location = new Point(textX, _percent.Bottom + (int)(2 * dpi));
                }

                int buttonY = (int)((Height - (int)(32 * dpi)) / 2);
                refresh.Location = new Point(Width - (int)((84 + 16) * dpi), buttonY);
                remove.Location = new Point(Width - (int)((84 + 16 + 64 + 8) * dpi), buttonY);

                _badge.Location = new Point(Width - _badge.Width - (int)(16 * dpi), (int)(16 * dpi));
            }

            _relayout = Layout;
            Resize += (_, _) => _relayout();
            _relayout();
            RefreshNow();
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
                Outline = value ? _accent : _borderRestore;
                _badge.Visible = value;
                _relayout?.Invoke();
                RefreshNow();
                Invalidate();
            }
        }

        public void RefreshNow()
        {
            var now = DateTime.UtcNow;
            int percent = _device.RemainingPercent(now);

            _name.Text = _device.Name;
            _status.Text = "估算中";
            _percent.Text = $"{percent}%";
            _percent.ForeColor = percent < 20 ? Color.FromArgb(240, 190, 20) : _accent;
            _detail.Text = _device.Describe(now);

            _percentShape = IconFactory.Shape == IconShape.Percent;

            var previous = _icon.Image;
            try
            {
                using var icon = IconFactory.Create(Math.Max(32, _icon.Width), new BatteryReading { Percent = percent }, _light);
                _icon.Image = icon.ToBitmap();
            }
            catch
            {
                // 保留原图
            }
            previous?.Dispose();

            PerformLayout();
        }
    }

    private void ShowPage(int index)
    {
        for (int i = 0; i < _pages.Length; i++)
            _pages[i].Visible = i == index;

        for (int i = 0; i < _navs.Length; i++)
            _navs[i].Selected = i == index;
    }

    private void CycleAccent()
    {
        if (_loading) return;

        int next = (_app.Settings.AccentIndex + 1) % Theme.Accents.Length;
        _app.Settings.AccentIndex = next;
        _accentButton.Text = Theme.AccentName(next);

        ApplyAccent();                    // 立即重绘，不重开窗口
        _app.ApplySettingsFromWindow();   // 保存并同步右键菜单配色
    }

    /// <summary>把主题色应用到界面上所有用色的控件。</summary>
    private void ApplyAccent()
    {
        _accent = Theme.AccentFor(_app.Settings.AccentIndex);

        foreach (var nav in _navs) nav.AccentColor = _accent;
        _autostartToggle.OnColor = _accent;
        _notifyToggle.OnColor = _accent;

        foreach (var button in _accentButtons) button.BackColor = _accent;
        foreach (var card in _deviceCards) card.AccentColor = _accent;

        _sidebar.Invalidate();
        _devicePage.Invalidate(true);
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
        _offlineButton.Text = OfflineTexts[IndexOr(OfflineValues, settings.OfflineAsOffSeconds, 2)];
        _themeButton.Text = ThemeTexts[IndexOr(ThemeValues, settings.ThemeMode, 0)];
        _opacityButton.Text = OpacityTexts[IndexOr(OpacityValues, settings.MenuOpacity, 0)];
        _cornerButton.Text = CornerTexts[IndexOr(CornerValues, settings.MenuCorner, 1)];
        _accentButton.Text = Theme.AccentName(settings.AccentIndex);

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

    /// <summary>重开窗口时不要退出程序。</summary>
    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool SuppressExitOnClose { get; set; }

    public void SelectPage(int index) => ShowPage(index);

    public void UpdateView()
    {
        bool receiverConnected = _app.Service.ReceiverConnected;
        _devicePage.Content.Visible = receiverConnected;
        _emptyState.Visible = !receiverConnected;

        // 样式变了就整组重建，避免两类卡片布局不同步
        if (IconFactory.Shape != _lastShape)
        {
            _lastShape = IconFactory.Shape;
            _cardsSignature = "";
            _manualSignature = "";
        }

        var devices = _app.Service.Devices;

        // 接收器插着但一台设备都没有（鼠标关着）：显示一张“停用”占位卡
        var shown = devices.ToList();
        if (shown.Count == 0 && receiverConnected)
        {
            shown.Add(new LogiDevice
            {
                Name = string.IsNullOrWhiteSpace(_app.Settings.LastDeviceName) ? "罗技设备" : _app.Settings.LastDeviceName,
                Path = "",
                DeviceIndex = 0,
                Online = false,
                PoweredOff = true,
            });
        }

        // 把托盘正在显示的那台排到最前面
        string selectedKey = _app.Settings.SelectedDeviceKey ?? "";
        if (selectedKey.Length > 0 && selectedKey != "auto")
        {
            var first = shown.FirstOrDefault(d => MatchesKey(d, selectedKey));
            if (first != null)
            {
                shown.Remove(first);
                shown.Insert(0, first);
            }
        }

        string signature = string.Join("|", shown.Select(DeviceKeyOf));
        if (signature != _cardsSignature)
        {
            _cardsSignature = signature;
            RebuildDeviceCards(shown);
        }

        bool staleCard = false;
        foreach (var card in _deviceCards)
        {
            var device = shown.FirstOrDefault(d => DeviceKeyOf(d) == card.Key);
            if (device != null) card.SetDevice(device);
            else staleCard = true;

            card.Selected = device != null
                ? MatchesKey(device, SelectedKey())
                : card.Key == SelectedKey();
        }

        // 卡片对应的设备已经不存在 → 下一轮重建，避免显示过期内容
        if (staleCard) _cardsSignature = "";

        RebuildManualCards();

        ArrangeLists();

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

        // 现在是“窗口为主”，关掉窗口就退出程序（换配色重开时除外）
        if (!SuppressExitOnClose) _app.ExitAppPublic();
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
        private readonly bool _light;
        private Color _accentColor;
        private BatteryReading _battery;
        private LogiDevice _lastDevice;
        private bool _online;
        private bool _selected;
        private bool _percentShape;
        private Action _relayout;
        private bool _percentMode;

        public DeviceCard(Color surface, Color outline, Color background, Color fore, Color secondary, bool light, Color accent)
        {
            Surface = surface;
            Outline = outline;
            Background = background;
            _fore = fore;
            _secondary = secondary;
            _light = light;
            _accentColor = accent;

            _ring = new PictureBox
            {
                Location = new Point((int)(16 * Dpi), (int)(16 * Dpi)),
                Size = new Size((int)(64 * Dpi), (int)(64 * Dpi)),
                SizeMode = PictureBoxSizeMode.Zoom,
                BackColor = surface,
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
        public Color AccentColor
        {
            get => _accentColor;
            set
            {
                _accentColor = value;
                if (_badge != null) _badge.BackColor = value;
                if (_selected) Outline = value;
                Invalidate();
            }
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
                Outline = value ? _accentColor : BorderRestore;
                Invalidate();
                _badge.Visible = value;
                _badge.Location = new Point(Width - _badge.Width - (int)(16 * Dpi), (int)(16 * Dpi));

                ApplyIconMetrics();
                if (_lastDevice != null) SetDevice(_lastDevice);
            }
        }

        private Color BorderRestore => _light ? Color.FromArgb(227, 227, 233) : Color.FromArgb(56, 56, 62);

        public void SetDevice(LogiDevice device)
        {
            if (Key.Length == 0) Key = device.Path + "#" + device.Name;

            _lastDevice = device;
            _battery = device.Battery;
            _online = device.Online && _battery != null;

            _name.Text = device.Name;
            _name.MaximumSize = new Size(Math.Max((int)(80 * Dpi), Width - (int)(126 * Dpi) - (int)(16 * Dpi)), 0);

            string offlineLabel = device.PoweredOff ? "停用" : "休眠";
            _status.Text = _online
                ? _battery!.StatusText
                : _battery?.Percent != null ? $"{offlineLabel} · 上次 {_battery.Percent}%"
                : device.PoweredOff ? "停用"
                : "未连接";

            _percent.Text = _battery?.Percent is int percent ? $"{percent}%" : "--";
            _percent.ForeColor = _online ? IconFactory.LevelColor(_battery!) : _secondary;

            bool percentShape = IconFactory.Shape == IconShape.Percent;
            _percentMode = percentShape;
            ApplyIconMetrics();

            // 百分比样式：隐藏图标，数字和文字排在同一列
            _ring.Visible = !percentShape;
            _bigPercent.Visible = false;
            _percent.Visible = true;
            _percent.Font = new Font("Segoe UI", percentShape ? (_selected ? 30f : 26f) : 22f, FontStyle.Bold);

            _updated.Text = device.UpdatedAt is DateTime updatedAt ? $"更新于 {updatedAt:HH:mm:ss}" : "";

            int size = Math.Max(32, _ring.Width);
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

        private void LayoutLabels()
        {
            if (_name == null) return;

            _percentMode = IconFactory.Shape == IconShape.Percent;
            int x = (int)((_percentMode ? 22 : 126) * Dpi);
            float y = 18 * Dpi;

            _name.Location = new Point(x, (int)y);
            y = _name.Bottom + 2 * Dpi;

            _status.Location = new Point(x, (int)y);
            y = _status.Bottom + 2 * Dpi;

            _percent.Location = new Point(x, (int)y);
            y = _percent.Bottom + 2 * Dpi;

            _updated.Location = new Point(x, (int)y);
            Invalidate(true);
        }

        /// <summary>图标（或百分比大数字）的尺寸与位置：选中大、未选中小且靠左上。</summary>
        private void ApplyIconMetrics()
        {
            float dpi = Dpi;
            int size = (int)((_selected ? 88 : 64) * dpi);
            int inset = (int)((_selected ? 22 : 16) * dpi);

            _ring.Location = new Point(inset, inset);
            _ring.Size = new Size(size, size);

            // 大数字可以比图标更宽，但不会压到右侧文字列
            int textX = (int)(126 * dpi);
            int available = Math.Max(size, textX - inset - (int)(8 * dpi));

            _bigPercent.Font = new Font("Segoe UI", _selected ? 30f : 26f, FontStyle.Bold);
            _bigPercent.Location = new Point(inset, (int)(20 * dpi));
            _bigPercent.Size = new Size(available, (int)(80 * dpi));
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
        private Color _accent;
        private bool _hover;
        private bool _selected;
        private bool _percentShape;
        private Action _relayout;

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
        public Color AccentColor
        {
            get => _accent;
            set { _accent = value; Invalidate(); }
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
            {
                var iconColor = _selected ? Color.White : (_hover ? _fore : _muted);
                TextRenderer.DrawText(g, _glyph, iconFont, new Rectangle(12, 0, 24, Height), iconColor,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }

            using (var textFont = new Font("Segoe UI", 9.5f, _selected ? FontStyle.Bold : FontStyle.Regular))
            {
                var textColor = _selected ? Color.White : (_hover ? _fore : _muted);
                TextRenderer.DrawText(g, Text, textFont, new Rectangle(46, 0, Width - 50, Height), textColor,
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
