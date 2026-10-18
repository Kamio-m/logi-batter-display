using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace LogiBatteryTray;

/// <summary>圆角菜单渲染器：圆角背景 + 圆角高亮 + 自定义配色。</summary>
internal sealed class RoundedMenuRenderer : ToolStripProfessionalRenderer
{
    public int Radius { get; set; } = 12;
    public Color Background { get; set; } = Color.FromArgb(32, 32, 34);
    public Color Border { get; set; } = Color.FromArgb(70, 70, 74);
    public Color Foreground { get; set; } = Color.White;
    public Color Muted { get; set; } = Color.FromArgb(160, 160, 165);
    public Color Highlight { get; set; } = Color.FromArgb(52, 52, 58);

    public RoundedMenuRenderer() : base(new PlainColorTable())
    {
        RoundedEdges = false;
    }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = MenuStyle.RoundedPath(e.AffectedBounds, Radius);
        using var brush = new SolidBrush(Background);
        e.Graphics.FillPath(brush, path);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new RectangleF(0.5f, 0.5f, e.ToolStrip.Width - 1.5f, e.ToolStrip.Height - 1.5f);
        using var path = MenuStyle.RoundedPath(bounds, Radius);
        using var pen = new Pen(Border, 1f);
        e.Graphics.DrawPath(pen, path);
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected || !e.Item.Enabled) return;

        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new RectangleF(3, 1, e.Item.Width - 6, e.Item.Height - 2);
        using var path = MenuStyle.RoundedPath(bounds, Math.Max(4, Radius - 6));
        using var brush = new SolidBrush(Highlight);
        e.Graphics.FillPath(brush, path);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? Foreground : Muted;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var r = e.ImageRectangle;
        float left = r.X + r.Width * 0.18f;
        float midY = r.Y + r.Height * 0.52f;

        using var pen = new Pen(Foreground, 1.8f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };
        g.DrawLines(pen, new[]
        {
            new PointF(left, midY),
            new PointF(left + r.Width * 0.16f, midY + r.Height * 0.18f),
            new PointF(left + r.Width * 0.52f, midY - r.Height * 0.22f),
        });
    }
    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        var y = e.Item.Height / 2;
        using var pen = new Pen(Color.FromArgb(70, Border), 1f);
        e.Graphics.DrawLine(pen, 10, y, e.Item.Width - 10, y);
    }

    private sealed class PlainColorTable : ProfessionalColorTable
    {
        public override Color MenuItemSelected => Color.Transparent;
        public override Color MenuItemBorder => Color.Transparent;
        public override Color ToolStripDropDownBackground => Color.Transparent;
        public override Color ImageMarginGradientBegin => Color.Transparent;
        public override Color ImageMarginGradientMiddle => Color.Transparent;
        public override Color ImageMarginGradientEnd => Color.Transparent;
    }
}

/// <summary>菜单外观设置：圆角、透明、配色。</summary>
internal static class MenuStyle
{
    private const int GwlExStyle = -20;
    private const int WsExLayered = 0x00080000;
    private const uint LwaAlpha = 0x00000002;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int index);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int index, int value);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetLayeredWindowAttributes(IntPtr hWnd, uint colorKey, byte alpha, uint flags);

    public static GraphicsPath RoundedPath(RectangleF rect, float radius)
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

    /// <summary>同时套用圆角裁剪与整体透明度（菜单和所有子菜单通用）。</summary>
    public static void ApplyMenuWindow(ToolStripDropDown dropdown, int opacityPercent, int radius)
    {
        ApplyOpacity(dropdown, opacityPercent);
        ApplyRegion(dropdown, radius);
    }
    /// <summary>给菜单套上圆角裁剪。</summary>
    public static void ApplyRegion(ToolStripDropDown menu, int radius)
    {
        if (menu.Width <= 0 || menu.Height <= 0) return;

        using var path = RoundedPath(new RectangleF(0, 0, menu.Width, menu.Height), radius);
        var old = menu.Region;
        menu.Region = new Region(path);
        old?.Dispose();
    }

    /// <summary>给菜单窗口设置整体透明度（0-100）。</summary>
    public static void ApplyOpacity(ToolStripDropDown menu, int percent)
    {
        if (!menu.IsHandleCreated) return;

        int alpha = (int)Math.Clamp(percent, 40, 100) * 255 / 100;
        int style = GetWindowLong(menu.Handle, GwlExStyle);
        SetWindowLong(menu.Handle, GwlExStyle, style | WsExLayered);
        SetLayeredWindowAttributes(menu.Handle, 0, (byte)alpha, LwaAlpha);
    }

    /// <summary>按主题生成渲染器。</summary>
    public static RoundedMenuRenderer CreateRenderer(bool light, int radius)
    {
        return new RoundedMenuRenderer
        {
            Radius = radius,
            Background = light ? Color.FromArgb(250, 250, 252) : Color.FromArgb(30, 30, 32),
            Border = light ? Color.FromArgb(214, 214, 220) : Color.FromArgb(70, 70, 76),
            Foreground = light ? Color.FromArgb(28, 28, 32) : Color.White,
            Muted = light ? Color.FromArgb(130, 130, 136) : Color.FromArgb(150, 150, 156),
            Highlight = light ? Color.FromArgb(232, 232, 238) : Color.FromArgb(52, 52, 58),
        };
    }
}
