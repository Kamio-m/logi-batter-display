using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace LogiBatteryTray;

/// <summary>页面容器：负责清屏与「快照淡入」动画，避免残影。</summary>
internal sealed class PageHost : Panel
{
    private Bitmap _snapshot;
    private double _alpha;
    private int _offsetX;
    private Color _emptyColor = Color.Black;

    public PageHost()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        DoubleBuffered = true;
    }

    [System.ComponentModel.Browsable(false)]
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Color EmptyColor
    {
        get => _emptyColor;
        set { _emptyColor = value; Invalidate(); }
    }

    /// <summary>设置动画快照与不透明度（0-1）。</summary>
    public void SetSnapshot(Bitmap snapshot, double alpha, int offsetX = 0)
    {
        if (!ReferenceEquals(_snapshot, snapshot))
        {
            _snapshot?.Dispose();
            _snapshot = snapshot;
        }

        _alpha = Math.Clamp(alpha, 0, 1);
        _offsetX = offsetX;
        Invalidate();
    }

    public void ClearSnapshot()
    {
        _snapshot?.Dispose();
        _snapshot = null;
        _alpha = 0;
        Invalidate();
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // 每帧强制刷满底色，杜绝残影
        using var brush = new SolidBrush(_emptyColor);
        e.Graphics.FillRectangle(brush, ClientRectangle);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);

        if (_snapshot == null) return;

        var state = e.Graphics.Save();
        try
        {
            e.Graphics.DrawImageUnscaled(_snapshot, _offsetX, 0);
        }
        finally
        {
            e.Graphics.Restore(state);
        }
    }
}
