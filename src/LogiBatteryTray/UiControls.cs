using System.Drawing.Drawing2D;

namespace LogiBatteryTray;

/// <summary>开关控件（替代勾选框），带圆角轨道和滑块。</summary>
internal sealed class ToggleSwitch : Control
{
    private readonly Color _onColor;
    private readonly Color _offColor;
    private bool _checked;

    public ToggleSwitch(Color onColor, Color offColor)
    {
        _onColor = onColor;
        _offColor = offColor;

        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
        Cursor = Cursors.Hand;
        BackColor = Color.Transparent;
        Size = new Size(46, 26);
    }

    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool Checked
    {
        get => _checked;
        set
        {
            if (_checked == value) return;
            _checked = value;
            Invalidate();
            CheckedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler CheckedChanged;

    protected override void OnClick(EventArgs e)
    {
        Checked = !Checked;
        base.OnClick(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var track = new RectangleF(1, (Height - 26f) / 2f + 0.5f, Width - 2, 26);
        using (var path = Rounded(track, track.Height / 2f))
        using (var brush = new SolidBrush(_checked ? _onColor : _offColor))
        {
            g.FillPath(brush, path);
        }

        float knob = track.Height - 6;
        float knobX = _checked ? track.Right - knob - 3 : track.Left + 3;
        using (var knobBrush = new SolidBrush(Color.White))
        using (var shadow = new SolidBrush(Color.FromArgb(40, 0, 0, 0)))
        {
            g.FillEllipse(shadow, knobX, track.Top + 4, knob, knob);
            g.FillEllipse(knobBrush, knobX, track.Top + 3, knob, knob);
        }
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
}

/// <summary>右侧的“当前值”按钮：左边文字，右边一个箭头。</summary>
internal sealed class ValueButton : Control
{
    private readonly Color _fore;
    private readonly Color _back;
    private readonly Color _border;
    private readonly Color _hoverBack;
    private bool _hover;

    public ValueButton(Color fore, Color back, Color border, Color hoverBack)
    {
        _fore = fore;
        _back = back;
        _border = border;
        _hoverBack = hoverBack;

        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
        Cursor = Cursors.Hand;
        BackColor = Color.Transparent;
        Size = new Size(168, 34);
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var bounds = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
        using (var path = Rounded(bounds, 8))
        {
            using (var fill = new SolidBrush(_hover ? _hoverBack : _back))
                g.FillPath(fill, path);

            using (var pen = new Pen(_border, 1f))
                g.DrawPath(pen, path);
        }

        using (var font = new Font("Segoe UI", 9f))
        using (var brush = new SolidBrush(_fore))
        using (var format = new StringFormat { LineAlignment = StringAlignment.Center })
        {
            g.DrawString(Text, font, brush, new RectangleF(12, 0, Width - 34, Height), format);
        }

        using (var chevron = new Pen(Color.FromArgb(150, _fore), 1.4f))
        {
            float cx = Width - 20;
            float cy = Height / 2f;
            g.DrawLines(chevron, new[]
            {
                new PointF(cx - 2, cy - 4),
                new PointF(cx + 2, cy),
                new PointF(cx - 2, cy + 4),
            });
        }
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
}
