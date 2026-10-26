using System.Drawing.Drawing2D;

namespace LogiBatteryTray;

/// <summary>开关控件（替代勾选框），带圆角轨道和滑块。</summary>
internal sealed class ToggleSwitch : Control
{
    private Color _onColor;
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

    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Color OnColor
    {
        get => _onColor;
        set { _onColor = value; Invalidate(); }
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
        Font = new Font("Segoe UI", 9f);
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

        TextRenderer.DrawText(g, Text, Font, new Rectangle(12, 0, Width - 34, Height), _fore,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

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

/// <summary>圆角卡片：自带双缓冲和缩放重绘，避免拖动窗口时留下残影。</summary>
internal class CardPanel : Panel
{
    public CardPanel()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.UserPaint, true);
        BackColor = Color.Transparent;
    }

    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Color Surface { get; set; } = Color.White;
    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Color Outline { get; set; } = Color.Gray;
    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Color Background { get; set; } = Color.Black;
    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public float Radius { get; set; } = 14;

    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public float OutlineWidth { get; set; } = 1f;

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        using var brush = new SolidBrush(Background);
        e.Graphics.FillRectangle(brush, ClientRectangle);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        using var path = RoundedPath(new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f), Radius);
        using (var fill = new SolidBrush(Surface))
            g.FillPath(fill, path);

        using (var pen = new Pen(Outline, OutlineWidth))
            g.DrawPath(pen, path);
    }

    private static GraphicsPath RoundedPath(RectangleF rect, float radius)
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
/// <summary>可滚动页面：内容在 Content 里，右侧是自绘的细滚动条。</summary>
internal sealed class ScrollHost : Panel
{
    private readonly SlimScrollBar _bar;
    private int _offset;
    private bool _layingOut;

    public ScrollHost(Color background)
    {
        BackColor = background;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.AllPaintingInWmPaint, true);

        Content = new Panel { BackColor = background, Location = new Point(0, 0) };
        Content.SizeChanged += (_, _) =>
        {
            if (_layingOut) return;
            Invalidate();
            PerformLayout();
        };
        Controls.Add(Content);

        _bar = new SlimScrollBar();
        _bar.ValueChanged += (_, _) => SetOffset(_bar.Value, fromBar: true);
        Controls.Add(_bar);
    }

    public Panel Content { get; }

    /// <summary>内容最大宽度（宽屏下居中，避免卡片被拉太长）。</summary>
    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int MaxContentWidth { get; set; } = 1000;

    public void ScrollBy(int delta) => SetOffset(_offset + delta, fromBar: false);

    public int ScrollOffset => _offset;

    private void SetOffset(int value, bool fromBar)
    {
        int max = Math.Max(0, Content.Height - Height);
        value = Math.Clamp(value, 0, max);
        if (value == _offset && !fromBar) return;

        _offset = value;
        Content.Top = -value;

        if (!fromBar) _bar.Value = value;
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (_layingOut || Content == null || _bar == null) return;

        _layingOut = true;
        try
        {
            int barSpace = (int)(14 * DeviceDpi / 96f);
            int available = Math.Max(0, Width - barSpace);
            int maxWidth = Math.Max((int)(420 * DeviceDpi / 96f), (int)(MaxContentWidth * DeviceDpi / 96f));

            Content.Width = Math.Min(available, maxWidth);
            Content.Left = Math.Max(0, (available - Content.Width) / 2);

            int bottom = 0;
            foreach (Control child in Content.Controls)
                bottom = Math.Max(bottom, child.Bottom);

            Content.Height = Math.Max(Height, bottom + (int)(24 * DeviceDpi / 96f));

            bool needed = Content.Height > Height;
            _bar.Visible = needed;
            _bar.Bounds = new Rectangle(
                Width - (int)(11 * DeviceDpi / 96f),
                (int)(6 * DeviceDpi / 96f),
                (int)(10 * DeviceDpi / 96f),
                Height - (int)(12 * DeviceDpi / 96f));

            _bar.Maximum = Content.Height;
            _bar.Viewport = Height;
            SetOffset(_offset, fromBar: false);
        }
        finally
        {
            _layingOut = false;
        }
    }
}

/// <summary>细长的自绘滚动条。</summary>
internal sealed class SlimScrollBar : Control
{
    private int _maximum;
    private int _viewport;
    private int _value;
    private bool _dragging;
    private int _dragStartY;
    private int _dragStartValue;
    private bool _hover;

    public SlimScrollBar()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
    }

    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int Maximum
    {
        get => _maximum;
        set { _maximum = Math.Max(0, value); Invalidate(); }
    }

    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int Viewport
    {
        get => _viewport;
        set { _viewport = Math.Max(1, value); Invalidate(); }
    }

    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int Value
    {
        get => _value;
        set
        {
            int clamped = Math.Clamp(value, 0, Math.Max(0, _maximum - _viewport));
            if (clamped == _value) return;
            _value = clamped;
            Invalidate();
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler ValueChanged;

    private int ThumbHeight => Math.Max((int)(28 * DeviceDpi / 96f),
        (int)((float)Height * _viewport / Math.Max(1, _maximum)));

    private int ThumbTop => Math.Max(0, (int)((float)(Height - ThumbHeight) * _value / Math.Max(1, _maximum - _viewport)));

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;

        int top = ThumbTop;
        if (e.Y >= top && e.Y <= top + ThumbHeight)
        {
            _dragging = true;
            _dragStartY = e.Y;
            _dragStartValue = _value;
            Capture = true;
        }
        else
        {
            // 点击空白处：上/下翻一屏
            Value = e.Y < top ? _value - _viewport : _value + _viewport;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging) return;

        int range = Math.Max(1, _maximum - _viewport);
        int travel = Math.Max(1, Height - ThumbHeight);
        int delta = e.Y - _dragStartY;
        Value = _dragStartValue + (int)((float)delta / travel * range);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!_dragging) return;
        _dragging = false;
        Capture = false;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        // 轨道
        using (var track = new SolidBrush(Color.FromArgb(40, 140, 140, 150)))
        {
            using var trackPath = RoundedPath(new RectangleF(0, 0, Width, Height), Width / 2f);
            g.FillPath(track, trackPath);
        }

        int alpha = _hover || _dragging ? 220 : 150;
        using (var brush = new SolidBrush(Color.FromArgb(alpha, 190, 190, 200)))
        {
            var thumb = new RectangleF(0, ThumbTop, Width, ThumbHeight);
            using var path = RoundedPath(thumb, Width / 2f);
            g.FillPath(brush, path);
        }
    }

    private static GraphicsPath RoundedPath(RectangleF rect, float radius)
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
