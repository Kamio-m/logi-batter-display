using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace LogiBatteryTray;

/// <summary>
/// 托盘图标不可用时的兜底：停靠在任务栏托盘区（系统电池图标左侧）的电量小卡。
/// 样式仿照 Windows 11 快捷设置里的电池行（白描边电池 + 百分比）。
/// 左键打开详情，右键菜单，按住可拖动（位置会记住）。
/// </summary>
internal sealed class BatteryWidget : Form
{
    private readonly Action _openDetails;
    private readonly ContextMenuStrip _menu;
    private readonly AppSettings _settings;
    private readonly Action _saveSettings;
    private readonly bool _light;

    private LogiDevice _device;
    private bool _online;
    private Point _downPoint;
    private bool _pressed;
    private bool _dragging;

    public BatteryWidget(Action openDetails, ContextMenuStrip menu, AppSettings settings, Action saveSettings)
    {
        _openDetails = openDetails;
        _menu = menu;
        _settings = settings;
        _saveSettings = saveSettings;
        _light = Theme.IsLightTaskbar();

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        DoubleBuffered = true;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Color.FromArgb(32, 32, 34);
        Size = new Size((int)S(98), (int)S(38));

        if (_settings.WidgetX != int.MinValue && _settings.WidgetY != int.MinValue)
            Location = new Point(_settings.WidgetX, _settings.WidgetY);
        else
            DockToTaskbar();
    }

    private float S(float value) => value * DeviceDpi / 96f;

    public void UpdateView(LogiDevice device, bool online)
    {
        _device = device;
        _online = online;
        Invalidate();
        KeepOnTop();
    }

    /// <summary>让窗口重绘（例如切换图标风格后）。</summary>
    public void RefreshView() => Invalidate();

    /// <summary>停靠回任务栏托盘区、系统图标组左侧。</summary>
    public void DockToTaskbar()
    {
        var tray = TaskbarGeometry.GetTrayRect();
        int width = Bounds.Width;
        int height = Bounds.Height;

        if (tray != null)
        {
            var area = tray.Value;
            int clockWidth = (int)(area.Height * 1.9f);
            int groupWidth = (int)(area.Height * 1.8f);
            int groupLeft = area.Right - clockWidth - groupWidth;

            // 用窗口真实矩形（含不可见的边框留白）计算，避免压到系统图标
            int realWidth = Bounds.Width;
            if (IsHandleCreated && GetWindowRect(Handle, out RECT actual))
                realWidth = actual.Right - actual.Left;

            int x = groupLeft - realWidth - (int)(area.Height * 0.20f);
            if (x < area.Left + 6) x = area.Left + 6;
            int y = area.Top + (area.Height - height) / 2;

            Location = new Point(x, y);
            Log.Write($"小卡停靠: 逻辑={width}x{height} 实际宽={realWidth} dpi={DeviceDpi} 托盘={area} 位置={x},{y}");
            return;
        }

        var work = Screen.PrimaryScreen.WorkingArea;
        Location = new Point(work.Right - width - (int)S(10), work.Bottom - height - (int)S(10));
        Log.Write($"小卡停靠(兜底): 尺寸={width}x{height} dpi={DeviceDpi} 位置={Location}");
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        KeepOnTop();
    }

    /// <summary>重新声明置顶，避免被其他置顶窗口压住。</summary>
    private void KeepOnTop()
    {
        if (!IsHandleCreated) return;
        SetWindowPos(Handle, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        using var path = Rounded(new RectangleF(0, 0, Width, Height), S(9));
        Region = new Region(path);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        var battery = _device?.Battery;
        bool charging = battery?.Charging == true || battery?.Full == true;

        Color background = _light ? Color.FromArgb(250, 250, 250) : Color.FromArgb(32, 32, 34);
        Color border = _light ? Color.FromArgb(224, 224, 228) : Color.FromArgb(64, 64, 68);
        Color foreground = _light ? Color.FromArgb(30, 30, 32) : Color.White;

        Color outline = _online ? foreground : Color.FromArgb(215, 150, 150, 156);
        Color fill = outline;
        if (_online && battery?.Percent is int level && !charging && level < IconFactory.LowBatteryThreshold)
            fill = IconFactory.Yellow;

        using (var bg = new SolidBrush(background))
            g.FillRectangle(bg, ClientRectangle);

        using (var pen = new Pen(border, 1f))
            g.DrawRectangle(pen, 0.5f, 0.5f, Width - 1f, Height - 1f);

        var batteryRect = new RectangleF(S(9), S(11), S(26), S(16));
        IconFactory.DrawBatteryAuto(g, batteryRect, battery?.Percent, charging, outline, fill);

        string percentText = battery?.Percent is int percent ? $"{percent}%" : "--%";
        using (var font = new Font("Segoe UI", 12.5f, FontStyle.Regular, GraphicsUnit.Point))
        using (var brush = new SolidBrush(fill == outline ? foreground : fill))
        using (var format = new StringFormat { LineAlignment = StringAlignment.Center })
        {
            var rect = new RectangleF(S(41), 0, Width - S(46), Height);
            g.DrawString(percentText, font, brush, rect, format);
        }
    }


    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left)
        {
            _pressed = true;
            _dragging = false;
            _downPoint = e.Location;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_pressed) return;

        int dx = e.X - _downPoint.X;
        int dy = e.Y - _downPoint.Y;
        if (!_dragging && (Math.Abs(dx) > 3 || Math.Abs(dy) > 3)) _dragging = true;
        if (_dragging)
        {
            Location = new Point(Left + dx, Top + dy);
            Cursor = Cursors.SizeAll;
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_pressed)
        {
            _pressed = false;
            if (_dragging)
            {
                _dragging = false;
                Cursor = Cursors.Default;

                _settings.WidgetX = Left;
                _settings.WidgetY = Top;
                _saveSettings?.Invoke();
            }
            else if (e.Button == MouseButtons.Left)
            {
                _openDetails();
            }
        }
        else if (e.Button == MouseButtons.Right)
        {
            _menu.Show(Cursor.Position);
        }
    }

    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter,
        int x, int y, int cx, int cy, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

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
}
