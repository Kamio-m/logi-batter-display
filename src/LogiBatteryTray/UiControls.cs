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

/// <summary>圆角按钮：胶囊形，带悬停/按下反馈。</summary>
internal sealed class RoundedButton : Control
{
    private bool _hover;
    private bool _pressed;

    public RoundedButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
        Font = new Font("Segoe UI", 9f);
        Size = new Size(100, 34);
    }

    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Color Surface { get; set; } = Color.FromArgb(48, 48, 54);

    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Color HoverSurface { get; set; } = Color.FromArgb(56, 56, 63);

    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Color TextColor { get; set; } = Color.White;

    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public float RadiusRatio { get; set; } = 0.5f;

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _pressed = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var color = _pressed ? ControlPaint.Dark(_hover ? HoverSurface : Surface, 0.05f)
                  : _hover ? HoverSurface
                  : Surface;

        var rect = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
        float radius = Math.Max(2f, Height * (float)RadiusRatio);

        using var path = RoundedPath(rect, radius);
        using (var brush = new SolidBrush(color))
            g.FillPath(brush, path);

        TextRenderer.DrawText(g, Text, Font, new Rectangle(0, 0, Width, Height), TextColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
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
/// <summary>圆角下拉列表：自绘按钮 + 深色圆角弹出列表。</summary>
internal sealed class RoundedDropDown : Control
{
    private readonly List<string> _items = new();
    private int _selectedIndex = -1;
    private bool _hover;
    private bool _suppress;

    public RoundedDropDown()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
        Font = new Font("Segoe UI", 9f);
        Size = new Size(168, 30);
    }

    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Color Surface { get; set; } = Color.FromArgb(48, 48, 54);

    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Color HoverSurface { get; set; } = Color.FromArgb(56, 56, 63);

    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Color TextColor { get; set; } = Color.White;

    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Color PopupBackground { get; set; } = Color.FromArgb(40, 40, 45);

    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Color AccentColor { get; set; } = Color.FromArgb(58, 130, 255);

    public IReadOnlyList<string> Items => _items;

    public void SetItems(IEnumerable<string> items)
    {
        _items.Clear();
        _items.AddRange(items);
    }

    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            int clamped = _items.Count == 0 ? -1 : Math.Clamp(value, -1, _items.Count - 1);
            if (clamped == _selectedIndex) return;

            _selectedIndex = clamped;
            Invalidate();

            if (!_suppress) SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler SelectedIndexChanged;

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left) ShowPopup();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var rect = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
        using (var path = RoundedPath(rect, Height * 0.35f))
        using (var brush = new SolidBrush(_hover ? HoverSurface : Surface))
            g.FillPath(brush, path);

        string text = _selectedIndex >= 0 && _selectedIndex < _items.Count ? _items[_selectedIndex] : "";
        TextRenderer.DrawText(g, text, Font, new Rectangle(12, 0, Width - 34, Height), TextColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

        // 小箭头
        var ax = Width - 20f;
        var ay = Height / 2f - 2f;
        using var pen = new Pen(TextColor, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLines(pen, new[]
        {
            new PointF(ax - 4, ay), new PointF(ax, ay + 4), new PointF(ax + 4, ay),
        });
    }

    private void ShowPopup()
    {
        if (_items.Count == 0) return;

        var popup = new Form
        {
            FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.Manual,
            ShowInTaskbar = false,
            TopMost = true,
            BackColor = PopupBackground,
        };

        int rowHeight = (int)(Math.Max(22, Height - 6));
        int height = _items.Count * rowHeight + 8;

        var list = new ListBox
        {
            BorderStyle = BorderStyle.None,
            IntegralHeight = false,
            DrawMode = DrawMode.OwnerDrawFixed,
            ItemHeight = rowHeight,
            BackColor = PopupBackground,
            ForeColor = TextColor,
            Font = Font,
            Location = new Point(4, 4),
            Size = new Size(Width - 8, height - 8),
        };

        foreach (var item in _items) list.Items.Add(item);
        if (_selectedIndex >= 0) list.SelectedIndex = _selectedIndex;

        list.DrawItem += (_, e) =>
        {
            bool hot = (e.State & DrawItemState.Selected) != 0;
            using (var brush = new SolidBrush(hot ? AccentColor : PopupBackground))
                e.Graphics.FillRectangle(brush, e.Bounds);

            string item = e.Index >= 0 && e.Index < list.Items.Count ? list.Items[e.Index]?.ToString() : "";
            TextRenderer.DrawText(e.Graphics, item, Font, new Rectangle(e.Bounds.X + 8, e.Bounds.Y, e.Bounds.Width - 8, e.Bounds.Height),
                hot ? Color.White : TextColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
        };

        void Choose()
        {
            if (list.SelectedIndex >= 0)
            {
                _suppress = false;
                SelectedIndex = list.SelectedIndex;
            }
            popup.Close();
        }

        list.MouseUp += (_, _) => Choose();
        list.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) Choose(); };

        popup.Controls.Add(list);
        popup.Deactivate += (_, _) => popup.Close();

        var origin = PointToScreen(new Point(0, Height + 4));
        var screen = Screen.FromControl(this).WorkingArea;
        if (origin.Y + height > screen.Bottom) origin = PointToScreen(new Point(0, -height - 4));

        popup.Size = new Size(Width, height);
        popup.Location = origin;
        popup.Region = new Region(RoundedPath(new RectangleF(0, 0, popup.Width, popup.Height), 10));

        popup.Show(this);
        list.Focus();
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
/// <summary>深色自绘数字输入框（可输入、可滚轮、可点上下箭头）。</summary>
internal sealed class NumberField : Control
{
    private int _value;
    private int _min;
    private int _max = 100;
    private string _buffer = "";

    public NumberField()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor |
                 ControlStyles.Selectable, true);
        BackColor = Color.Transparent;
        Font = new Font("Segoe UI", 10f);
        Size = new Size(68, 30);
        Cursor = Cursors.IBeam;
        TabStop = true;
    }

    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Color Surface { get; set; } = Color.FromArgb(26, 26, 29);

    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Color TextColor { get; set; } = Color.White;

    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Color AccentColor { get; set; } = Color.FromArgb(58, 130, 255);

    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int Minimum { get => _min; set { _min = value; Value = _value; } }

    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int Maximum { get => _max; set { _max = value; Value = _value; } }

    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int Value
    {
        get => _value;
        set
        {
            int clamped = Math.Clamp(value, _min, Math.Max(_min, _max));
            if (clamped == _value) { _buffer = ""; Invalidate(); return; }
            _value = clamped;
            _buffer = "";
            Invalidate();
            ValueChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler ValueChanged;

    private Rectangle UpRect => new(Width - 22, 2, 20, Height / 2 - 2);
    private Rectangle DownRect => new(Width - 22, Height / 2, 20, Height / 2 - 2);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var rect = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
        using (var path = Rounded(rect, 8))
        using (var brush = new SolidBrush(Surface))
            g.FillPath(brush, path);

        string text = _buffer.Length > 0 ? _buffer : _value.ToString();
        TextRenderer.DrawText(g, text, Font, new Rectangle(8, 0, Width - 30, Height), TextColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter);

        using var pen = new Pen(Color.FromArgb(150, TextColor), 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        float cx = Width - 12;

        g.DrawLines(pen, new[] { new PointF(cx - 4, Height / 2 - 6), new PointF(cx, Height / 2 - 10), new PointF(cx + 4, Height / 2 - 6) });
        g.DrawLines(pen, new[] { new PointF(cx - 4, Height / 2 + 6), new PointF(cx, Height / 2 + 10), new PointF(cx + 4, Height / 2 + 6) });

        if (Focused)
        {
            using var focus = new Pen(AccentColor, 1.4f);
            using var path = Rounded(rect, 8);
            g.DrawPath(focus, path);
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();

        if (UpRect.Contains(e.Location)) { Value += 1; return; }
        if (DownRect.Contains(e.Location)) { Value -= 1; return; }

        // 点文字区域：清空缓冲，方便直接输入替换
        _buffer = string.Empty;
        Invalidate();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        Value += e.Delta > 0 ? 1 : -1;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (e.KeyCode == Keys.Up) { Value += 1; e.Handled = true; return; }
        if (e.KeyCode == Keys.Down) { Value -= 1; e.Handled = true; return; }

        if (e.KeyCode == Keys.Back)
        {
            if (_buffer.Length > 0) _buffer = _buffer[..^1];
            Invalidate();
            e.Handled = true;
            return;
        }

        if (e.KeyCode == Keys.Enter) { CommitBuffer(); e.Handled = true; return; }
    }

    protected override void OnKeyPress(KeyPressEventArgs e)
    {
        base.OnKeyPress(e);

        if (!char.IsDigit(e.KeyChar)) return;

        if (_buffer.Length >= 6) { e.Handled = true; return; }

        _buffer += e.KeyChar;
        Invalidate();
        e.Handled = true;
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        CommitBuffer();
        Invalidate();
    }

    private void CommitBuffer()
    {
        if (_buffer.Length > 0 && int.TryParse(_buffer, out int parsed))
            Value = parsed;

        _buffer = "";
        Invalidate();
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
/// <summary>圆角深色文本框：内部包一个无边框 TextBox，保留输入法与光标。</summary>
internal sealed class RoundedTextField : Control
{
    private readonly TextBox _box;

    public RoundedTextField()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;

        _box = new TextBox
        {
            BorderStyle = BorderStyle.None,
            BackColor = Surface,
            ForeColor = Color.White,
            Font = new Font("Segoe UI", 10f),
        };
        _box.TextChanged += (_, _) => { };
        _box.GotFocus += (_, _) => Invalidate();
        _box.LostFocus += (_, _) => Invalidate();
        Controls.Add(_box);
    }

    public TextBox InnerBox => _box;

    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Color Surface { get; set; } = Color.FromArgb(26, 26, 29);

    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Color Outline { get; set; } = Color.FromArgb(56, 56, 62);

    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Color AccentColor { get; set; } = Color.FromArgb(58, 130, 255);

    public override string Text
    {
        get => _box.Text;
        set => _box.Text = value;
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (_box == null) return;

        _box.Location = new Point(12, (Height - _box.Height) / 2);
        _box.Width = Math.Max(20, Width - 24);
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // 不画矩形底色，避免圆角外出现黑边
        if (Parent != null)
        {
            using var brush = new SolidBrush(Parent.BackColor);
            e.Graphics.FillRectangle(brush, ClientRectangle);
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        var rect = new RectangleF(0.5f, 0.5f, Width - 1f, Height - 1f);
        using var path = Rounded(rect, 8);

        using (var brush = new SolidBrush(Surface))
            g.FillPath(brush, path);

        using (var pen = new Pen(_box.Focused ? AccentColor : Outline, _box.Focused ? 1.6f : 1f))
            g.DrawPath(pen, path);
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
