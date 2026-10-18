using System.Runtime.InteropServices;

namespace LogiBatteryTray;

/// <summary>
/// 通过 Raw Input 监听全局鼠标移动。鼠标一旦活动就刷新一次电量，
/// 这样不必等定时器，也能在设备从休眠唤醒后马上拿到新数据。
/// </summary>
internal sealed class MouseActivityWatcher : NativeWindow, IDisposable
{
    private const int WM_INPUT = 0x00FF;
    private const uint RIDEV_INPUTSINK = 0x00000100;
    private static readonly IntPtr HwndMessage = new(-3); // HWND_MESSAGE

    public event Action Activity;

    public MouseActivityWatcher()
    {
        var parameters = new CreateParams
        {
            Caption = "LogiBatteryTrayInput",
            Parent = HwndMessage,
        };
        CreateHandle(parameters);

        var device = new RAWINPUTDEVICE
        {
            UsagePage = 0x01, // Generic Desktop
            Usage = 0x02,     // Mouse
            Flags = RIDEV_INPUTSINK,
            Target = Handle,
        };
        RegisterRawInputDevices(new[] { device }, 1, Marshal.SizeOf<RAWINPUTDEVICE>());
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == WM_INPUT) Activity?.Invoke();
        base.WndProc(ref message);
    }

    public void Dispose()
    {
        try { DestroyHandle(); } catch { }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE
    {
        public ushort UsagePage;
        public ushort Usage;
        public uint Flags;
        public IntPtr Target;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint count, int size);
}
