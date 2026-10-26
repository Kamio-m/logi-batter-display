using System.Drawing.Imaging;

namespace LogiBatteryTray;

/// <summary>
/// 拖拽时跟随鼠标的半透明卡片快照（不抢焦点、不显示在任务栏）。
/// </summary>
internal sealed class DragGhostForm : Form
{
    private readonly Bitmap _image;

    public DragGhostForm(Bitmap image)
    {
        _image = image;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        Width = image.Width;
        Height = image.Height;
        BackColor = Color.FromArgb(32, 32, 36);
        Opacity = 0.15;
        DoubleBuffered = true;

        var picture = new PictureBox
        {
            Dock = DockStyle.Fill,
            Image = image,
            SizeMode = PictureBoxSizeMode.StretchImage,
            BackColor = Color.Transparent,
        };
        Controls.Add(picture);
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE
            cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW
            return cp;
        }
    }

    /// <summary>让幽灵跟随鼠标（以抓取点为中心）。</summary>
    public void FollowCursor(int offsetX, int offsetY)
    {
        if (IsDisposed) return;
        Location = new Point(Cursor.Position.X - offsetX, Cursor.Position.Y - offsetY);
    }

    /// <summary>淡入到目标不透明度。</summary>
    public void FadeIn(double target)
    {
        for (double o = 0.15; o < target; o += 0.12)
        {
            if (IsDisposed) return;
            Opacity = Math.Min(target, o);
            Application.DoEvents();
            Thread.Sleep(8);
        }
        Opacity = target;
    }

    /// <summary>淡出并关闭。</summary>
    public void FadeOutAndClose()
    {
        for (double o = Opacity; o > 0; o -= 0.12)
        {
            if (IsDisposed) return;
            Opacity = Math.Max(0, o);
            Application.DoEvents();
            Thread.Sleep(8);
        }
        Close();
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) _image.Dispose();
    }

    /// <summary>把控件渲染成位图（按比例缩小，保证拖动流畅）。</summary>
    public static Bitmap Snapshot(Control control, int maxWidth = 380)
    {
        int width = Math.Max(1, control.Width);
        int height = Math.Max(1, control.Height);

        var full = new Bitmap(width, height);
        try
        {
            control.DrawToBitmap(full, new Rectangle(0, 0, width, height));
        }
        catch
        {
            using var g = Graphics.FromImage(full);
            g.Clear(Color.FromArgb(40, 40, 45));
        }

        if (width <= maxWidth) return full;

        int scaledHeight = Math.Max(1, (int)(height * (maxWidth / (double)width)));
        var scaled = new Bitmap(maxWidth, scaledHeight);
        using (var g = Graphics.FromImage(scaled))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
            g.DrawImage(full, 0, 0, maxWidth, scaledHeight);
        }

        full.Dispose();
        return scaled;
    }
}
