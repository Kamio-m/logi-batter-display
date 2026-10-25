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

    public event Action<string> Changed;

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

    /// <summary>从 DEV_BROADCAST_DEVICEINTERFACE 里取出设备接口路径（含 vid/pid）。</summary>
    private static string ReadDeviceInterfaceName(IntPtr lParam)
    {
        if (lParam == IntPtr.Zero) return string.Empty;

        try
        {
            int size = Marshal.ReadInt32(lParam);
            int type = Marshal.ReadInt32(lParam, 4);
            if (type != DbtDevTypDeviceInterface) return string.Empty;

            const int nameOffset = 28; // DWORD size + DWORD type + DWORD reserved + GUID
            int chars = Math.Max(0, (size - nameOffset) / 2);
            var builder = new System.Text.StringBuilder(chars);

            for (int i = 0; i < chars; i++)
            {
                char c = (char)Marshal.ReadInt16(lParam, nameOffset + i * 2);
                if (c == '\0') break;
                builder.Append(c);
            }

            return builder.ToString();
        }
        catch
        {
            return string.Empty;
        }
    }
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmDeviceChange)
        {
            int kind = m.WParam.ToInt32();
            if (kind is DbtDeviceArrival or DbtDeviceRemoveComplete)
                Changed?.Invoke(ReadDeviceInterfaceName(m.LParam));
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
