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
    private RoundedButton _addManualButton;
    private readonly List<ManualCard> _manualCards = new();
    private string _manualSignature = "";
    private Panel _actionsPanel;
    private Panel _emptyState;
    private ScrollHost _devicePage;
    private string _cardsSignature = "";
    private int _listTopY;
    private IconShape _lastShape = IconFactory.Shape;
    private int _lastAccent;
    private const string CardDragFormat = "LogiBatteryTray.DeviceKey";
    private bool _suppressClick;
    private string _dragKey;
    private Point _dragPressScreen;
    private bool _dragStarted;
    private DragGhostForm _dragGhost;
    private int _dragOffsetX = 20;
    private int _dragOffsetY = 20;

    private ToggleSwitch _autostartToggle;
    private ToggleSwitch _notifyToggle;
    private RoundedDropDown _shapeCombo;
    private RoundedDropDown _intervalCombo;
    private RoundedDropDown _offlineCombo;
    private RoundedDropDown _deviceCombo;
    private RoundedDropDown _themeCombo;
    private RoundedDropDown _opacityCombo;
    private RoundedDropDown _cornerCombo;
    private RoundedDropDown _accentCombo;
    private readonly List<(string Key, string Label)> _deviceOptions = new();
    private int _deviceIndex;

    private readonly Color _background;
    private readonly Color _sidebarColor;
    private readonly Color _cardColor;
    private readonly Color _border;
    private readonly Color _fore;
    private readonly Color _secondary;
    private Color _accent;
    private readonly List<RoundedButton> _accentButtons = new();

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
        int startWidth = app.Settings.WindowWidth > 200
            ? Math.Min(app.Settings.WindowWidth, area.Width)
            : Math.Min((int)S(860), area.Width - (int)S(40));
        int startHeight = app.Settings.WindowHeight > 200
            ? Math.Min(app.Settings.WindowHeight, area.Height)
            : Math.Min((int)S(620), area.Height - (int)S(60));
        ClientSize = new Size(startWidth, startHeight);
        MinimumSize = new Size((int)S(720), (int)S(460));

        if (app.Settings.WindowMaximized)
            WindowState = FormWindowState.Maximized;

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
        AllowDrop = true;
        DragEnter += (_, e) =>
        {
            if (e.Data != null && e.Data.GetDataPresent(CardDragFormat)) e.Effect = DragDropEffects.Move;
        };
        DragDrop += (_, e) =>
        {
            if (e.Data == null || !e.Data.GetDataPresent(CardDragFormat)) return;

            string key = e.Data.GetData(CardDragFormat) as string;
            if (string.IsNullOrEmpty(key)) return;

            _app.Settings.SelectedDeviceKey = key;
            _app.ApplySettingsFromWindow();
            UpdateView();
        };

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

        _addManualButton = new RoundedButton
        {
            Text = "＋ 添加估算设备",
            Size = new Size((int)S(148), (int)S(36)),
            Surface = _cardColor,
            HoverSurface = ButtonHover,
            TextColor = _accent,
            Visible = false,
        };
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
                Radius = S(18),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            };

            card.SetDevice(device);
            card.Selected = MatchesKey(device, SelectedKey());

            AttachCardDrag(card, DeviceKeyOf(device));

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

        if (key.StartsWith("manual:", StringComparison.OrdinalIgnoreCase))
        {
            string name = key["manual:".Length..];
            if (_app.Settings.ManualDevices?.Any(d => d.Name == name) == true) return key;
            // 这台估算设备已被删除 → 忽略这个记录
        }
        else if (key.Length > 0 && key != "auto")
        {
            if (_app.Service.Devices.Any(d => MatchesKey(d, key))) return key;
        }

        return _app.DisplayDevice is LogiDevice shown
            ? DeviceKeyOf(shown)
            : key;
    }

    /// <summary>按下卡片即登记为候选拖拽，后续移动/松手由消息过滤器接管。</summary>
    private void AttachCardDrag(Control card, string key)
    {
        void Down(object s, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;

            _dragKey = key;
            _dragPressScreen = Cursor.Position;
            _dragStarted = false;
        }

        card.MouseDown += Down;
        foreach (Control child in card.Controls)
        {
            if (child is Button) continue;
            child.MouseDown += Down;
        }
    }

    /// <summary>鼠标移动：超过阈值就开始拖动并让幽灵跟随。</summary>
    private void UpdateDrag()
    {
        if (_dragKey == null) return;

        if (!_dragStarted)
        {
            var threshold = SystemInformation.DragSize;
            if (Math.Abs(Cursor.Position.X - _dragPressScreen.X) < threshold.Width / 2 &&
                Math.Abs(Cursor.Position.Y - _dragPressScreen.Y) < threshold.Height / 2) return;

            _dragStarted = true;
            _suppressClick = true;

            var card = DeviceCardForDrag();
            if (card != null)
            {
                var origin = card.PointToScreen(Point.Empty);
                _dragOffsetX = Math.Min(Math.Max(Cursor.Position.X - origin.X, 0), Math.Max(1, card.Width / 2));
                _dragOffsetY = Math.Min(Math.Max(Cursor.Position.Y - origin.Y, 0), Math.Max(1, card.Height / 2));

                _dragGhost = new DragGhostForm(DragGhostForm.Snapshot(card)) { Opacity = 0.85 };
                _dragGhost.Show();
            }
        }

        _dragGhost?.FollowCursor(_dragOffsetX, _dragOffsetY);
    }

    /// <summary>松手：在窗口内就把该设备设为托盘显示。</summary>
    private void FinishDrag()
    {
        if (_dragKey == null) return;

        string key = _dragKey;
        bool started = _dragStarted;
        _dragKey = null;
        _dragStarted = false;

        var ghost = _dragGhost;
        _dragGhost = null;
        ghost?.Close();

        if (!started) return;

        if (ClientRectangle.Contains(PointToClient(Cursor.Position)))
        {
            var order = _app.Settings.CardOrder ??= new List<string>();
            foreach (var card in _deviceCards) if (!order.Contains(card.Key)) order.Add(card.Key);
            foreach (var card in _manualCards)
            {
                string k = "manual:" + card.DeviceName;
                if (!order.Contains(k)) order.Add(k);
            }

            order.Remove(key);

            // 落到哪张卡上就插到那张卡的位置；落在空白处放到最后
            string targetKey = CardKeyAt(Cursor.Position);
            int index = targetKey != null && targetKey != key
                ? Math.Max(0, order.IndexOf(targetKey))
                : order.Count;

            order.Insert(Math.Min(index, order.Count), key);

            _app.Settings.SelectedDeviceKey = order[0];
            _app.Settings.Save();
            _app.ApplySettingsFromWindow();
            UpdateView();
        }

        _suppressClick = true;
    }

    /// <summary>找出鼠标落点上的卡片 key。</summary>
    private string CardKeyAt(Point screenPoint)
    {
        foreach (var card in _deviceCards)
        {
            if (card.RectangleToScreen(card.ClientRectangle).Contains(screenPoint))
                return card.Key;
        }

        foreach (var card in _manualCards)
        {
            if (card.RectangleToScreen(card.ClientRectangle).Contains(screenPoint))
                return "manual:" + card.DeviceName;
        }

        return null;
    }

    private Control DeviceCardForDrag()
    {
        string key = _dragKey ?? "";
        if (key.StartsWith("manual:", StringComparison.OrdinalIgnoreCase))
            return _manualCards.FirstOrDefault(c => ("manual:" + c.DeviceName) == key);
        return _deviceCards.FirstOrDefault(c => c.Key == key);
    }
    /// <summary>点设备卡片循环切换图标样式，所有设备一起变。</summary>
    private void CycleCardShape()
    {
        if (_suppressClick)
        {
            _suppressClick = false;
            return;
        }

        int next = IconFactory.Shape switch
        {
            IconShape.Ring => 1,
            IconShape.Battery => 2,
            _ => 0,
        };

        _app.Settings.IconShapeMode = next;
        IconFactory.Shape = (IconShape)next;

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
        AddComboRow(tray, S(46), "显示设备", out _deviceCombo, Array.Empty<string>(), -1, index =>
        {
            if (index < 0 || index >= _deviceOptions.Count) return;
            _app.Settings.SelectedDeviceKey = _deviceOptions[index].Key;
            _app.ApplySettingsFromWindow();
            UpdateView();
        });
        y += tray.Height + S(16);

        var appearance = AddCard(page.Content, "外观", 3, y);
        float row = S(46);
        row = AddComboRow(appearance, row, "主题", out _themeCombo, ThemeTexts, 0, index =>
        {
            _app.Settings.ThemeMode = ThemeValues[index];
            _app.ApplySettingsFromWindow();
            _app.ReopenWindow();
        });
        row = AddComboRow(appearance, row, "图标形状", out _shapeCombo, ShapeTexts, 0, index =>
        {
            int shape = ShapeValues[index];
            _app.Settings.IconShapeMode = shape;
            IconFactory.Shape = (IconShape)shape;
            _app.ApplySettingsFromWindow();
            UpdateView();
        });
        AddComboRow(appearance, row, "主题色", out _accentCombo, Theme.Accents.Select(a => a.Name).ToArray(), 0, index =>
        {
            _app.Settings.AccentIndex = index;
            ApplyAccent();
            _app.ApplySettingsFromWindow();
        });

        y += appearance.Height + S(16);

        var menuCard = AddCard(page.Content, "右键菜单", 2, y);
        float menuRow = S(46);
        menuRow = AddComboRow(menuCard, menuRow, "不透明度", out _opacityCombo, OpacityTexts, 0, index =>
        {
            _app.Settings.MenuOpacity = OpacityValues[index];
            _app.ApplySettingsFromWindow();
        });
        AddComboRow(menuCard, menuRow, "圆角", out _cornerCombo, CornerTexts, 1, index =>
        {
            _app.Settings.MenuCorner = CornerValues[index];
            _app.ApplySettingsFromWindow();
        });

        y += menuCard.Height + S(16);

        var behavior = AddCard(page.Content, "行为", 4, y);
        row = S(46);
        row = AddComboRow(behavior, row, "刷新间隔", out _intervalCombo, IntervalTexts, 1, index =>
        {
            _app.Settings.IntervalSeconds = IntervalValues[index];
            _app.ApplySettingsFromWindow();
        });
        row = AddComboRow(behavior, row, "关机判定", out _offlineCombo, OfflineTexts, 2, index =>
        {
            _app.Settings.OfflineAsOffSeconds = OfflineValues[index];
            _app.ApplySettingsFromWindow();
        });
        row = AddToggleRow(behavior, row, "开机自动启动", out _autostartToggle);
        AddToggleRow(behavior, row, "电量低于 20% 时通知", out _notifyToggle);

        _autostartToggle.CheckedChanged += (_, _) => OnSettingChanged();
        _notifyToggle.CheckedChanged += (_, _) => OnSettingChanged();

        return page;
    }
    // ---------- 关于页 ----------
    private ScrollHost BuildAboutPage()
    {
        var page = NewPage();
        float y = PlaceTitle(page.Content, "关于", "版本信息与常用入口");
        y += S(6);

        var card = NewCard(Math.Max((int)S(420), page.Content.Width - (int)S(56)), (int)S(208));
        card.Location = P(28, y);
        page.Content.Controls.Add(card);

        // 应用名
        var title = AddLabel(card, 28, 16, "LogiBatteryTray", 16f, FontStyle.Bold, _fore);

        // 版本 + 构建时间（同一行）
        string buildText = "";
        try { buildText = System.IO.File.GetLastWriteTime(Application.ExecutablePath).ToString("yyyy-MM-dd HH:mm"); } catch { }

        var meta = AddLabel(card, 30, title.Bottom + S(1),
            $"版本 {typeof(MainForm).Assembly.GetName().Version?.ToString(3)}　·　构建 {buildText}",
            9f, FontStyle.Regular, _secondary);

        // 说明
        var desc = AddLabel(card, 30, meta.Bottom + S(4),
            "在任务栏显示罗技鼠标电量，直接读取 HID++ 2.0，不需要 G HUB。",
            9f, FontStyle.Regular, _fore);

        var data = AddLabel(card, 30, desc.Bottom + S(2),
            @"数据目录  %APPDATA%\LogiBatteryTray",
            8.5f, FontStyle.Regular, _secondary);

        // 卡片高度按实际内容撑开
        card.Height = (int)(data.Bottom + S(12));

        // 按钮行
        float buttonsY = card.Bottom + S(8);

        var log = MakeButton(28, buttonsY, "打开日志", S(116));
        log.Parent = page.Content;
        log.Click += (_, _) => _app.OpenLogFolderPublic();

        var repo = MakeButton(156, buttonsY, "项目主页", S(116));
        repo.Parent = page.Content;
        repo.Click += (_, _) =>
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "https://github.com/Kamio-m/logi-batter-display",
                    UseShellExecute = true,
                });
            }
            catch { }
        };

        var quit = MakeButton(284, buttonsY, "退出程序", S(116));
        quit.Parent = page.Content;
        quit.Click += (_, _) => _app.ExitAppPublic();

        return page;
    }
    private CardPanel NewCard(int width, int height) => new()
    {
        Size = new Size(width, height),
        Radius = S(18),
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

    /// <summary>一行“标签 + 下拉列表”。</summary>
    private float AddComboRow(Panel card, float y, string label, out RoundedDropDown combo, string[] items,
        int selectedIndex, Action<int> changed)
    {
        AddRowLabel(card, y, label);

        var box = new RoundedDropDown
        {
            Font = new Font("Segoe UI", 9f),
            Surface = ButtonBack,
            HoverSurface = ButtonHover,
            TextColor = _fore,
            AccentColor = _accent,
            PopupBackground = _cardColor,
            Size = new Size((int)S(168), (int)S(32)),
            Location = new Point(card.Width - (int)S(168) - (int)S(20), (int)(y + (S(52) - S(32)) / 2)),
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
        };

        box.SetItems(items);
        if (selectedIndex >= 0 && selectedIndex < box.Items.Count) box.SelectedIndex = selectedIndex;

        box.SelectedIndexChanged += (_, _) =>
        {
            if (_loading) return;
            changed(box.SelectedIndex);
        };

        combo = box;
        card.Controls.Add(box);
        return y + S(52);
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

    private RoundedButton MakeButton(float x, float y, string text, float width, bool primary = false)
    {
        var button = new RoundedButton
        {
            Text = text,
            Location = P(x, y),
            Size = new Size((int)width, (int)S(34)),
            Surface = primary ? _accent : ButtonBack,
            HoverSurface = primary ? ControlPaint.Light(_accent, 0.08f) : ButtonHover,
            TextColor = primary ? Color.White : _fore,
        };

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
                    Radius = S(18),
                    Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                    Selected = selectedManual == "manual:" + device.Name,
                };

                card.StyleCycleRequested += (_, _) => CycleCardShape();
                AttachCardDrag(card, "manual:" + device.Name);

                card.RefreshRequested += (_, _) =>
                {
                    device.LastChargedUtc = DateTime.UtcNow;
                    _app.ApplySettingsFromWindow();
                    _manualSignature = "";
                    UpdateView();
                };

                card.DeleteRequested += (_, _) =>
                {
                    if ((_app.Settings.SelectedDeviceKey ?? "") == "manual:" + device.Name)
                        _app.Settings.SelectedDeviceKey = "";

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
        public event EventHandler StyleCycleRequested;

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
                Font = new Font("Segoe UI", 9.5f),
                ForeColor = secondary,
                BackColor = Color.Transparent,
            };

            _percent = new Label
            {
                AutoSize = true,
                Font = new Font("Segoe UI", 21f, FontStyle.Bold),
                ForeColor = accent,
                BackColor = Color.Transparent,
            };

            _detail = new Label
            {
                AutoSize = false,
                AutoEllipsis = true,
                Font = new Font("Segoe UI", 8.5f),
                ForeColor = secondary,
                BackColor = Color.Transparent,
            };

            var refresh = new RoundedButton
            {
                Text = "刚充满",
                Size = new Size(84, 32),
                Surface = accent,
                HoverSurface = ControlPaint.Light(accent, 0.08f),
                TextColor = Color.White,
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
            };
            refresh.Click += (_, _) => RefreshRequested?.Invoke(this, EventArgs.Empty);

            var remove = new RoundedButton
            {
                Text = "删除",
                Size = new Size(64, 32),
                Surface = light ? Color.FromArgb(243, 243, 247) : Color.FromArgb(48, 48, 54),
                TextColor = fore,
                Anchor = AnchorStyles.Top | AnchorStyles.Right,
            };
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

            // 点卡片任意位置都可以切换图标样式（按钮除外）
            void Cycle(object _, EventArgs __) => StyleCycleRequested?.Invoke(this, EventArgs.Empty);
            Click += Cycle;
            Cursor = Cursors.Hand;
            foreach (var label in new Control[] { _icon, _name, _status, _percent, _detail, _badge })
            {
                label.Click += Cycle;
                label.Cursor = Cursors.Hand;
            }

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
                    _percent.Font = new Font("Segoe UI", _selected ? 27f : 23f, FontStyle.Bold);
                    _percent.Location = new Point((int)(22 * dpi), _status.Bottom + (int)(2 * dpi));
                    _detail.Location = new Point((int)(22 * dpi), _percent.Bottom + (int)(2 * dpi));
                }
                else
                {
                    _icon.Visible = true;
                    _percent.Font = new Font("Segoe UI", 21f, FontStyle.Bold);
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

        public void ForceRefresh()
        {
            _relayout?.Invoke();
            RefreshNow();
            Invalidate();
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

    /// <summary>把设置行同步成当前设置值。</summary>
    private void SyncSettingLabels()
    {
        if (_loading) return;

        var settings = _app.Settings;

        SetComboIndex(_shapeCombo, IndexOr(ShapeValues, settings.IconShapeMode, 0));
        SetComboIndex(_intervalCombo, IndexOr(IntervalValues, settings.IntervalSeconds, 1));
        SetComboIndex(_offlineCombo, IndexOr(OfflineValues, settings.OfflineAsOffSeconds, 2));
        SetComboIndex(_themeCombo, IndexOr(ThemeValues, settings.ThemeMode, 0));
        SetComboIndex(_opacityCombo, IndexOr(OpacityValues, settings.MenuOpacity, 0));
        SetComboIndex(_cornerCombo, IndexOr(CornerValues, settings.MenuCorner, 1));
        SetComboIndex(_accentCombo, settings.AccentIndex);
    }

    /// <summary>安全地设置下拉选中项（不触发回调）。</summary>
    private void SetComboIndex(RoundedDropDown combo, int index)
    {
        if (combo == null || index < 0 || index >= combo.Items.Count) return;
        if (combo.SelectedIndex == index) return;

        bool wasLoading = _loading;
        _loading = true;
        try { combo.SelectedIndex = index; }
        finally { _loading = wasLoading; }
    }
    /// <summary>按设置里的顺序摆放所有卡片（第一张即托盘显示设备）。</summary>
    private void LayoutOrderedCards()
    {
        if (_deviceList == null || _manualList == null) return;

        var all = _deviceCards.Cast<Control>().Concat(_manualCards.Cast<Control>()).ToList();
        if (all.Count == 0)
        {
            if (_addManualButton != null) _addManualButton.Top = _listTopY;
            if (_actionsPanel != null) _actionsPanel.Top = (_addManualButton == null ? _listTopY : _addManualButton.Bottom) + CardGap;
            return;
        }

        string KeyOf(Control c) => c switch
        {
            DeviceCard d => d.Key,
            ManualCard m => "manual:" + m.DeviceName,
            _ => "",
        };

        var order = _app.Settings.CardOrder ??= new List<string>();
        order.RemoveAll(k => !all.Any(c => KeyOf(c) == k));
        foreach (var card in all)
        {
            string k = KeyOf(card);
            if (k.Length > 0 && !order.Contains(k)) order.Add(k);
        }

        int width = Math.Max((int)S(420), _deviceList.Width - (int)S(56));
        float top = 0;

        foreach (var key in order)
        {
            var card = all.FirstOrDefault(c => KeyOf(c) == key);
            if (card == null) continue;

            if (card.Parent != _deviceList)
            {
                card.Parent?.Controls.Remove(card);
                _deviceList.Controls.Add(card);
            }

            card.Location = new Point((int)S(28), (int)top);
            card.Size = new Size(width, (int)S(152));
            card.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
            top += S(152) + CardGap;
        }

        _deviceList.Height = (int)Math.Max(top, S(10));
        _manualList.Height = 0;

        if (_addManualButton != null)
            _addManualButton.Top = _deviceList.Bottom + CardGap;

        if (_actionsPanel != null)
            _actionsPanel.Top = (_addManualButton == null ? _deviceList.Bottom : _addManualButton.Bottom) + CardGap;

        // 托盘设备始终排到第一位
        string wanted = _app.Settings.SelectedDeviceKey ?? "";
        if (wanted.Length > 0 && wanted != "auto" && order.Count > 0 && order[0] != wanted)
        {
            if (order.Remove(wanted)) order.Insert(0, wanted);
        }

        _devicePage?.PerformLayout();
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

        foreach (var button in _accentButtons) { button.Surface = _accent; button.Invalidate(); }
        foreach (var card in _deviceCards) card.AccentColor = _accent;
        foreach (var card in _manualCards) card.ForceRefresh();

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
        if (_deviceCombo == null) return;

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

        bool wasLoading = _loading;
        _loading = true;
        try
        {
            _deviceCombo.SetItems(_deviceOptions.Select(o => o.Label));
            _deviceCombo.SelectedIndex = index;
        }
        finally
        {
            _loading = wasLoading;
        }

        _deviceIndex = index;
    }
    private void LoadFromSettings()
    {
        _loading = true;

        var settings = _app.Settings;
        _autostartToggle.Checked = AppSettings.IsAutostartEnabled();
        _notifyToggle.Checked = settings.LowBatteryNotify;

        SetComboIndex(_intervalCombo, IndexOr(IntervalValues, settings.IntervalSeconds, 1));
        SetComboIndex(_shapeCombo, IndexOr(ShapeValues, settings.IconShapeMode, 0));
        SetComboIndex(_offlineCombo, IndexOr(OfflineValues, settings.OfflineAsOffSeconds, 2));
        SetComboIndex(_themeCombo, IndexOr(ThemeValues, settings.ThemeMode, 0));
        SetComboIndex(_opacityCombo, IndexOr(OpacityValues, settings.MenuOpacity, 0));
        SetComboIndex(_cornerCombo, IndexOr(CornerValues, settings.MenuCorner, 1));
        SetComboIndex(_accentCombo, settings.AccentIndex);





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

    /// <summary>真正退出程序时才允许关闭窗口；平时点 × 只是隐藏到托盘。</summary>
    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool AllowClose { get; set; }

    public void SelectPage(int index) => ShowPage(index);

    public void UpdateView()
    {
        bool receiverConnected = _app.Service.ReceiverConnected;
        _devicePage.Content.Visible = receiverConnected;
        _emptyState.Visible = !receiverConnected;

        // 样式或主题色变了就整组重建，避免两类卡片不同步
        if (IconFactory.Shape != _lastShape || _lastAccent != _app.Settings.AccentIndex)
        {
            _lastShape = IconFactory.Shape;
            _lastAccent = _app.Settings.AccentIndex;
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

        SyncSettingLabels();
        RebuildManualCards();

        LayoutOrderedCards();

        if (_deviceCombo != null) RebuildDeviceOptions();
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
        const int WmMouseMove = 0x0200;
        const int WmLeftButtonUp = 0x0202;

        // 拖拽期间接管鼠标移动与抬起
        if (_dragKey != null)
        {
            if (m.Msg == WmMouseMove)
            {
                UpdateDrag();
                return false;
            }

            if (m.Msg == WmLeftButtonUp)
            {
                FinishDrag();
                return false;
            }
        }

        if (m.Msg != WmMouseWheel || !Visible) return false;
        if (!ClientRectangle.Contains(PointToClient(Cursor.Position))) return false;

        var page = _pages.FirstOrDefault(p => p.Visible);
        if (page == null) return false;

        int delta = (short)((m.WParam.ToInt64() >> 16) & 0xFFFF);
        if (delta == 0) return false;

        page.ScrollBy(delta > 0 ? -(int)S(90) : (int)S(90));
        return true;
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        base.OnFormClosing(e);

        if (AllowClose || SuppressExitOnClose) return;
        if (e.CloseReason != CloseReason.UserClosing) return;

        // 点 × 只隐藏到托盘，程序继续运行
        e.Cancel = true;
        Hide();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        Application.RemoveMessageFilter(this);

        // 记住窗口尺寸，下次打开保持一样
        try
        {
            var settings = _app.Settings;
            settings.WindowMaximized = WindowState == FormWindowState.Maximized;

            var bounds = WindowState == FormWindowState.Normal ? Bounds : RestoreBounds;
            if (bounds.Width > 200 && bounds.Height > 200)
            {
                settings.WindowWidth = bounds.Width;
                settings.WindowHeight = bounds.Height;
            }
            settings.Save();
        }
        catch { }


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

            // 数字跟随主题色；只有低电量（<20% 且未充电）保留黄色警示
            bool lowWarning = _battery?.Percent is int lowValue && lowValue < IconFactory.LowBatteryThreshold && _battery.Charging != true;
            _percent.ForeColor = !_online
                ? _secondary
                : lowWarning ? IconFactory.LevelColor(_battery!) : _accentColor;

            bool percentShape = IconFactory.Shape == IconShape.Percent;
            _percentMode = percentShape;
            ApplyIconMetrics();

            // 百分比样式：隐藏图标，数字和文字排在同一列
            _ring.Visible = !percentShape;
            _bigPercent.Visible = false;
            _percent.Visible = true;
            _percent.Font = new Font("Segoe UI", percentShape ? (_selected ? 27f : 23f) : 21f, FontStyle.Bold);

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
