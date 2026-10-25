using System.Drawing.Imaging;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace LogiBatteryTray;

public enum IconShape
{
    /// <summary>圆形进度环，中间显示百分比。</summary>
    Ring,
    /// <summary>系统电池图标。</summary>
    Battery,
}

public enum BatteryIconStyle
{
    /// <summary>白描边电池 + 电量内胆（低于 20% 内胆变黄，与系统一致）。</summary>
    OutlinePlug,
    /// <summary>同上，但充电时画闪电而不是插头。</summary>
    OutlineBolt,
    /// <summary>实心圆角电池 + 白色百分比数字。</summary>
    FilledCapsule,
}

/// <summary>
/// 托盘图标 / 小卡图标。
/// 电池外形与 Windows 一致：白色描边始终不变；内胆按电量着色，低于 20% 变黄。
/// </summary>
internal static class IconFactory
{
    public static readonly Color Green = Color.FromArgb(255, 48, 209, 88);
    public static readonly Color Amber = Color.FromArgb(255, 255, 196, 0);
    public static readonly Color Yellow = Color.FromArgb(255, 255, 214, 10);
    public static readonly Color Red = Color.FromArgb(255, 255, 69, 58);
    public static readonly Color ChargingColor = Color.FromArgb(255, 48, 209, 88);
    public static readonly Color UnknownColor = Color.FromArgb(255, 142, 142, 147);

    public static BatteryIconStyle Style { get; set; } = BatteryIconStyle.OutlinePlug;

    /// <summary>当前图标形状。</summary>
    public static IconShape Shape { get; set; } = IconShape.Ring;

    /// <summary>电量低于该值时内胆变黄。</summary>
    public const int LowBatteryThreshold = 20;

    public static Color LevelColor(BatteryReading battery)
    {
        if (battery?.Percent == null) return UnknownColor;
        if (battery.Charging || battery.Full) return ChargingColor;
        if (battery.Percent < LowBatteryThreshold) return Yellow;
        return Green;
    }

