using System.Drawing.Drawing2D;

namespace LogiBatteryTray;

/// <summary>色环 + 饱和度/明度方块取色器（仿 Windows 颜色选择器）。</summary>
internal sealed class ColorPickerForm : Form
{
    private readonly float _scale;
    private readonly Color _fieldBg;
    private readonly Color _buttonBg;
    private readonly Color _buttonHover;

    private readonly HueRing _ring;
    private readonly SvSquare _square;
    private readonly Panel _preview;

    private double _hue;
    private double _sat = 1.0;
    private double _val = 1.0;

    private readonly List<Color> _custom = new();
    private readonly List<RoundedButton> _customSlots = new();

    /// <summary>把当前颜色写入自定义颜色区（最多 8 个）。</summary>
    private void AddToCustom()
    {
        if (!_custom.Contains(SelectedColor))
        {
            _custom.Insert(0, SelectedColor);
            if (_custom.Count > _customSlots.Count) _custom.RemoveAt(_custom.Count - 1);
        }

        RefreshCustomSlots();
    }

    private void RefreshCustomSlots()
    {
        for (int i = 0; i < _customSlots.Count; i++)
        {
            _customSlots[i].Surface = i < _custom.Count ? _custom[i] : _fieldBg;
            _customSlots[i].Invalidate();
        }
    }

    public Color SelectedColor { get; private set; }

    /// <summary>当前自定义颜色列表（供外部保存）。</summary>
    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public List<Color> CustomColors => _custom.ToList();

