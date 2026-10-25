using Microsoft.Win32;

namespace LogiBatteryTray;

/// <summary>主题判断（浅色/深色任务栏）。</summary>
internal static class Theme
{
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
    private readonly ToolStripMenuItem _percentItem;
    private readonly ToolStripMenuItem _autostartItem;
    private readonly ToolStripMenuItem _notifyItem;
    private readonly Dictionary<int, ToolStripMenuItem> _intervalItems = new();
    private readonly List<(ToolStripMenuItem Item, int Mode)> _themeItems = new();
    private readonly List<(ToolStripMenuItem Item, int Percent)> _opacityItems = new();
    private readonly List<(ToolStripMenuItem Item, int Radius)> _cornerItems = new();
    private readonly List<(ToolStripMenuItem Item, int Seconds)> _offlineItems = new();
    private readonly List<(ToolStripMenuItem Item, int Shape)> _shapeItems = new();
    private readonly HashSet<ToolStripDropDown> _styledDropdowns = new();
    private readonly MouseActivityWatcher _mouseWatcher;

    private MainForm _mainForm;
    private BatteryWidget _widget;
    private bool _refreshing;
    private DateTime _lastLowWarning = DateTime.MinValue;
    private DateTime _lastActivityRefresh = DateTime.MinValue;
    private string _lastState = "";

    public TrayApp()
    {
        _settings = AppSettings.Load();
        if (!string.IsNullOrEmpty(_settings.LoadError)) Log.Write("读取设置失败: " + _settings.LoadError);

        _dispatcher = new Control();
        _dispatcher.CreateControl();

        _header = new ToolStripMenuItem("正在读取…") { Enabled = false };
        try { _header.Font = new Font(_header.Font, FontStyle.Bold); } catch { }

        _percentItem = new ToolStripMenuItem("图标显示百分比", null, (_, _) => TogglePercent());
        _autostartItem = new ToolStripMenuItem("开机自动启动", null, (_, _) => ToggleAutostart());
        _notifyItem = new ToolStripMenuItem("低电量提醒", null, (_, _) => ToggleLowBatteryNotify());

        IconFactory.Style = BatteryIconStyle.OutlinePlug;
        IconFactory.Shape = (IconShape)_settings.IconShapeMode;

        _menu = new ContextMenuStrip { ShowImageMargin = false, ShowCheckMargin = true };
        _menu.Opened += (_, _) =>
        {
            MenuStyle.ApplyMenuWindow(_menu, _settings.MenuOpacity, _settings.MenuCorner);
            RefreshAsync();
        };
        _menu.Items.Add(_header);
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(new ToolStripMenuItem("刷新", null, (_, _) => RefreshAsync()) { ShortcutKeyDisplayString = "F5" });
        _menu.Items.Add(new ToolStripMenuItem("贴回任务栏", null, (_, _) => DockWidgetToTaskbar()));
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add(_percentItem);
        _menu.Items.Add(_notifyItem);
        _menu.Items.Add(_autostartItem);
        _menu.Items.Add(BuildIntervalMenu());
        _menu.Items.Add(BuildPersonalizeMenu());
        _menu.Items.Add(BuildShapeMenu());
        _menu.Items.Add(BuildOfflineMenu());
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
            Icon = IconFactory.Create(32, null, false, Theme.IsLightTaskbar()),
        };
        _tray.MouseClick += OnTrayMouseClick;
        _tray.MouseDoubleClick += (_, _) => ShowDetails();

        // 1.5 秒后向系统确认图标是否真的进了通知区域；失败重试一次，仍失败则切换悬浮窗
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

        _timer = new System.Windows.Forms.Timer { Interval = 600 };
        _timer.Tick += OnTimerTick;
        _timer.Start();

        _mouseWatcher = new MouseActivityWatcher();
        _mouseWatcher.Activity += OnMouseActivity;

        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.SessionSwitch += (_, _) => RefreshAsync();

