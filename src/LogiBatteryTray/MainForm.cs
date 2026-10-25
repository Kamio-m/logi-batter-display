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

/// <summary>左键点托盘图标打开的设置窗口。</summary>
internal sealed class MainForm : Form
{
    private readonly IMainFormHost _app;
    private readonly bool _light;

    private readonly Panel _card;
    private readonly Label _nameLabel;
    private readonly Label _statusLabel;
    private readonly Label _percentLabel;
    private readonly Label _updatedLabel;
    private readonly PictureBox _ringBox;

    private readonly CheckBox _autostartCheck;
    private readonly CheckBox _notifyCheck;
    private readonly CheckBox _percentCheck;

    private readonly Button _intervalButton;
    private readonly Button _shapeButton;
    private readonly Button _offlineButton;

    private readonly Color _background;
    private readonly Color _cardColor;
    private readonly Color _border;
    private readonly Color _fore;
    private readonly Color _secondary;

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

        _background = _light ? Color.FromArgb(246, 246, 248) : Color.FromArgb(30, 30, 32);
        _cardColor = _light ? Color.White : Color.FromArgb(39, 39, 42);
        _border = _light ? Color.FromArgb(226, 226, 231) : Color.FromArgb(56, 56, 60);
        _fore = _light ? Color.FromArgb(27, 27, 31) : Color.FromArgb(242, 242, 245);
        _secondary = _light ? Color.FromArgb(107, 107, 114) : Color.FromArgb(156, 156, 163);

        Text = "LogiBatteryTray";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = _background;
        ForeColor = _fore;
        Font = new Font("Segoe UI", 9f);
        ClientSize = new Size((int)S(440), (int)S(578));

        _card = new Panel { Location = P(16, 16), Size = new Size((int)S(408), (int)S(164)), BackColor = Color.Transparent };
        _card.Paint += PaintCard;
        Controls.Add(_card);

        _nameLabel = MakeLabel(32, 24, "罗技鼠标", 13f, FontStyle.Bold, _fore, _card);
        _statusLabel = MakeLabel(32, 50, "使用中", 9.5f, FontStyle.Regular, _secondary, _card);

        _ringBox = new PictureBox
        {
            Location = P(32, 74),
            Size = new Size((int)S(76), (int)S(76)),
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.Transparent,
        };
        _card.Controls.Add(_ringBox);

        _percentLabel = MakeLabel(118, 74, "--", 30f, FontStyle.Bold, _fore, _card);
        _updatedLabel = MakeLabel(118, 128, "", 8.5f, FontStyle.Regular, _secondary, _card);

        MakeLabel(20, 196, "设置", 10f, FontStyle.Bold, _fore);

        _autostartCheck = MakeCheck(24, 222, "开机自动启动");
        _notifyCheck = MakeCheck(24, 252, "电量低于 20% 时通知");
        _percentCheck = MakeCheck(24, 282, "图标显示百分比（仅电池图标）");

        _intervalButton = MakeValueButton(24, 320, "刷新间隔", "5 分钟");
        _shapeButton = MakeValueButton(24, 358, "图标形状", "圆环");
        _offlineButton = MakeValueButton(24, 396, "关机判定", "2 分钟");

        MakeLabel(24, 434, "点击右侧数值可切换", 8f, FontStyle.Regular, _secondary);

        MakeLabel(20, 462, "关于", 10f, FontStyle.Bold, _fore);
        MakeLabel(24, 486, $"LogiBatteryTray {typeof(MainForm).Assembly.GetName().Version?.ToString(3)}",
            8.5f, FontStyle.Regular, _secondary);
        MakeLabel(24, 506, @"数据目录 %APPDATA%\LogiBatteryTray", 8.5f, FontStyle.Regular, _secondary);

        var refresh = MakeButton(24, 530, "立即刷新", primary: true);
        refresh.Click += (_, _) => _app.RefreshNow();

        var log = MakeButton(148, 530, "打开日志");
        log.Click += (_, _) => _app.OpenLogFolderPublic();