    public ColorPickerForm(Color initial, Color surface, Color text, Color accent, Color border, float scale, IEnumerable<Color> customColors = null)
    {
        _scale = scale <= 0 ? 1f : scale;
        SelectedColor = initial;

        bool lightTheme = text.R < 128;                    // 文字深 → 浅色主题
        _fieldBg = lightTheme ? Color.FromArgb(232, 232, 236) : Color.FromArgb(32, 32, 38);
        _buttonBg = lightTheme ? Color.FromArgb(222, 222, 227) : Color.FromArgb(50, 50, 56);
        _buttonHover = lightTheme ? Color.FromArgb(206, 206, 212) : Color.FromArgb(70, 70, 78);

        if (customColors != null)
        {
            foreach (var color in customColors.Take(8)) _custom.Add(color);
        }

        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = S(420, 600);
        BackColor = surface;
        ForeColor = text;
        ShowInTaskbar = false;
        AutoScaleMode = AutoScaleMode.None;
        Font = new Font("Segoe UI", 9f);

        Region = new Region(RoundedPath(new RectangleF(0, 0, Width, Height), S(14)));

        // 初始 HSV
        (_hue, _sat, _val) = ToHsv(initial);

        var title = MakeLabel("色彩", 20, 14, 10f, true, text);

        _ring = new HueRing
        {
            Location = P(20, 44),
            Size = S(250, 250),
            Border = border,
        };

        _square = new SvSquare
        {
            Location = P(20 + 46, 44 + 46),
            Size = S(158, 158),
        };

        _ring.HueChanged += (_, _) =>
        {
            _hue = _ring.Hue;
            _square.Hue = _hue;
            RefreshColor();
        };

        _square.SvChanged += (_, _) =>
        {
            _sat = _square.Saturation;
            _val = _square.Value;
            RefreshColor();
        };

        _ring.Hue = _hue;
        _square.Hue = _hue;
        _square.Saturation = _sat;
        _square.Value = _val;

        _preview = new Panel
        {
            Location = P(292, 60),
            Size = S(84, 30),
            BackColor = initial,
            BorderStyle = BorderStyle.FixedSingle,
        };

        var plus = new RoundedButton
        {
            Text = "＋",
            Size = S(32, 30),
            Location = P(382, 60),
            Surface = _buttonBg,
            HoverSurface = _buttonHover,
            TextColor = text,
        };

        var redLabel = MakeLabel("红", 288, 114, 9f, false, text);
        var greenLabel = MakeLabel("绿", 288, 154, 9f, false, text);
        var blueLabel = MakeLabel("蓝", 288, 194, 9f, false, text);

        var redBox = MakeNumber(318, 110, initial.R);
        var greenBox = MakeNumber(318, 150, initial.G);
        var blueBox = MakeNumber(318, 190, initial.B);

        void FromRgb()
        {
            var color = Color.FromArgb(redBox.Value, greenBox.Value, blueBox.Value);
            (_hue, _sat, _val) = ToHsv(color);
            _ring.Hue = _hue;
            _square.Hue = _hue;
            _square.Saturation = _sat;
            _square.Value = _val;
            SelectedColor = color;
            _preview.BackColor = color;
        }

        redBox.ValueChanged += (_, _) => FromRgb();
        greenBox.ValueChanged += (_, _) => FromRgb();
        blueBox.ValueChanged += (_, _) => FromRgb();

        var preset = new RoundedButton
        {
            Text = "预设颜色",
            Size = S(120, 28),
            Location = P(288, 232),
            Surface = _buttonBg,
            HoverSurface = _buttonHover,
            TextColor = text,
        };
        preset.Click += (_, _) => FromRgb();

        plus.Click += (_, _) => AddToCustom();

        Controls.AddRange(new Control[] { title, _ring, _square, _preview, plus, redLabel, greenLabel, blueLabel, redBox, greenBox, blueBox, preset });

        // 标准颜色
        Controls.Add(MakeLabel("标准颜色", 24, 330, 9.5f, false, text));

        string[] standard =
        {
            "#FF0000", "#FF7A00", "#FFD400", "#00C800",
            "#00C8C8", "#0033FF", "#C800FF", "#000000",
        };

        for (int i = 0; i < standard.Length; i++)
        {
            var color = ColorTranslator.FromHtml(standard[i]);
            var swatch = new RoundedButton
            {
                Text = "",
                Size = S(34, 34),
                Location = P(24 + i * 44, 360),
                Surface = color,
                HoverSurface = ControlPaint.Light(color, 0.15f),
                TextColor = color,
            };

            swatch.Click += (_, _) =>
            {
                SetFromColor(color, redBox, greenBox, blueBox);
            };

            Controls.Add(swatch);
        }

        // 自定义颜色
        Controls.Add(MakeLabel("自定义颜色", 24, 424, 9.5f, false, text));

        for (int i = 0; i < 8; i++)
        {
            int slot = i;
            var box = new RoundedButton
            {
                Text = "",
                Size = S(34, 34),
                Location = P(24 + i * 44, 454),
                Surface = _fieldBg,
                HoverSurface = _buttonHover,
                TextColor = text,
            };

            box.Click += (_, _) =>
            {
                if (slot < _custom.Count) SetFromColor(_custom[slot], redBox, greenBox, blueBox);
            };

            // 右键删除该自定义颜色
            box.MouseUp += (_, e) =>
            {
                if (e.Button != MouseButtons.Right) return;
                if (slot >= _custom.Count) return;

                _custom.RemoveAt(slot);
                RefreshCustomSlots();
            };

            _customSlots.Add(box);
            Controls.Add(box);
        }

        var ok = new RoundedButton
        {
            Text = "确定",
            Size = S(180, 36),
            Location = P(24, 534),
            Surface = accent,
            HoverSurface = ControlPaint.Light(accent, 0.08f),
            TextColor = Color.White,
        };
        ok.Click += (_, _) =>
        {
            AddToCustom();
            DialogResult = DialogResult.OK;
            Close();
        };

        var cancel = new RoundedButton
        {
            Text = "取消",
            Size = S(180, 36),
            Location = P(216, 534),
            Surface = _buttonBg,
            HoverSurface = _buttonHover,
            TextColor = text,
        };
        cancel.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };

