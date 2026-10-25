using System.Runtime.InteropServices;

namespace LogiBatteryTray;

/// <summary>监听 USB/HID 设备插拔事件，让界面立即反应（类似 G HUB）。</summary>
internal sealed class DeviceWatcher : NativeWindow, IDisposable
{
    private const int WmDeviceChange = 0x0219;
    private const int DbtDeviceArrival = 0x8000;
    private const int DbtDeviceRemoveComplete = 0x8004;
    private const int DbtDevTypDeviceInterface = 5;

    private static readonly Guid HidInterfaceGuid = new("4d1e55b2-f16f-11cf-88cb-001111000030");

    [StructLayout(LayoutKind.Sequential)]
    private struct DevBroadcastDeviceInterface
    {
        public int Size;
        public int DeviceType;
        public int Reserved;
        public Guid ClassGuid;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string Name;
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr RegisterDeviceNotification(IntPtr recipient, ref DevBroadcastDeviceInterface filter, int flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterDeviceNotification(IntPtr handle);

    private IntPtr _registration;
    private bool _disposed;

    public event Action Changed;

    public DeviceWatcher()
    {
        CreateHandle(new CreateParams { Caption = "LogiBatteryTrayDeviceWatcher" });

        var filter = new DevBroadcastDeviceInterface
        {
            Size = Marshal.SizeOf<DevBroadcastDeviceInterface>(),
            DeviceType = DbtDevTypDeviceInterface,
            ClassGuid = HidInterfaceGuid,
        };

        _registration = RegisterDeviceNotification(Handle, ref filter, 0);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmDeviceChange)
        {
            int kind = m.WParam.ToInt32();
            if (kind is DbtDeviceArrival or DbtDeviceRemoveComplete)
                Changed?.Invoke();
        }

        base.WndProc(ref m);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_registration != IntPtr.Zero)
        {
            UnregisterDeviceNotification(_registration);
            _registration = IntPtr.Zero;
        }

        if (Handle != IntPtr.Zero) DestroyHandle();
    }
}