        var quit = MakeButton(272, 530, "退出");
        quit.Click += (_, _) => { _app.ExitAppPublic(); Close(); };

        _autostartCheck.CheckedChanged += (_, _) => OnSettingChanged();
        _notifyCheck.CheckedChanged += (_, _) => OnSettingChanged();
        _percentCheck.CheckedChanged += (_, _) => OnSettingChanged();

        _intervalButton.Click += (_, _) => Cycle(_intervalButton, IntervalValues, IntervalTexts, value => _app.Settings.IntervalSeconds = value);
        _shapeButton.Click += (_, _) => Cycle(_shapeButton, ShapeValues, ShapeTexts, value => _app.Settings.IconShapeMode = value);
        _offlineButton.Click += (_, _) => Cycle(_offlineButton, OfflineValues, OfflineTexts, value => _app.Settings.OfflineAsOffSeconds = value);

        LoadFromSettings();
        UpdateView();
    }

    private float S(float value) => value * DeviceDpi / 96f;

    private Point P(float x, float y) => new((int)S(x), (int)S(y));

    private Label MakeLabel(float x, float y, string text, float size, FontStyle style, Color color, Control parent = null)
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
        (parent ?? this).Controls.Add(label);
        return label;
    }

    private CheckBox MakeCheck(float x, float y, string text)
    {
        var check = new CheckBox
        {
            Text = text,
            AutoSize = true,
            Location = P(x, y),
            ForeColor = _fore,
            FlatStyle = FlatStyle.Flat,
        };
        Controls.Add(check);
        return check;
    }

    /// <summary>一行设置：[标签] [可点击的当前值]。</summary>
    private Button MakeValueButton(float x, float y, string caption, string value)
    {
        float captionWidth;
        using (var measure = CreateGraphics())
        using (var font = new Font("Segoe UI", 9f))
            captionWidth = measure.MeasureString(caption, font).Width;

        MakeLabel(x, y + S(6), caption, 9f, FontStyle.Regular, _fore);

        var button = new Button
        {
            Text = value,
            Location = P(x + captionWidth + 18, y),
            Size = new Size((int)S(150), (int)S(30)),
            FlatStyle = FlatStyle.Flat,
            BackColor = _light ? Color.FromArgb(240, 240, 244) : Color.FromArgb(45, 45, 49),
            ForeColor = _fore,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding((int)S(8), 0, 0, 0),
        };
        button.FlatAppearance.BorderColor = _border;
        Controls.Add(button);
        return button;
    }

    private Button MakeButton(float x, float y, string text, bool primary = false)
    {
        var button = new Button
        {
            Text = text,
            Location = P(x, y),
            Size = new Size((int)S(112), (int)S(32)),
            FlatStyle = FlatStyle.Flat,
            BackColor = primary
                ? Color.FromArgb(10, 132, 255)
                : (_light ? Color.FromArgb(240, 240, 244) : Color.FromArgb(45, 45, 49)),
            ForeColor = primary ? Color.White : _fore,
        };
        button.FlatAppearance.BorderColor = _border;
        Controls.Add(button);
        return button;
    }

    /// <summary>点击切换下一个取值。</summary>
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

    private void PaintCard(object sender, PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using var path = Rounded(new RectangleF(0, 0, _card.Width, _card.Height), S(12));
        using (var fill = new SolidBrush(_cardColor)) g.FillPath(fill, path);
        using (var pen = new Pen(_border, 1f)) g.DrawPath(pen, path);
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

        int size = (int)S(76);
        var previous = _ringBox.Image;
        try
        {
            using var icon = IconFactory.Create(size, battery, _app.Settings.ShowPercentInIcon, _light,
                device != null && !online);
            _ringBox.Image = icon.ToBitmap();
        }
        catch
        {
            // 绘制失败时保持原图
        }
        previous?.Dispose();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _ringBox.Image?.Dispose();
        base.OnFormClosed(e);
    }
}
