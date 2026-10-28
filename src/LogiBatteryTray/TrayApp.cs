using Microsoft.Win32;

namespace LogiBatteryTray;

/// <summary>主题判断（浅色/深色任务栏）。</summary>
internal static class Theme
{
    /// <summary>主题色（强调色）备选。</summary>
    public static readonly (string Name, Color Color)[] Accents =
    {
        ("蓝色", Color.FromArgb(58, 130, 255)),
        ("绿色", Color.FromArgb(47, 180, 99)),
        ("紫色", Color.FromArgb(139, 92, 246)),
        ("橙色", Color.FromArgb(245, 158, 11)),
        ("粉色", Color.FromArgb(236, 72, 153)),
        ("青色", Color.FromArgb(6, 182, 212)),
    };

    public static Color AccentFor(int index) => Accents[Math.Clamp(index, 0, Accents.Length - 1)].Color;

    public static string AccentName(int index) => Accents[Math.Clamp(index, 0, Accents.Length - 1)].Name;

    private static bool _cached;
    private static bool _cachedValue;
    private static DateTime _cachedAt = DateTime.MinValue;

    public static bool IsLightTaskbar()
    {
        if (_cached && DateTime.Now - _cachedAt < TimeSpan.FromSeconds(10)) return _cachedValue;

        bool light = false;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            if (key?.GetValue("SystemUsesLightTheme") is int value) light = value != 0;
        }
        catch
        {
            // 读不到就按深色处理
        }

        _cached = true;
        _cachedValue = light;
        _cachedAt = DateTime.Now;
        return light;
    }
}

/// <summary>托盘程序主体：图标、菜单、定时刷新。</summary>
internal sealed class TrayApp : ApplicationContext, IMainFormHost
{
    private readonly BatteryService _service = new();
    private readonly AppSettings _settings;
    private readonly NotifyIcon _tray;
    private readonly ContextMenuStrip _menu;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly Control _dispatcher;

    private readonly ToolStripMenuItem _header;
    private readonly ToolStripMenuItem _autostartItem;
    private readonly ToolStripMenuItem _notifyItem;
    private readonly ToolStripMenuItem _deviceMenu = new("显示设备");
    private readonly Dictionary<int, ToolStripMenuItem> _intervalItems = new();
    private readonly List<(ToolStripMenuItem Item, int Mode)> _themeItems = new();
    private readonly List<(ToolStripMenuItem Item, int Percent)> _opacityItems = new();
    private readonly List<(ToolStripMenuItem Item, int Radius)> _cornerItems = new();
    private readonly List<(ToolStripMenuItem Item, int Seconds)> _offlineItems = new();
    private readonly List<(ToolStripMenuItem Item, int Shape)> _shapeItems = new();
    private readonly HashSet<ToolStripDropDown> _styledDropdowns = new();
    private readonly MouseActivityWatcher _mouseWatcher;
    private readonly DeviceWatcher _deviceWatcher;
    private DateTime _lastDeviceEvent = DateTime.MinValue;
    private readonly System.Windows.Forms.Timer _burstTimer;
    private int _burstRemaining;
    private bool _pendingRefresh;

    private MainForm _mainForm;
    private BatteryWidget _widget;
    private bool _refreshing;
    private readonly HashSet<string> _lowNotified = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastActivityRefresh = DateTime.MinValue;
    private string _lastState = "";

