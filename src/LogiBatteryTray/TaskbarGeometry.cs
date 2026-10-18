using System.Runtime.InteropServices;

namespace LogiBatteryTray;

/// <summary>定位任务栏通知区域（TrayNotifyWnd）的位置，用于把电量小卡停靠到托盘里。</summary>
internal static class TaskbarGeometry
{
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string className, string windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string windowName);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    /// <summary>返回通知区域矩形；任务栏不可用（自动隐藏/全屏）时返回 null。</summary>
    public static Rectangle? GetTrayRect()
    {
        IntPtr tray = FindWindow("Shell_TrayWnd", null);
        if (tray == IntPtr.Zero) return null;

        IntPtr notify = FindWindowEx(tray, IntPtr.Zero, "TrayNotifyWnd", null);
        if (notify == IntPtr.Zero) return null;

        if (!GetWindowRect(notify, out RECT rect)) return null;
        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0) return null;

        return new Rectangle(rect.Left, rect.Top, width, height);
    }

    /// <summary>把小卡放到托盘里、系统图标组（网络/音量/电池）左侧的位置。</summary>
    public static Point? GetDockLocation(int chipWidth, int chipHeight, int scalePercent)
    {
        var tray = GetTrayRect();
        if (tray == null) return null;

        var area = tray.Value;
        // 时钟约 1.9 倍任务栏高，系统图标组约 1.8 倍；留出间隔，避免盖住系统图标
        int clockWidth = (int)(area.Height * 1.9f);
        int groupWidth = (int)(area.Height * 1.8f);
        int groupLeft = area.Right - clockWidth - groupWidth;

        int x = groupLeft - chipWidth - (int)(area.Height * 0.12f);
        if (x < area.Left + 4) x = area.Left + 4;

        int y = area.Top + (area.Height - chipHeight) / 2;
        return new Point(x, y);
    }
}