        UpdateMenuState();
        RefreshAsync();
    }

    private ToolStripMenuItem BuildShapeMenu()
    {
        var root = new ToolStripMenuItem("图标形状");
        foreach (var option in new (int Shape, string Text)[] { (0, "圆环"), (1, "电池图标") })
        {
            int shape = option.Shape;
            var item = new ToolStripMenuItem(option.Text, null, (_, _) =>
            {
                _settings.IconShapeMode = shape;
                IconFactory.Shape = (IconShape)shape;
                _settings.Save();
                UpdateMenuState();

                var current = _service.Primary;
                UpdateIcon(current?.Battery, current?.Online == true,
                    current?.Battery?.Percent is int percent ? $"{percent}%" : "--");
                _widget?.RefreshView();
            });
            _shapeItems.Add((item, shape));
            root.DropDownItems.Add(item);
        }
        return root;
    }
    private ToolStripMenuItem BuildOfflineMenu()
    {
        var root = new ToolStripMenuItem("关机判定");
        foreach (var option in new (int Seconds, string Text)[] { (60, "1 分钟"), (120, "2 分钟"), (300, "5 分钟"), (600, "10 分钟") })
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

        var renderer = MenuStyle.CreateRenderer(light, _settings.MenuCorner);
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
            (5, "5 秒（实时）"),
            (10, "10 秒"),
            (30, "30 秒"),
            (60, "1 分钟"),
            (300, "5 分钟"),
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

    private void ShowDetails()
    {
        if (_mainForm != null && !_mainForm.IsDisposed)
        {
            _mainForm.Activate();
            return;
        }

        _mainForm = new MainForm(this);
        _mainForm.FormClosed += (_, _) => _mainForm = null;
        _mainForm.Show();
        _mainForm.UpdateView();
    }

    public AppSettings Settings => _settings;
    public BatteryService Service => _service;

    public void RefreshNow() => RefreshAsync();
    public void OpenLogFolderPublic() => OpenLogFolder();
    public void ExitAppPublic() => ExitApp();

    public void ApplySettingsFromWindow()
    {
        _settings.Save();
        IconFactory.Shape = (IconShape)_settings.IconShapeMode;
        _service.PoweredOffAfterSeconds = _settings.OfflineAsOffSeconds;
        UpdateMenuState();

        var device = _service.Primary;
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
        _widget.UpdateView(_service.Primary, _service.Primary?.Online == true);
    }

    private void DockWidgetToTaskbar()
    {
        if (_widget == null || _widget.IsDisposed) return;

        _settings.WidgetX = int.MinValue;
        _settings.WidgetY = int.MinValue;
        _settings.Save();
        _widget.DockToTaskbar();
    }

    private void OnTrayMouseClick(object sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left) ShowDetails();
    }

    private void OnTimerTick(object sender, EventArgs e)
    {
        // 没找到设备或设备离线时加快重试，避免拔插接收器/插线后长时间不更新
        _timer.Interval = _service.Primary == null || !_service.Primary.Online
            ? 15_000
            : Math.Max(5, _settings.IntervalSeconds) * 1000;
        RefreshAsync();
    }

    private void OnMouseActivity()
    {
        // 已连接时响应快一些；还没找到设备时降低扫描频率，避免频繁超时探测
        int seconds = _service.Primary == null ? 15 : 5;
        if (DateTime.Now - _lastActivityRefresh < TimeSpan.FromSeconds(seconds)) return;
        _lastActivityRefresh = DateTime.Now;
        RefreshAsync();
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) RefreshAsync();
    }

    private void RefreshAsync()
    {
        if (_refreshing) return;
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
                }));
            }
            catch
            {
                _refreshing = false;
            }
        });
    }

    private void Apply(bool ok)
    {
        var device = _service.Primary;
        var battery = device?.Battery;
        bool online = device != null && device.Online && battery != null;

        string percentText = battery?.Percent is int percent ? $"{percent}%" : "--";
        string status = battery?.StatusText ?? "";
        if (battery?.Charging == true && _service.ChargeRatePerHour is double rate && rate >= 0.5)
            status += $" · 约 +{rate:F0}%/小时";

        if (online)
        {
            _header.Text = $"{device.Name}   {percentText}   {status}";
            _settings.LastDeviceName = device.Name;
        }
        else if (device != null)
        {
            _header.Text = battery?.Percent != null
                ? $"{device.Name}   {percentText}   {(device.PoweredOff ? "停用" : "休眠")}"
                : $"{device.Name}   等待响应";
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

        if (online && device.Battery.Percent is int value && value < IconFactory.LowBatteryThreshold
            && !device.Battery.Charging && !device.Battery.Full
            && _settings.LowBatteryNotify && DateTime.Now - _lastLowWarning > TimeSpan.FromHours(3))
        {
            _lastLowWarning = DateTime.Now;
            _tray.BalloonTipTitle = "罗技鼠标电量偏低";
            _tray.BalloonTipText = $"{device.Name} 剩余 {value}%，已低于 20%，建议尽快充电。";
            _tray.BalloonTipIcon = ToolTipIcon.Warning;
            _tray.ShowBalloonTip(8000);
        }

        _mainForm?.UpdateView();
    }

    private string _lastIconKey;

    private void UpdateIcon(BatteryReading battery, bool online, string percentText)
    {
        string iconKey = $"{battery?.Percent}|{battery?.Charging}|{online}|{_settings.ShowPercentInIcon}|{IconFactory.Shape}";
        if (iconKey != _lastIconKey || _tray.Icon == null)
        {
            _lastIconKey = iconKey;
            try
            {
                var old = _tray.Icon;
                _tray.Icon = IconFactory.Create(
                    Math.Max(32, SystemInformation.SmallIconSize.Width),
                    battery,
                    _settings.ShowPercentInIcon,
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
        if (online)
            tip = $"{_settings.LastDeviceName} {percentText} · {battery.StatusText}";
        else if (battery?.Percent is int last)
            tip = $"{_settings.LastDeviceName} {(_service.Primary?.PoweredOff == true ? "停用" : "休眠")} · 上次 {last}%";
        else
            tip = _service.Problem ?? "等待鼠标唤醒";

        if (tip.Length > 63) tip = tip[..63];

        try { _tray.Text = tip; } catch { }
    }

    private void UpdateMenuState()
    {
        _percentItem.Checked = _settings.ShowPercentInIcon;
        // 圆环本身不显示数字，这个选项只对电池图标有意义
        _percentItem.Visible = IconFactory.Shape == IconShape.Battery;
        _autostartItem.Checked = AppSettings.IsAutostartEnabled();
        _notifyItem.Checked = _settings.LowBatteryNotify;

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

    private void TogglePercent()
    {
        _settings.ShowPercentInIcon = !_settings.ShowPercentInIcon;
        _settings.Save();
        UpdateMenuState();

        var device = _service.Primary;
        UpdateIcon(device?.Battery, device?.Online == true, device?.Battery?.Percent is int p ? $"{p}%" : "--");
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
        _timer.Interval = Math.Max(5, seconds) * 1000;
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