        Controls.AddRange(new Control[] { ok, cancel });
        RefreshCustomSlots();
    }

    private void SetFromColor(Color color, NumberField red, NumberField green, NumberField blue)
    {
        (_hue, _sat, _val) = ToHsv(color);
        _ring.Hue = _hue;
        _square.Hue = _hue;
        _square.Saturation = _sat;
        _square.Value = _val;

        red.Value = color.R;
        green.Value = color.G;
        blue.Value = color.B;

        SelectedColor = color;
        _preview.BackColor = color;
    }

    private void RefreshColor()
    {
        var color = FromHsv(_hue, _sat, _val);
        SelectedColor = color;
        _preview.BackColor = color;
        _square.Invalidate();
        _ring.Invalidate();
    }

    private int S(float value) => (int)Math.Round(value * _scale);
    private Size S(int w, int h) => new(S(w), S(h));
    private Point P(float x, float y) => new((int)(x * _scale), (int)(y * _scale));

    private Label MakeLabel(string text, float x, float y, float size, bool bold, Color color) => new()
    {
        Text = text,
        AutoSize = true,
        Location = P(x, y),
        ForeColor = color,
        BackColor = Color.Transparent,
        Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular),
    };

    private NumberField MakeNumber(float x, float y, int value) => new()
    {
        Location = P(x, y),
        Size = S(88, 30),
        Minimum = 0,
        Maximum = 255,
        Value = value,
        Surface = _fieldBg,
        TextColor = ForeColor,
        AccentColor = Color.FromArgb(58, 130, 255),
    };

    private static GraphicsPath RoundedPath(RectangleF rect, float radius)
    {
        var path = new GraphicsPath();
        float diameter = Math.Min(radius * 2f, Math.Min(rect.Width, rect.Height));
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

    private static (double H, double S, double V) ToHsv(Color color)
    {
        double r = color.R / 255.0, g = color.G / 255.0, b = color.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b));
        double min = Math.Min(r, Math.Min(g, b));
        double delta = max - min;

        double hue = 0;
        if (delta > 0)
        {
            hue = max == r ? ((g - b) / delta) % 6
                : max == g ? ((b - r) / delta) + 2
                : ((r - g) / delta) + 4;
            hue *= 60;
            if (hue < 0) hue += 360;
        }

        return (hue, max <= 0 ? 0 : delta / max, max);
    }

    private static Color FromHsv(double h, double s, double v)
    {
        h = ((h % 360) + 360) % 360;
        double c = v * s;
        double x = c * (1 - Math.Abs((h / 60) % 2 - 1));
        double m = v - c;

        (double r, double g, double b) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };

        return Color.FromArgb(
            (int)Math.Round((r + m) * 255),
            (int)Math.Round((g + m) * 255),
            (int)Math.Round((b + m) * 255));
    }
}

/// <summary>色相环（外环），点击/拖动选色相。</summary>
internal sealed class HueRing : Control
{
    private Bitmap _cache;
    private int _cacheSize;
    private double _hue;