    public TrayApp(bool showWindow = false)
    {
        _settings = AppSettings.Load();

        LampEngine.BatteryPercentProvider = () =>
            DisplayDevice?.Battery?.Percent ?? SelectedManualDevice?.RemainingPercent(DateTime.UtcNow);
        if (!string.IsNullOrEmpty(_settings.LoadError)) Log.Write("读取设置失败: " + _settings.LoadError);

        // 预热灯效控制权缓存（后台线程，避免首次打开灯光页卡顿）
        LampArrayControl.WarmUp();

        // 恢复上次灯效
        RestoreLampEffect();

        // 灯效变化后自动保存
        LampEngine.Changed += (_, _) => SaveLampEffect();
        _dispatcher = new Control();
        _dispatcher.CreateControl();

        _header = new ToolStripMenuItem("正在读取…") { Enabled = false };
        try { _header.Font = new Font(_header.Font, FontStyle.Bold); } catch { }

        _autostartItem = new ToolStripMenuItem("开机自动启动", null, (_, _) => ToggleAutostart());
        _notifyItem = new ToolStripMenuItem("低电量提醒", null, (_, _) => ToggleLowBatteryNotify());

        IconFactory.Style = BatteryIconStyle.OutlinePlug;
        IconFactory.Shape = (IconShape)_settings.IconShapeMode;

        _menu = new ContextMenuStrip
        {
            ShowImageMargin = false,
            ShowCheckMargin = false,
            MinimumSize = new Size(250, 0),   // 固定最小宽度，避免换设备后菜单忽宽忽窄
            Padding = new Padding(8, 4, 8, 6),
        };
        _menu.Opened += (_, _) =>
        {
            MenuStyle.ApplyMenuWindow(_menu, _settings.MenuOpacity, _settings.MenuCorner);
            RefreshAsync();
        };
        _menu.Items.Add(_header);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("刷新", null, (_, _) => RefreshAsync()) { ShortcutKeyDisplayString = "F5" });
        _menu.Items.Add(_deviceMenu);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_notifyItem);
        _menu.Items.Add(_autostartItem);
        _menu.Items.Add(BuildIntervalMenu());
        _menu.Items.Add(BuildPersonalizeMenu());
        _menu.Items.Add(BuildShapeMenu());
        _menu.Items.Add(BuildOfflineMenu());
        _menu.Opened += (_, _) => RebuildDeviceMenu();

        _menu.Items.Add(new ToolStripSeparator());
        ApplyMenuStyle();
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("打开日志文件夹", null, (_, _) => OpenLogFolder()));
        _menu.Items.Add(new ToolStripMenuItem("退出", null, (_, _) => ExitApp()));

        _settings.Autostart = AppSettings.IsAutostartEnabled();

        _tray = new NotifyIcon
        {
            Visible = true,
            ContextMenuStrip = _menu,
            Text = "罗技鼠标电量",
            Icon = IconFactory.Create(32, null, Theme.IsLightTaskbar()),
        };
        _tray.MouseClick += OnTrayMouseClick;
        _tray.MouseDoubleClick += (_, _) => ShowDetails();

        // 1.5 秒后向系统确认图标是否真的进了通知区域；失败重试一次，仍失败则切换悬浮窗
        var promoteTimer = new System.Windows.Forms.Timer { Interval = 2000 };
        promoteTimer.Tick += (_, _) =>
        {
            promoteTimer.Stop();
            promoteTimer.Dispose();
            PromoteTrayIcon();
        };
        promoteTimer.Start();

        var trayCheck = new System.Windows.Forms.Timer { Interval = 1500 };
        int trayCheckAttempts = 0;
        trayCheck.Tick += (_, _) =>
        {
            trayCheck.Stop();
            string state = RuntimeProbe.TrayState(_tray);
            Log.Write("托盘检查: " + state);

            if (state.StartsWith("已注册"))
            {
                trayCheck.Dispose();
                return;
            }

            trayCheckAttempts++;
            if (trayCheckAttempts < 2)
            {
                trayCheck.Interval = 2500;
                trayCheck.Start();
                return;
            }

            trayCheck.Dispose();
            Log.Write("托盘不可用，切换到悬浮窗模式");
            ShowWidget();
        };
        trayCheck.Start();

        _burstTimer = new System.Windows.Forms.Timer { Interval = 250 };
        _burstTimer.Tick += (_, _) =>
        {
            RefreshAsync();
            if (--_burstRemaining <= 0) _burstTimer.Stop();
        };

        _timer = new System.Windows.Forms.Timer { Interval = 600 };
        _timer.Tick += OnTimerTick;
        _timer.Start();

        _mouseWatcher = new MouseActivityWatcher();
        _mouseWatcher.Activity += OnMouseActivity;

        // USB/HID 插拔事件：拔掉接收器时立即刷新，不等下一个轮询
        _deviceWatcher = new DeviceWatcher();
        _deviceWatcher.Changed += OnDeviceChanged;

        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.SessionSwitch += (_, _) => RefreshAsync();

        ConfigureSubMenus();

        UpdateMenuState();
        RefreshAsync();

        if (showWindow) ShowDetails();
    }

    private ToolStripMenuItem BuildShapeMenu()
    {
        var root = new ToolStripMenuItem("图标形状");
        foreach (var option in new (int Shape, string Text)[] { (0, "圆环"), (1, "电池图标"), (2, "百分比") })
        {
            int shape = option.Shape;
            var item = new ToolStripMenuItem(option.Text, null, (_, _) =>
            {
                _settings.IconShapeMode = shape;
                IconFactory.Shape = (IconShape)shape;
                _settings.Save();
                UpdateMenuState();

                var current = DisplayDevice;
                UpdateIcon(current?.Battery, current?.Online == true,
                    current?.Battery?.Percent is int percent ? $"{percent}%" : "--");
                _widget?.RefreshView();
                _mainForm?.UpdateView();
            });
            _shapeItems.Add((item, shape));
            root.DropDownItems.Add(item);
        }
        return root;
    }
    private ToolStripMenuItem BuildOfflineMenu()
    {
        var root = new ToolStripMenuItem("关机判定");
        foreach (var option in new (int Seconds, string Text)[] { (0, "立即（无响应即停用）"), (30, "30 秒"), (60, "1 分钟"), (120, "2 分钟"), (300, "5 分钟"), (600, "10 分钟") })
        {
            int seconds = option.Seconds;
            var item = new ToolStripMenuItem(option.Text, null, (_, _) =>
            {
                _settings.OfflineAsOffSeconds = seconds;
                _service.PoweredOffAfterSeconds = seconds;
                _settings.Save();
                UpdateMenuState();
            });
            _offlineItems.Add((item, seconds));
            root.DropDownItems.Add(item);
        }
        return root;
    }
    private ToolStripMenuItem BuildPersonalizeMenu()
    {
        var root = new ToolStripMenuItem("个性化");

        var theme = new ToolStripMenuItem("主题");
        foreach (var option in new (int Mode, string Text)[] { (0, "跟随系统"), (1, "深色"), (2, "浅色") })
        {
            int mode = option.Mode;
            var item = new ToolStripMenuItem(option.Text, null, (_, _) =>
            {
                _settings.ThemeMode = mode;
                _settings.Save();
                UpdateMenuState();
                ApplyMenuStyle();
            });
            _themeItems.Add((item, mode));
            theme.DropDownItems.Add(item);
        }

        var opacity = new ToolStripMenuItem("不透明度");
        foreach (int percentValue in new[] { 100, 95, 90, 85 })
        {
            int value = percentValue;
            var item = new ToolStripMenuItem($"{percentValue}%", null, (_, _) =>
            {
                _settings.MenuOpacity = value;
                _settings.Save();
                UpdateMenuState();
                ApplyMenuStyle();
                ApplyWidgetStyle();
            });
            _opacityItems.Add((item, value));
            opacity.DropDownItems.Add(item);
        }

        var corner = new ToolStripMenuItem("圆角");
        foreach (var option in new (int Radius, string Text)[] { (8, "小"), (12, "中"), (16, "大") })
        {
            int radius = option.Radius;
            var item = new ToolStripMenuItem(option.Text, null, (_, _) =>
            {
                _settings.MenuCorner = radius;
                _settings.Save();
                UpdateMenuState();
                ApplyMenuStyle();
                ApplyWidgetStyle();
            });
            _cornerItems.Add((item, radius));
            corner.DropDownItems.Add(item);
        }

        root.DropDownItems.Add(theme);
        root.DropDownItems.Add(opacity);
        root.DropDownItems.Add(corner);
        return root;
    }

    private void ApplyMenuStyle()
    {
        bool light = _settings.ThemeMode switch
        {
            1 => false,
            2 => true,
            _ => Theme.IsLightTaskbar(),
        };

        var renderer = MenuStyle.CreateRenderer(light, _settings.MenuCorner, Theme.AccentFor(_settings.AccentIndex));
        Color back = light ? Color.FromArgb(250, 250, 252) : Color.FromArgb(30, 30, 32);

        ToolStripManager.Renderer = renderer;
        _menu.Renderer = renderer;
        _menu.BackColor = back;
        _menu.Padding = new Padding(6, 6, 6, 6);

        StyleDropdowns(_menu.Items, renderer, back);
        ApplyMenuShape();
    }

    /// <summary>把渲染器、背景色、透明度和圆角递归套用到所有子菜单。</summary>
    private void StyleDropdowns(ToolStripItemCollection items, ToolStripRenderer renderer, Color back)
    {
        foreach (ToolStripItem item in items)
        {
            if (item is not ToolStripDropDownItem dropItem) continue;

            var dropdown = dropItem.DropDown;
            dropdown.Renderer = renderer;
            dropdown.BackColor = back;
            dropdown.Padding = new Padding(6, 6, 6, 6);

            if (_styledDropdowns.Add(dropdown))
                dropdown.Opened += (_, _) => MenuStyle.ApplyMenuWindow(dropdown, _settings.MenuOpacity, _settings.MenuCorner);

            StyleDropdowns(dropdown.Items, renderer, back);
        }
    }

    private void ApplyMenuShape()
    {
        if (!_menu.IsHandleCreated) return;
        MenuStyle.ApplyMenuWindow(_menu, _settings.MenuOpacity, _settings.MenuCorner);
    }

    private void ApplyWidgetStyle()
    {
        if (_widget == null || _widget.IsDisposed) return;
        _widget.Opacity = Math.Clamp(_settings.MenuOpacity, 40, 100) / 100.0;
    }

    private ToolStripMenuItem BuildIntervalMenu()
    {
        var root = new ToolStripMenuItem("刷新间隔");
        var options = new (int Seconds, string Text)[]
        {
            (1, "1 秒（实时）"),
            (2, "2 秒"),
            (5, "5 秒"),
            (10, "10 秒"),
            (30, "30 秒"),
            (60, "1 分钟"),
        };

        foreach (var option in options)
        {
            int seconds = option.Seconds;
            var item = new ToolStripMenuItem(option.Text, null, (_, _) => SetInterval(seconds));
            _intervalItems[seconds] = item;
            root.DropDownItems.Add(item);
        }
        return root;
    }

    /// <summary>当前用于估算的“手动设备”（硬件读不到电量时使用）。</summary>
    private ManualBatteryDevice SelectedManualDevice
    {
        get
        {
            var list = _settings.ManualDevices;
            if (list == null || list.Count == 0) return null;

            string key = _settings.SelectedDeviceKey ?? "";
            if (key.StartsWith("manual:", StringComparison.OrdinalIgnoreCase))
            {
                string name = key["manual:".Length..];
                var match = list.FirstOrDefault(d => d.Name == name);
                if (match != null) return match;
            }

            return list[0];
        }
    }
    /// <summary>当前托盘图标要显示的设备。</summary>
    public LogiDevice DisplayDevice
    {
        get
        {
            var devices = _service.Devices;
            if (devices.Count == 0) return null;
            if (devices.Count == 1) return devices[0];

            string key = _settings.SelectedDeviceKey ?? "";

            if (key == "auto")
            {
                return devices.Where(d => d.Online && d.Battery?.Percent != null)
                              .OrderBy(d => d.Battery!.Percent)
                              .FirstOrDefault() ?? devices[0];
            }

            if (key.Length > 0)
            {
                var match = devices.FirstOrDefault(d => DeviceKey(d) == key && d.Online)
                            ?? devices.FirstOrDefault(d => DeviceKey(d) == key);
                if (match != null) return match;
            }

            return devices.FirstOrDefault(d => d.Online) ?? devices[0];
        }
    }

    private static string DeviceKey(LogiDevice device) => device.Path + "#" + device.Name;

    /// <summary>让所有子菜单都预留勾选位置（默认只有主菜单有）。</summary>
    private void ConfigureSubMenus()
    {
        void Visit(ToolStripItemCollection items)
        {
            foreach (ToolStripItem item in items)
            {
                if (item is not ToolStripMenuItem menu) continue;

                if (menu.DropDown is ToolStripDropDownMenu drop)
                {
                    drop.ShowCheckMargin = false;
                    drop.ShowImageMargin = false;
                    drop.Padding = new Padding(8, 4, 8, 6);
                    drop.Renderer = _menu.Renderer;
                    drop.Font = _menu.Font;
                    drop.BackColor = _menu.BackColor;
                }

                Visit(menu.DropDownItems);
            }
        }

        Visit(_menu.Items);
    }
    /// <summary>把勾选状态写进文字前缀，避免自绘勾选列错位。</summary>
    private void ApplyCheckMarks()
    {
        void Visit(ToolStripItemCollection items)
        {
            foreach (ToolStripItem entry in items)
            {
                if (entry is not ToolStripMenuItem item) continue;

                string baseText = item.Tag as string ?? item.Text.TrimStart('✓', ' ');
                item.Tag = baseText;

                item.Text = baseText;

                Visit(item.DropDownItems);
            }
        }

        Visit(_menu.Items);
    }
    private void RebuildDeviceMenu()
    {
        _deviceMenu.DropDownItems.Clear();

        foreach (var manual in _settings.ManualDevices ?? new List<ManualBatteryDevice>())
        {
            int estimated = manual.RemainingPercent(DateTime.UtcNow);
            string key = "manual:" + manual.Name;

            bool isCurrent = (_settings.SelectedDeviceKey ?? "") == key;
            string manualLabel = $"{manual.Name}（约 {estimated}%）" + (isCurrent ? "　当前" : "");

            var item = new ToolStripMenuItem(manualLabel, null, (_, _) =>
            {
                _settings.SelectedDeviceKey = key;
                _settings.Save();
                Apply(true);
            })
            {
                Checked = (_settings.SelectedDeviceKey ?? "") == key,
            };

            _deviceMenu.DropDownItems.Add(item);
        }

        var devices = _service.Devices;
        if ((_settings.ManualDevices?.Count ?? 0) > 0 && devices.Count > 0)
            _deviceMenu.DropDownItems.Add(new ToolStripSeparator());
        if (devices.Count == 0)
        {
            _deviceMenu.DropDownItems.Add(new ToolStripMenuItem("（未检测到设备）") { Enabled = false });
            return;
        }

        string currentKey = DisplayDevice is LogiDevice current ? DeviceKey(current) : "";
        string selected = _settings.SelectedDeviceKey ?? "";

        if (devices.Count > 1)
        {
            var auto = new ToolStripMenuItem("自动（电量最低）", null, (_, _) =>
            {
                _settings.SelectedDeviceKey = "auto";
                _settings.Save();
                RefreshAsync();
            })
            {
                Checked = selected == "auto",
            };
            _deviceMenu.DropDownItems.Add(auto);
            _deviceMenu.DropDownItems.Add(new ToolStripSeparator());
        }

        foreach (var device in devices)
        {
            string key = DeviceKey(device);
            string percent = device.Battery?.Percent is int value ? $"{value}%" : "--";
            string state = device.Online ? "" : device.PoweredOff ? "（停用）" : "（休眠）";

            bool isCurrentDevice = (selected.Length > 0 ? selected : currentKey) == key;

            var item = new ToolStripMenuItem($"{device.Name}   {percent}{state}" + (isCurrentDevice ? "　当前" : ""))
            {
                Checked = isCurrentDevice,
            };

            item.Click += (_, _) =>
            {
                _settings.SelectedDeviceKey = key;
                _settings.Save();
                RefreshAsync();
            };

            _deviceMenu.DropDownItems.Add(item);
        }

        ConfigureSubMenus();
    }
    private void ShowDetails()
    {
        if (_mainForm != null && !_mainForm.IsDisposed)
        {
            // 窗口可能被隐藏到托盘了：先显示再提到前台
            if (!_mainForm.Visible) _mainForm.Show();

            if (_mainForm.WindowState == FormWindowState.Minimized)
                _mainForm.WindowState = FormWindowState.Normal;

            _mainForm.TopMost = true;
            _mainForm.TopMost = false;
            _mainForm.Activate();
            _mainForm.BringToFront();
            return;
        }

        _mainForm = new MainForm(this);
        _mainForm.FormClosed += (_, _) => _mainForm = null;
        _mainForm.Show();
        _mainForm.UpdateView();
    }

    /// <summary>读取设置并恢复上次的灯效。</summary>
    private void RestoreLampEffect()
    {
        try
        {
            int mode = _settings.LampEffectMode;
            if (mode <= 0) return;                       // 0 = 上次是关闭，不恢复

            var color = ColorTranslator.FromHtml(string.IsNullOrEmpty(_settings.LampColor) ? "#3A82FF" : _settings.LampColor);
            var lampMode = (LampMode)mode;

            LampEngine.Configure(lampMode, color,
                Math.Clamp(_settings.LampBrightness, 1, 100),
                Math.Clamp(_settings.LampSpeed, 0.4, 2.4));

            Log.Write($"恢复灯效: {lampMode} #{color.R:X2}{color.G:X2}{color.B:X2}");
        }
        catch (Exception ex)
        {
            Log.Write("恢复灯效失败: " + ex.Message);
        }
    }

    /// <summary>把当前灯效参数写入设置。</summary>
    private void SaveLampEffect()
    {
        try
        {
            _settings.LampEffectMode = (int)LampEngine.Mode;
            var color = LampEngine.BaseColor;
            _settings.LampColor = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
            _settings.LampBrightness = LampEngine.Brightness;
            _settings.LampSpeed = LampEngine.Speed;
            _settings.Save();
        }
        catch (Exception ex)
        {
            Log.Write("保存灯效失败: " + ex.Message);
        }
    }
    public AppSettings Settings => _settings;
    public BatteryService Service => _service;

    public void RefreshNow() => RefreshAsync();

    /// <summary>关掉再重开主窗口（用来应用新配色）。</summary>
    public void ReopenWindow()
    {
        if (_mainForm != null && !_mainForm.IsDisposed)
        {
            _mainForm.SuppressExitOnClose = true;
            _mainForm.Close();
        }

        ShowDetails();
    }
    public void OpenLogFolderPublic() => OpenLogFolder();
    public void ExitAppPublic() => ExitApp();

    public void ApplySettingsFromWindow()
    {
        _settings.Save();
        ApplyMenuStyle();
        IconFactory.Shape = (IconShape)_settings.IconShapeMode;
        _service.PoweredOffAfterSeconds = _settings.OfflineAsOffSeconds;
        UpdateMenuState();

        var device = DisplayDevice;
        UpdateIcon(device?.Battery, device?.Online == true,
            device?.Battery?.Percent is int percent ? $"{percent}%" : "--");
        _widget?.RefreshView();
        _mainForm?.UpdateView();
    }

    private void ShowWidget()
    {
        if (_widget != null && !_widget.IsDisposed) return;

        _widget = new BatteryWidget(ShowDetails, _menu, _settings, () => _settings.Save());
        _widget.FormClosed += (_, _) => _widget = null;
        _widget.Show();
        ApplyWidgetStyle();
        _widget.UpdateView(DisplayDevice, DisplayDevice?.Online == true);
    }

    private void DockWidgetToTaskbar()
    {
        if (_widget == null || _widget.IsDisposed) return;

        _settings.WidgetX = int.MinValue;
        _settings.WidgetY = int.MinValue;
        _settings.Save();
        _widget.DockToTaskbar();
    }

    /// <summary>
    /// 把本程序在通知区域的图标设为「在任务栏显示」（不折叠进 ^ 面板）。
    /// Win11 22H2+ 用 HKCU\Control Panel\NotifyIconSettings\*!IsPromoted 控制。
    /// </summary>
    private void PromoteTrayIcon()
    {
        try
        {
            string exe = Environment.ProcessPath ?? Application.ExecutablePath;
            string name = System.IO.Path.GetFileNameWithoutExtension(exe);

            using var root = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Control Panel\NotifyIconSettings", true);
            if (root == null) return;

            bool found = false;
            foreach (string keyName in root.GetSubKeyNames())
            {
                using var sub = root.OpenSubKey(keyName, true);
                if (sub == null) continue;

                string path = sub.GetValue("ExecutablePath") as string;
                if (string.IsNullOrEmpty(path)) continue;
                if (!path.Contains(name, StringComparison.OrdinalIgnoreCase)) continue;

                sub.SetValue("IsPromoted", 1, Microsoft.Win32.RegistryValueKind.DWord);
                found = true;
                Log.Write("通知区域图标已设为固定显示: " + path);
            }

            if (!found) Log.Write("未找到通知区域条目（图标可能还没生成，稍后重试）");
        }
        catch (Exception ex)
        {
            Log.Write("设置通知区域图标失败: " + ex.Message);
        }
    }
    private void OnTrayMouseClick(object sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left) ShowDetails();
    }

    private void OnTimerTick(object sender, EventArgs e)
    {
        // 在线时每秒探一次（断电/休眠能立刻发现）；离线时每 2 秒重试（开机后能马上回来）
        bool online = DisplayDevice?.Online == true;
        _timer.Interval = online
            ? Math.Min(1000, Math.Max(1, _settings.IntervalSeconds) * 1000)
            : 2000;
        RefreshAsync();
    }

    private void OnMouseActivity()
    {
        // 已连接时响应快一些；还没找到设备时降低扫描频率，避免频繁超时探测
        int seconds = DisplayDevice == null ? 10 : 2;
        if (DateTime.Now - _lastActivityRefresh < TimeSpan.FromSeconds(seconds)) return;
        _lastActivityRefresh = DateTime.Now;
        RefreshAsync();
    }

    private void OnDeviceChanged(string devicePath)
    {
        // 插拔会连续来多条消息，节流一下
        if (DateTime.Now - _lastDeviceEvent < TimeSpan.FromMilliseconds(250)) return;
        _lastDeviceEvent = DateTime.Now;

        // 只有罗技设备的插拔才清空状态，避免插个键盘也闪一下
        bool logitech = devicePath.IndexOf("vid_046d", StringComparison.OrdinalIgnoreCase) >= 0;
        if (logitech) _service.Reset();

        // 插上时直接用事件里的接口路径连接，比整轮枚举快很多
        if (logitech && _service.TryConnect(devicePath))
        {
            Apply(true);
            _burstRemaining = 2;
            _burstTimer.Start();
            return;
        }

        // 还没枚举好：马上刷一次，再每 250ms 补几次
        RefreshAsync();
        _burstRemaining = 6;
        _burstTimer.Start();
    }
    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) RefreshAsync();
    }

    private void RefreshAsync()
    {
        if (_refreshing)
        {
            _pendingRefresh = true;
            return;
        }
        _refreshing = true;

        Task.Run(() =>
        {
            bool ok = _service.Refresh();
            try
            {
                _dispatcher.BeginInvoke(new Action(() =>
                {
                    _refreshing = false;
                    Apply(ok);

                    if (_pendingRefresh)
                    {
                        _pendingRefresh = false;
                        RefreshAsync();
                    }
                }));
            }
            catch
            {
                _refreshing = false;
            }
        });
    }

    /// <summary>用户在「显示设备」里明确选了估算设备时返回它。</summary>
    private ManualBatteryDevice ExplicitManualDevice()
    {
        string key = _settings.SelectedDeviceKey ?? "";
        if (!key.StartsWith("manual:", StringComparison.OrdinalIgnoreCase)) return null;

        string name = key["manual:".Length..];
        return _settings.ManualDevices?.FirstOrDefault(d => d.Name == name);
    }

    /// <summary>把标题补齐到固定像素宽度，保证菜单宽度不随设备名变化。</summary>
    private static string PadHeader(string text, int targetPixels = 300)
    {
        var font = new Font("Segoe UI", 9f);
        try
        {
            int width = TextRenderer.MeasureText(text, font).Width;
            if (width >= targetPixels) return text;

            var builder = new System.Text.StringBuilder(text);
            int spaceWidth = Math.Max(6, TextRenderer.MeasureText("\u3000", font).Width);

            while (width < targetPixels)
            {
                builder.Append('\u3000');
                width += spaceWidth;
            }

            return builder.ToString();
        }
        finally
        {
            font.Dispose();
        }
    }
    /// <summary>检查所有设备，任一低于 20% 立即弹系统通知（每台只提醒一次）。</summary>
    private void CheckLowBatteryForAllDevices()
    {
        if (!_settings.LowBatteryNotify) return;

        var low = new List<string>();
        var alive = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var device in _service.Devices)
        {
            if (device.Battery?.Percent is not int percent) continue;
            if (device.Battery.Charging || device.Battery.Full) continue;

            string key = DeviceKey(device);
            alive.Add(key);

            if (percent < IconFactory.LowBatteryThreshold)
            {
                if (_lowNotified.Add(key)) low.Add($"{device.Name} {percent}%");
            }
            else if (percent > IconFactory.LowBatteryThreshold + 5)
            {
                // 回到安全区间后重新武装，下次再低还会提醒
                _lowNotified.Remove(key);
            }
        }

        foreach (var manual in _settings.ManualDevices ?? new List<ManualBatteryDevice>())
        {
            int percent = manual.RemainingPercent(DateTime.UtcNow);
            string key = "manual:" + manual.Name;
            alive.Add(key);

            if (percent < IconFactory.LowBatteryThreshold)
            {
                if (_lowNotified.Add(key)) low.Add($"{manual.Name} {percent}%");
            }
            else if (percent > IconFactory.LowBatteryThreshold + 5)
            {
                _lowNotified.Remove(key);
            }
        }

        // 已不存在的设备清掉记录
        _lowNotified.RemoveWhere(k => !alive.Contains(k));

        if (low.Count == 0) return;

        _tray.BalloonTipTitle = "电量偏低提醒";
        _tray.BalloonTipText = string.Join("\n", low.Select(x => $"· {x} 已低于 {IconFactory.LowBatteryThreshold}%")) + "\n请及时充电或更换电池。";
        _tray.BalloonTipIcon = ToolTipIcon.Warning;
        _tray.ShowBalloonTip(10000);

        Log.Write("低电量提醒: " + string.Join(" | ", low));
    }
    private void Apply(bool ok)
    {
        // 全局低电量检查：与「托盘显示哪台」无关
        CheckLowBatteryForAllDevices();

        // 明确选了估算设备 → 直接显示它（不看罗技设备在不在线）
        var explicitManual = ExplicitManualDevice();
        if (explicitManual != null)
        {
            int manualPercent = explicitManual.RemainingPercent(DateTime.UtcNow);
            var reading = new BatteryReading { Percent = manualPercent };

            _header.Text = PadHeader(manualPercent <= 0
                ? $"{explicitManual.Name}   停用"
                : $"{explicitManual.Name}   约 {manualPercent}%");
            _settings.LastDeviceName = explicitManual.Name;

            UpdateIcon(reading, true, $"{manualPercent}%");
            UpdateMenuState();
            _widget?.UpdateView(null, false);
            SetTrayText($"{explicitManual.Name} 约 {manualPercent}% · 还剩 {ManualBatteryDevice.FormatDuration(explicitManual.RemainingDays(DateTime.UtcNow))}");

            _mainForm?.UpdateView();   // 提前返回也要刷新窗口，否则界面会停在旧状态
            return;
        }

        var device = DisplayDevice;

        // 没有可用硬件设备时，用「估算设备」顶上
        if (device == null)
        {
            var manual = SelectedManualDevice;
            if (manual != null)
            {
                int estimated = manual.RemainingPercent(DateTime.UtcNow);
                var estimate = new BatteryReading { Percent = estimated };

                _header.Text = PadHeader(estimated <= 0
                    ? $"{manual.Name}   停用"
                    : $"{manual.Name}   约 {estimated}%");
                _settings.LastDeviceName = manual.Name;

                UpdateIcon(estimate, true, $"{estimated}%");
                UpdateMenuState();
                _widget?.UpdateView(null, false);
                _tray.Text = $"{manual.Name} 约 {estimated}% · 还剩 {ManualBatteryDevice.FormatDuration(manual.RemainingDays(DateTime.UtcNow))}";

                _mainForm?.UpdateView();
                return;
            }
        }

        var battery = device?.Battery;
        bool online = device != null && device.Online && battery != null;

        string percentText = battery?.Percent is int percent ? $"{percent}%" : "--";
        string status = battery?.StatusText ?? "";
        if (battery?.Charging == true && _service.ChargeRatePerHour is double rate && rate >= 0.5)
            status += $" · 约 +{rate:F0}%/小时";

        if (online)
        {
            _header.Text = PadHeader($"{device.Name}   {percentText}   {status}");
            _settings.LastDeviceName = device.Name;
        }
        else if (device != null)
        {
            _header.Text = battery?.Percent != null
                ? PadHeader($"{device.Name}   {percentText}   {(device.PoweredOff ? "停用" : "休眠")}")
                : PadHeader($"{device.Name}   等待响应");
        }
        else
        {
            _header.Text = _service.Problem ?? "等待鼠标唤醒";
        }

        UpdateIcon(battery, online, percentText);
        UpdateMenuState();

        _widget?.UpdateView(device, online);

        if (_header.Text != _lastState)
        {
            _lastState = _header.Text;
            Log.Write("状态: " + _header.Text + (_service.Problem == null ? "" : " / " + _service.Problem));
        }

        _mainForm?.UpdateView();
    }

    private string _lastIconKey;

    private void UpdateIcon(BatteryReading battery, bool online, string percentText)
    {
        string iconKey = $"{battery?.Percent}|{battery?.Charging}|{online}|{IconFactory.Shape}";
        if (iconKey != _lastIconKey || _tray.Icon == null)
        {
            _lastIconKey = iconKey;
            try
            {
                var old = _tray.Icon;
                _tray.Icon = IconFactory.Create(
                    Math.Max(32, SystemInformation.SmallIconSize.Width),
                    battery,
                    Theme.IsLightTaskbar(),
                    !online);
                old?.Dispose();
            }
            catch (Exception ex)
            {
                Log.Write("更新图标失败: " + ex.Message);
            }
        }

        string tip;

        var devices = _service.Devices;
        if (devices.Count > 1)
        {
            // 多设备：悬停提示里列出全部电量
            var parts = devices.Select(device =>
            {
                string value = device.Battery?.Percent is int percent ? $"{percent}%" : "--";
                string state = device.Online ? "" : device.PoweredOff ? "(停用)" : "(休眠)";
                return $"{device.Name} {value}{state}";
            });

            tip = string.Join(" · ", parts);
        }
        else if (online)
        {
            tip = $"{_settings.LastDeviceName} {percentText} · {battery.StatusText}";
        }
        else if (battery?.Percent is int last)
        {
            tip = $"{_settings.LastDeviceName} {(DisplayDevice?.PoweredOff == true ? "停用" : "休眠")} · 上次 {last}%";
        }
        else
        {
            tip = _service.Problem ?? "等待鼠标唤醒";
        }

        SetTrayText(tip);
    }

    /// <summary>NotifyIcon.Text 有长度限制，超长时逐级回退。</summary>
    private void SetTrayText(string tip)
    {
        foreach (int limit in new[] { 120, 60 })
        {
            try
            {
                _tray.Text = tip.Length > limit ? tip[..limit] : tip;
                return;
            }
            catch
            {
                // 换更短的限制再试
            }
        }
    }

    private void UpdateMenuState()
    {
        _autostartItem.Text = "开机自动启动" + (AppSettings.IsAutostartEnabled() ? "　　开" : "　　关");
        _notifyItem.Text = "低电量提醒" + (_settings.LowBatteryNotify ? "　　开" : "　　关");

        foreach (var pair in _intervalItems)
            pair.Value.Checked = pair.Key == _settings.IntervalSeconds;

        foreach (var (item, mode) in _themeItems)
            item.Checked = mode == _settings.ThemeMode;

        foreach (var (item, percent) in _opacityItems)
            item.Checked = percent == _settings.MenuOpacity;

        foreach (var (item, radius) in _cornerItems)
            item.Checked = radius == _settings.MenuCorner;

        foreach (var (item, seconds) in _offlineItems)
            item.Checked = seconds == _settings.OfflineAsOffSeconds;

    }

    private void ToggleLowBatteryNotify()
    {
        _settings.LowBatteryNotify = !_settings.LowBatteryNotify;
        _settings.Save();
        UpdateMenuState();
    }
    private void ToggleAutostart()
    {
        bool enable = !AppSettings.IsAutostartEnabled();
        if (AppSettings.SetAutostart(enable))
        {
            _settings.Autostart = enable;
            _settings.Save();
            UpdateMenuState();
        }
        else
        {
            MessageBox.Show("无法修改开机启动项，请检查系统权限。", "罗技鼠标电量",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void SetInterval(int seconds)
    {
        _settings.IntervalSeconds = seconds;
        _settings.Save();
        _timer.Interval = Math.Max(1, seconds) * 1000;
        UpdateMenuState();
        RefreshAsync();
    }

    private void OpenLogFolder()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = AppPaths.Directory,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Log.Write("打开文件夹失败: " + ex.Message);
        }
    }

    private void ExitApp()
    {
        _timer.Stop();
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        _mouseWatcher.Dispose();
        _deviceWatcher.Dispose();
        _widget?.Close();
        _widget?.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            _mouseWatcher?.Dispose();
            _widget?.Dispose();
            _tray.Dispose();
            _dispatcher.Dispose();
        }
        base.Dispose(disposing);
    }
}