    public static Icon Create(int size, BatteryReading battery, bool showPercent, bool lightTaskbar, bool offline = false)
    {
        using var bitmap = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        graphics.Clear(Color.Transparent);

        var bounds = new RectangleF(0, 0, size, size);
        if (Shape == IconShape.Ring)
            DrawRing(graphics, bounds, battery, lightTaskbar, offline);
        else if (Style == BatteryIconStyle.FilledCapsule)
            DrawCapsule(graphics, bounds, battery, offline);
        else
            DrawFromSheet(graphics, bounds, battery, lightTaskbar, offline, showPercent);

        IntPtr handle = bitmap.GetHicon();
        try
        {
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private static readonly object SheetGate = new();
    private static Bitmap _sheet24;
    private static Bitmap _sheet32;

    private static Bitmap LoadSheet(string name)
    {
        using var stream = typeof(IconFactory).Assembly.GetManifestResourceStream("LogiBatteryTray.Assets." + name);
        return stream == null ? null : new Bitmap(stream);
    }

    private static Bitmap SheetFor(float size)
    {
        lock (SheetGate)
        {
            try
            {
                if (size >= 28) return _sheet32 ??= LoadSheet("battery32.png");
                return _sheet24 ??= LoadSheet("battery24.png");
            }
            catch
            {
                return null;
            }
        }
    }

    private static readonly Dictionary<Bitmap, Rectangle> ContentBounds = new();

    /// <summary>计算雪碧图里电池本体的包围盒（去掉空白），用于放大绘制。</summary>
    private static Rectangle SheetContentBounds(Bitmap sheet)
    {
        lock (SheetGate)
        {
            if (ContentBounds.TryGetValue(sheet, out var cached)) return cached;

            int pitch = sheet.Height;
            int minX = pitch, minY = pitch, maxX = -1, maxY = -1;
            for (int f = 0; f <= 9; f++)
            {
                for (int y = 0; y < pitch; y++)
                {
                    for (int x = 0; x < pitch; x++)
                    {
                        if (sheet.GetPixel(f * pitch + x, y).A <= 20) continue;
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
                }
            }

            var bounds = maxX < 0
                ? new Rectangle(0, 0, pitch, pitch)
                : new Rectangle(minX, minY, maxX - minX + 1, maxY - minY + 1);
            ContentBounds[sheet] = bounds;
            return bounds;
        }
    }
    /// <summary>
    /// 用 Windows 自带电池雪碧图绘制：0-9 帧为 0-100% 电量，10-19 帧为充电（带插头）。
    /// 低于 20% 时只把内胆染黄，描边保持白色。
    /// </summary>
    private static void DrawFromSheet(Graphics g, RectangleF rect, BatteryReading battery, bool lightTaskbar, bool offline, bool showPercent)
    {
        Bitmap sheet = SheetFor(rect.Width);
        if (sheet == null)
        {
            DrawIcon(g, BatteryRect(rect.Width), battery, lightTaskbar, offline, false);
            return;
        }

        int pitch = sheet.Height;
        int level = Math.Clamp((int)Math.Round((battery?.Percent ?? 0) / 10.0), 0, 9);
        bool charging = battery?.Charging == true || battery?.Full == true;
        int frame = level;

        // 裁掉帧里的空白后放大，电池占满可用区域
        Rectangle src = SheetContentBounds(sheet);
        var batteryRect = rect;

        float scale = Math.Min(batteryRect.Width / src.Width, batteryRect.Height / src.Height);
        float dw = src.Width * scale, dh = src.Height * scale;
        var dest = new RectangleF(
            batteryRect.X + (batteryRect.Width - dw) / 2f,
            batteryRect.Y + (batteryRect.Height - dh) / 2f,
            dw, dh);

        if (showPercent && battery?.Percent is int shownPercent)
        {
            // 百分比模式：电池内部不填充，只把数字画在中间
            using (var baseAttrs = BuildTint(lightTaskbar, offline))
                g.DrawImage(sheet, Rectangle.Round(dest), 0 * pitch + src.X, src.Y, src.Width, src.Height, GraphicsUnit.Pixel, baseAttrs);

            var innerRect = new RectangleF(
                dest.X + dest.Width * 0.12f,
                dest.Y + dest.Height * 0.26f,
                dest.Width * 0.76f,
                dest.Height * 0.48f);

            DrawPercentText(g, innerRect, shownPercent, lightTaskbar, offline);
            return;
        }

        using (var baseAttrs = BuildTint(lightTaskbar, offline))
            g.DrawImage(sheet, Rectangle.Round(dest), frame * pitch + src.X, src.Y, src.Width, src.Height, GraphicsUnit.Pixel, baseAttrs);

        var inner = new RectangleF(
            dest.X + dest.Width * 0.21f,
            dest.Y + dest.Height * 0.32f,
            dest.Width * 0.54f,
            dest.Height * 0.36f);

        if (charging && !offline)
        {
            // 充电：只把内胆染成绿色，描边保持白色
            var saved = g.Clip;
            g.SetClip(inner, CombineMode.Intersect);
            using var green = BuildTint(lightTaskbar, false, green: true);
            g.DrawImage(sheet, Rectangle.Round(dest), frame * pitch + src.X, src.Y, src.Width, src.Height, GraphicsUnit.Pixel, green);
            g.Clip = saved;
            return;
        }

        bool low = !offline && battery?.Percent is int p && p < LowBatteryThreshold;
        if (low)
        {
            var saved = g.Clip;
            g.SetClip(inner, CombineMode.Intersect);
            using var yellow = BuildTint(lightTaskbar, false, lowBattery: true);
            g.DrawImage(sheet, Rectangle.Round(dest), frame * pitch + src.X, src.Y, src.Width, src.Height, GraphicsUnit.Pixel, yellow);
            g.Clip = saved;
        }
    }

    /// <summary>插脚朝下的插头（立在电池上方）。</summary>
    private static void DrawPlugDown(Graphics g, RectangleF area, Color fill, Color rim)
    {
        float width = area.Width;
        float prongHeight = area.Height * 0.30f;
        float bodyHeight = area.Height - prongHeight;
        float bodyWidth = width * 0.86f;
        float bodyLeft = area.X + (width - bodyWidth) / 2f;

        var body = new RectangleF(bodyLeft, area.Y, bodyWidth, bodyHeight);
        float prongWidth = Math.Max(1.2f, width * 0.18f);

        var parts = new List<GraphicsPath>
        {
            Rounded(body, Math.Max(0.8f, bodyHeight * 0.35f)),
            Rounded(new RectangleF(bodyLeft + bodyWidth * 0.14f, area.Y + bodyHeight - 1f, prongWidth, prongHeight + 1f), prongWidth * 0.5f),
            Rounded(new RectangleF(bodyLeft + bodyWidth * 0.86f - prongWidth, area.Y + bodyHeight - 1f, prongWidth, prongHeight + 1f), prongWidth * 0.5f),
        };

        using (var rimBrush = new SolidBrush(rim))
        using (var rimPen = new Pen(rim, Math.Max(1f, area.Height * 0.10f)) { LineJoin = LineJoin.Round })
        {
            foreach (var part in parts)
            {
                g.FillPath(rimBrush, part);
                g.DrawPath(rimPen, part);
            }
        }

        using (var fillBrush = new SolidBrush(fill))
        {
            foreach (var part in parts)
                g.FillPath(fillBrush, part);
        }

        foreach (var part in parts) part.Dispose();
    }
    /// <summary>插头取反差色：深色任务栏用深色插头，浅色任务栏用白色插头。</summary>
    private static ImageAttributes BuildPlugTint(bool lightTaskbar)
    {
        float scale = lightTaskbar ? 1f : -1f;
        float offset = lightTaskbar ? 0f : 1f;

        var matrix = new ColorMatrix(new[]
        {
            new[] { scale, 0f, 0f, 0f, 0f },
            new[] { 0f, scale, 0f, 0f, 0f },
            new[] { 0f, 0f, scale, 0f, 0f },
            new[] { 0f, 0f, 0f, 1f, 0f },
            new[] { offset, offset, offset, 0f, 1f },
        });

        var attributes = new ImageAttributes();
        attributes.SetColorMatrix(matrix);
        return attributes;
    }

    private static ImageAttributes BuildTint(bool lightTaskbar, bool offline, bool lowBattery = false, bool green = false)
    {
        float r = 1f, gr = 1f, b = 1f, a = 1f, off = 0f;

        if (offline)
        {
            r = gr = b = 0.55f;
            a = 0.85f;
        }
        else if (lowBattery)
        {
            r = 1f;
            gr = 0.84f;
            b = 0.04f;
        }
        else if (green)
        {
            r = 0.19f;
            gr = 0.82f;
            b = 0.35f;
        }
        else if (lightTaskbar)
        {
            r = gr = b = -1f;
            off = 1f;
        }

        var matrix = new ColorMatrix(new[]
        {
            new[] { r, 0f, 0f, 0f, 0f },
            new[] { 0f, gr, 0f, 0f, 0f },
            new[] { 0f, 0f, b, 0f, 0f },
            new[] { 0f, 0f, 0f, a, 0f },
            new[] { off, off, off, 0f, 1f },
        });

        var attributes = new ImageAttributes();
        attributes.SetColorMatrix(matrix);
        return attributes;
    }

    public static RectangleF BatteryRect(float size)
    {
        float margin = size * 0.015f;
        float nubWidth = Math.Max(1.6f, size * 0.085f);
        float bodyHeight = size * 0.54f;
        float bodyWidth = size - margin * 2f - nubWidth - 1f;
        float top = (size - bodyHeight) / 2f;
        return new RectangleF(margin, top, bodyWidth, bodyHeight);
    }

    private static void DrawIcon(Graphics g, RectangleF rect, BatteryReading battery, bool lightTaskbar,
        bool offline, bool bolt)
    {
        Color outline = lightTaskbar ? Color.FromArgb(255, 40, 40, 45) : Color.FromArgb(255, 245, 245, 250);
        Color fill = outline;

        if (offline)
        {
            outline = Color.FromArgb(215, 150, 150, 156);
            fill = outline;
        }
        else if (battery?.Percent is int percent && !battery.Charging && !battery.Full && percent < LowBatteryThreshold)
        {
            fill = Yellow;   // 描边保持白色，只有内胆变黄
        }

        DrawBattery(g, rect, battery?.Percent, battery?.Charging == true || battery?.Full == true,
            outline, fill, bolt);
    }

    /// <summary>在电池内部画百分比数字（自动缩小以适应宽度）。</summary>
    private static void DrawPercentText(Graphics g, RectangleF area, int percent, bool lightTaskbar, bool offline)
    {
        string text = percent.ToString();
        Color color = offline
            ? Color.FromArgb(200, 150, 150, 156)
            : lightTaskbar ? Color.FromArgb(25, 25, 28) : Color.White;

        float size = Math.Max(4f, area.Height * 0.98f);
        Font font = new Font("Segoe UI", size, FontStyle.Bold, GraphicsUnit.Pixel);
        try
        {
            while (size > 4f)
            {
                var measured = g.MeasureString(text, font);
                if (measured.Width <= area.Width && measured.Height <= area.Height * 1.15f) break;
                size -= 0.5f;
                font.Dispose();
                font = new Font("Segoe UI", size, FontStyle.Bold, GraphicsUnit.Pixel);
            }

            using var brush = new SolidBrush(color);
            using var format = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
            };
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.DrawString(text, font, brush, area, format);
        }
        finally
        {
            font.Dispose();
        }
    }
    /// <summary>圆形进度环：外圈进度 + 中间百分比。</summary>
    private static void DrawRing(Graphics g, RectangleF rect, BatteryReading battery, bool lightTaskbar, bool offline)
    {
        float size = Math.Min(rect.Width, rect.Height);
        float stroke = Math.Max(1.5f, size * 0.075f);
        var circle = new RectangleF(
            rect.X + (rect.Width - size) / 2f + stroke / 2f,
            rect.Y + (rect.Height - size) / 2f + stroke / 2f,
            size - stroke,
            size - stroke);

        Color outline = lightTaskbar ? Color.FromArgb(255, 32, 32, 36) : Color.FromArgb(255, 250, 250, 252);
        Color accent = offline
            ? Color.FromArgb(200, 140, 140, 146)
            : battery?.Charging == true ? Color.FromArgb(255, 52, 199, 110)
            : battery?.Percent is int low && low < LowBatteryThreshold ? Color.FromArgb(255, 240, 190, 20)
            : outline;

        using (var trackPen = new Pen(Color.FromArgb(70, outline), stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            g.DrawEllipse(trackPen, circle);

        int percent = battery?.Percent ?? 0;
        float ratio = Math.Clamp(percent / 100f, 0f, 1f);

        if (ratio > 0f && !offline)
        {
            using var arc = new GraphicsPath();
            arc.AddArc(circle, -90f, Math.Max(2f, ratio * 360f));
            using var arcPen = new Pen(accent, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawPath(arcPen, arc);
        }

        if (battery == null) return;

        string text = offline ? "--" : percent.ToString();
        float fontSize = size * 0.74f;
        Font font = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel);

        try
        {
            while (fontSize > 4f && g.MeasureString(text, font).Width > size - stroke * 1.4f)
            {
                fontSize -= 0.5f;
                font.Dispose();
                font = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel);
            }

            using var brush = new SolidBrush(accent);
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            var textRect = new RectangleF(rect.X, rect.Y - size * 0.02f, rect.Width, rect.Height);
            g.DrawString(text, font, brush, textRect, format);
        }
        finally
        {
            font.Dispose();
        }
    }
    /// <summary>小卡里使用：与托盘图标同一套系统雪碧图。</summary>
    public static void DrawAuto(Graphics g, RectangleF rect, BatteryReading battery, bool lightTaskbar, bool offline)
    {
        if (Shape == IconShape.Ring)
        {
            DrawRing(g, rect, battery, lightTaskbar, offline);
            return;
        }

        if (Style == BatteryIconStyle.FilledCapsule)
        {
            DrawCapsule(g, rect, battery, offline);
            return;
        }

        DrawFromSheet(g, rect, battery, lightTaskbar, offline, false);
    }
    /// <summary>画电池：outline 为描边色（始终白），fill 为内胆色。</summary>
    public static void DrawBattery(Graphics g, RectangleF rect, int? percent, bool charging,
        Color outline, Color fill, bool bolt = false)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        float nubWidth = Math.Max(1.8f, rect.Height * 0.20f);
        float bodyWidth = rect.Width - nubWidth - 1f;
        var body = new RectangleF(rect.X, rect.Y, bodyWidth, rect.Height);
        float radius = rect.Height * 0.40f;
        float stroke = Math.Max(1.3f, rect.Height * 0.13f);

        using (var path = Rounded(body, radius))
        using (var pen = new Pen(outline, stroke) { LineJoin = LineJoin.Round })
        {
            g.DrawPath(pen, path);
        }

        using (var brush = new SolidBrush(outline))
        {
            float nubHeight = body.Height * 0.34f;
            var nub = new RectangleF(body.Right - stroke * 0.15f,
                body.Top + (body.Height - nubHeight) / 2f, nubWidth, nubHeight);
            using var nubPath = Rounded(nub, nubHeight * 0.42f);
            g.FillPath(brush, nubPath);
        }

        float inset = Math.Max(2.0f, rect.Height * 0.18f);
        var inner = RectangleF.Inflate(body, -inset, -inset);
        if (inner.Width <= 1f || inner.Height <= 1f) return;

        // 先画电量内胆（充电时也保留，和 Windows 一样能看到当前电量）
        if (percent is int value)
        {
            float ratio = Math.Clamp(value / 100f, 0.07f, 1f);
            var fillRect = new RectangleF(inner.X, inner.Y, Math.Max(1.4f, inner.Width * ratio), inner.Height);
            using var clip = Rounded(inner, radius * 0.55f);
            var saved = g.Clip;
            g.SetClip(clip, CombineMode.Intersect);
            using var fillBrush = new SolidBrush(fill);
            g.FillPath(fillBrush, Rounded(fillRect, radius * 0.45f));
            g.Clip = saved;

            if (charging)
            {
                // Windows 的做法：白色插头 + 深色描边，压在白内胆上形成镂空效果
                float lum = 0.299f * fill.R + 0.587f * fill.G + 0.114f * fill.B;
                Color plugFill = lum > 140f ? Color.White : Color.FromArgb(250, 24, 24, 28);
                Color plugRim = lum > 140f ? Color.FromArgb(240, 16, 16, 18) : Color.FromArgb(240, 252, 252, 254);
                DrawPlug(g, inner, plugFill, plugRim);
            }
            return;
        }

        if (charging)
        {
            if (bolt) DrawBolt(g, inner, outline);
            else DrawPlug(g, inner, outline, outline);
        }
    }

    /// <summary>小卡里使用（与托盘图标同一套画法）。</summary>
    public static void DrawBatteryAuto(Graphics g, RectangleF rect, int? percent, bool charging,
        Color outline, Color fill)
    {
        DrawBattery(g, rect, percent, charging, outline, fill);
    }

    private static void DrawCapsule(Graphics g, RectangleF rect, BatteryReading battery, bool offline)
    {
        Color fill = offline ? Color.FromArgb(180, 120, 120, 126) : LevelColor(battery);
        var body = RectangleF.Inflate(rect, -1f, -1f);
        float radius = body.Height * 0.34f;

        using (var path = Rounded(body, radius))
        using (var brush = new SolidBrush(fill))
            g.FillPath(brush, path);

        if (battery?.Percent is int percent)
        {
            string text = percent.ToString();
            float fontSize = body.Height * (text.Length >= 3 ? 0.44f : 0.54f);
            using var font = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel);
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            using var textBrush = new SolidBrush(Color.White);
            g.DrawString(text, font, textBrush, body, format);
        }
    }

    private static void DrawPlug(Graphics g, RectangleF area, Color fill, Color rim)
    {
        float width = area.Width * 0.56f;
        float height = area.Height * 0.94f;
        float left = area.X + (area.Width - width) / 2f;
        float top = area.Y + (area.Height - height) / 2f;

        float prongWidth = Math.Max(1.2f, width * 0.16f);
        float prongHeight = height * 0.34f;
        float bodyWidth = width * 0.82f;
        float bodyHeight = height - prongHeight;

        var parts = new List<GraphicsPath>
        {
            Rounded(new RectangleF(left + width * 0.06f, top, prongWidth, prongHeight + 1f), prongWidth * 0.5f),
            Rounded(new RectangleF(left + width * 0.78f, top, prongWidth, prongHeight + 1f), prongWidth * 0.5f),
            Rounded(new RectangleF(left + (width - bodyWidth) / 2f, top + prongHeight, bodyWidth, bodyHeight),
                Math.Max(0.8f, bodyHeight * 0.36f)),
        };

        // 先画深色外轮廓（每个部件填深色并描边），再填白色，重叠处的深色会被白色盖住，只留外圈
        using (var rimBrush = new SolidBrush(rim))
        using (var rimPen = new Pen(rim, Math.Max(1f, area.Height * 0.11f)) { LineJoin = LineJoin.Round })
        {
            foreach (var part in parts)
            {
                g.FillPath(rimBrush, part);
                g.DrawPath(rimPen, part);
            }
        }

        using (var fillBrush = new SolidBrush(fill))
        {
            foreach (var part in parts)
                g.FillPath(fillBrush, part);
        }

        foreach (var part in parts) part.Dispose();
    }

    private static void DrawBolt(Graphics g, RectangleF area, Color color)
    {
        float w = area.Width * 0.52f;
        float h = area.Height * 0.96f;
        float x = area.X + (area.Width - w) / 2f;
        float y = area.Y + (area.Height - h) / 2f;

        PointF[] points =
        {
            new(x + w * 0.62f, y),
            new(x + w * 0.06f, y + h * 0.55f),
            new(x + w * 0.44f, y + h * 0.55f),
            new(x + w * 0.30f, y + h),
            new(x + w * 0.96f, y + h * 0.40f),
            new(x + w * 0.55f, y + h * 0.40f),
        };

        using var brush = new SolidBrush(color);
        g.FillPolygon(brush, points);
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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);
}