    public HueRing()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Cursor = Cursors.Hand;
    }

    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Color Border { get; set; } = Color.Gray;
    public event EventHandler HueChanged;

    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public double Hue
    {
        get => _hue;
        set { _hue = value; Invalidate(); }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        int size = Math.Min(Width, Height);
        if (_cache == null || _cacheSize != size)
        {
            _cache?.Dispose();
            _cache = Build(size);
            _cacheSize = size;
        }

        g.DrawImage(_cache, (Width - size) / 2, (Height - size) / 2, size, size);

        // 色相标记
        double radius = size / 2.0 - size * 0.09;
        double angle = _hue * Math.PI / 180.0;
        float cx = Size.Width / 2f + (float)(Math.Cos(angle) * radius);
        float cy = Size.Height / 2f + (float)(Math.Sin(angle) * radius);

        using var pen = new Pen(Color.White, 2f);
        g.DrawEllipse(pen, cx - 7, cy - 7, 14, 14);
        using var dark = new Pen(Color.FromArgb(120, 0, 0, 0), 1f);
        g.DrawEllipse(dark, cx - 8, cy - 8, 16, 16);
    }

    private static Bitmap Build(int size)
    {
        var bmp = new Bitmap(size, size);
        int half = size / 2;
        double outer = half - 1;
        double inner = outer * 0.82;

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                double dx = x - half, dy = y - half;
                double dist = Math.Sqrt(dx * dx + dy * dy);

                if (dist > outer || dist < inner)
                {
                    bmp.SetPixel(x, y, Color.Transparent);
                    continue;
                }

                double hue = Math.Atan2(dy, dx) * 180 / Math.PI;
                bmp.SetPixel(x, y, HsvToRgb(hue, 1.0, 1.0));
            }
        }

        return bmp;
    }

    private void Pick(Point point)
    {
        double dx = point.X - Width / 2.0;
        double dy = point.Y - Height / 2.0;
        double hue = Math.Atan2(dy, dx) * 180 / Math.PI;
        Hue = hue;
        HueChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); Pick(e.Location); }
    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); if (e.Button == MouseButtons.Left) Pick(e.Location); }

    internal static Color HsvToRgbPublic(double h, double s, double v) => HsvToRgb(h, s, v);

    private static Color HsvToRgb(double h, double s, double v)
    {
        h = ((h % 360) + 360) % 360;
        double c = v * s;
        double x = c * (1 - Math.Abs((h / 60) % 2 - 1));
        double m = v - c;

        (double r, double g, double b) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };

        return Color.FromArgb((int)((r + m) * 255), (int)((g + m) * 255), (int)((b + m) * 255));
    }
}

/// <summary>饱和度/明度方块。</summary>
internal sealed class SvSquare : Control
{
    private Bitmap _cache;
    private double _hue;
    private double _saturation = 1;
    private double _value = 1;

    public SvSquare()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor, true);
        BackColor = Color.Transparent;
        Cursor = Cursors.Cross;
    }

    public event EventHandler SvChanged;

    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public double Hue
    {
        get => _hue;
        set { _hue = value; _cache = null; Invalidate(); }
    }

    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public double Saturation
    {
        get => _saturation;
        set { _saturation = Math.Clamp(value, 0, 1); Invalidate(); }
    }

    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public double Value
    {
        get => _value;
        set { _value = Math.Clamp(value, 0, 1); Invalidate(); }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        if (_cache == null || _cache.Width != Math.Max(1, Width) || _cache.Height != Math.Max(1, Height))
        {
            _cache?.Dispose();
            _cache = Build(Math.Max(1, Width), Math.Max(1, Height));
        }

        g.DrawImage(_cache, 0, 0, Width, Height);

        using var border = new Pen(Color.FromArgb(70, 70, 78), 1f);
        g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);

        float cx = (float)(_saturation * (Width - 1));
        float cy = (float)((1 - _value) * (Height - 1));

        using var pen = new Pen(Color.White, 2f);
        g.DrawEllipse(pen, cx - 6, cy - 6, 12, 12);
        using var dark = new Pen(Color.FromArgb(130, 0, 0, 0), 1f);
        g.DrawEllipse(dark, cx - 7, cy - 7, 14, 14);
    }

    private Bitmap Build(int width, int height)
    {
        var bmp = new Bitmap(width, height);
        for (int y = 0; y < height; y++)
        {
            double v = 1.0 - (double)y / Math.Max(1, height - 1);
            for (int x = 0; x < width; x++)
            {
                double s = (double)x / Math.Max(1, width - 1);
                bmp.SetPixel(x, y, HueRing.HsvToRgbPublic(_hue, s, v));
            }
        }

        return bmp;
    }

    private void Pick(Point point)
    {
        Saturation = (double)point.X / Math.Max(1, Width - 1);
        Value = 1.0 - (double)point.Y / Math.Max(1, Height - 1);
        SvChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); Pick(e.Location); }
    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); if (e.Button == MouseButtons.Left) Pick(e.Location); }
}
