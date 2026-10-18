using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace LogiBatteryTray;

/// <summary>点击托盘图标弹出的小面板。</summary>
internal sealed class DetailsForm : Form
{
    private readonly BatteryService _service;
    private readonly Action _refresh;
    private readonly bool _light;
    private RectangleF _buttonRect;

    public DetailsForm(BatteryService service, Action refresh)
    {
        _service = service;
        _refresh = refresh;
        _light = Theme.IsLightTaskbar();

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        DoubleBuffered = true;
        KeyPreview = true;
        BackColor = _light ? Color.White : Color.FromArgb(28, 28, 30);
        Size = new Size((int)Dpi(320), (int)Dpi(196));
    }

    private float Dpi(float value) => value * DeviceDpi / 96f;

    public void ShowNearTray()
    {
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        Location = new Point(
            area.Right - Width - (int)Dpi(12),
            area.Bottom - Height - (int)Dpi(12));

        Show();
        Activate();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        using var path = Rounded(new RectangleF(0, 0, Width, Height), Dpi(14));
        Region = new Region(path);
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        Close();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape) Close();
        base.OnKeyDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Cursor = _buttonRect.Contains(e.Location) ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);

        if (_buttonRect.Contains(e.Location)) _refresh();
        Close();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        var device = _service.Primary;
        var battery = device?.Battery;
        bool online = device != null && device.Online && battery != null;

        Color background = _light ? Color.White : Color.FromArgb(28, 28, 30);
        Color border = _light ? Color.FromArgb(224, 224, 228) : Color.FromArgb(58, 58, 60);
        Color primary = _light ? Color.FromArgb(28, 28, 30) : Color.White;
        Color secondary = _light ? Color.FromArgb(110, 110, 115) : Color.FromArgb(152, 152, 157);
        Color accent = online ? IconFactory.LevelColor(battery) : IconFactory.UnknownColor;

        using (var brush = new SolidBrush(background))
            graphics.FillRectangle(brush, ClientRectangle);

        using (var pen = new Pen(border, 1f))
            graphics.DrawRectangle(pen, 0.5f, 0.5f, Width - 1f, Height - 1f);

        float pad = Dpi(18);
        float contentWidth = Width - pad * 2;

        string name = device?.Name ?? "罗技鼠标";
        string percentText = battery?.Percent is int percent ? $"{percent}%" : "--";
        string status = online
            ? battery.StatusText
            : battery?.Percent != null ? $"休眠 · 上次 {battery.Percent}%" : "未连接";
        if (online && battery?.Charging == true && _service.ChargeRatePerHour is double rate && rate >= 0.5)
            status += $" · 约 +{rate:F0}%/小时";
        string updated = device?.UpdatedAt is DateTime time
            ? $"更新于 {time:HH:mm:ss}"
            : (_service.Problem ?? "尚未读取到数据");

        using (var nameFont = new Font("Segoe UI", 10.5f, FontStyle.Regular, GraphicsUnit.Point))
        using (var nameBrush = new SolidBrush(secondary))
        {
            graphics.DrawString(name, nameFont, nameBrush, pad, Dpi(14));
        }

        float percentTop = Dpi(34);
        float percentWidth;
        using (var percentFont = new Font("Segoe UI", 34f, FontStyle.Bold, GraphicsUnit.Point))
        using (var percentBrush = new SolidBrush(online ? accent : secondary))
        {
            graphics.DrawString(percentText, percentFont, percentBrush, pad - Dpi(2), percentTop);
            percentWidth = graphics.MeasureString(percentText, percentFont).Width;
        }

        using (var statusFont = new Font("Segoe UI", 10f, FontStyle.Regular, GraphicsUnit.Point))
        using (var statusBrush = new SolidBrush(secondary))
        {
            graphics.DrawString(status, statusFont, statusBrush,
                pad + percentWidth + Dpi(10), percentTop + Dpi(28));
        }

        // 电量条
        float barTop = Dpi(108);
        float barHeight = Dpi(9);
        using (var trackBrush = new SolidBrush(Color.FromArgb(45, secondary)))
        {
            graphics.FillPath(trackBrush, Rounded(new RectangleF(pad, barTop, contentWidth, barHeight), barHeight / 2));
        }

        if (battery?.Percent is int value)
        {
            float ratio = Math.Clamp(value / 100f, 0f, 1f);
            ratio = Math.Max(ratio, 0.03f);

            using var fillBrush = new SolidBrush(accent);
            graphics.FillPath(fillBrush, Rounded(new RectangleF(pad, barTop, contentWidth * ratio, barHeight), barHeight / 2));
        }

        using (var footFont = new Font("Segoe UI", 8.5f, FontStyle.Regular, GraphicsUnit.Point))
        using (var footBrush = new SolidBrush(secondary))
        {
            graphics.DrawString(updated, footFont, footBrush, pad, Dpi(128));
        }

        // 刷新按钮
        _buttonRect = new RectangleF(Width - pad - Dpi(84), Height - pad - Dpi(32), Dpi(84), Dpi(32));
        using (var buttonPath = Rounded(_buttonRect, Dpi(9)))
        using (var buttonBrush = new SolidBrush(Color.FromArgb(255, 10, 132, 255)))
        {
            graphics.FillPath(buttonBrush, buttonPath);
        }

        using (var buttonFont = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point))
        using (var buttonTextBrush = new SolidBrush(Color.White))
        using (var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
        {
            graphics.DrawString("刷新", buttonFont, buttonTextBrush, _buttonRect, format);
        }

        using (var hintFont = new Font("Segoe UI", 8f, FontStyle.Regular, GraphicsUnit.Point))
        using (var hintBrush = new SolidBrush(Color.FromArgb(120, secondary)))
        {
            graphics.DrawString("右键托盘图标可设置刷新间隔", hintFont, hintBrush, pad, Height - pad - Dpi(20));
        }
    }

    private static GraphicsPath Rounded(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        float diameter = Math.Min(radius * 2f, Math.Min(rect.Width, rect.Height));

        if (diameter <= 1f)
        {
            path.AddRectangle(rect);
            return path;
        }

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
