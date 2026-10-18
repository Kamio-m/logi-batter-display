using System.Text;

namespace LogiBatteryTray;

/// <summary>
/// 一个已打开的 HID 接口。使用重叠 I/O，读写都带超时，避免设备无响应时卡死。
/// </summary>
public sealed class HidDevice : IDisposable
{
    public static readonly Guid HidInterfaceClassGuid = new("4d1e55b2-f16f-11cf-88cb-001111000030");

    public string Path { get; }
    public ushort VendorId { get; private set; }
    public ushort ProductId { get; private set; }
    public ushort VersionNumber { get; private set; }
    public string Product { get; private set; } = "";
    public string Manufacturer { get; private set; } = "";
    public string SerialNumber { get; private set; } = "";
    public ushort UsagePage { get; private set; }
    public ushort Usage { get; private set; }
    public int InputReportLength { get; private set; }
    public int OutputReportLength { get; private set; }
    public int FeatureReportLength { get; private set; }
    public bool CanWrite { get; private set; }

    public string DisplayName => string.IsNullOrWhiteSpace(Product) ? "Logitech HID" : Product;

    private IntPtr _handle = Native.INVALID_HANDLE_VALUE;
    private IntPtr _readEvent;
    private IntPtr _writeEvent;
    private IntPtr _readOverlapped;
    private IntPtr _writeOverlapped;
    private readonly byte[] _readBuffer = new byte[256];

    private HidDevice(string path)
    {
        Path = path;
    }

    public static HidDevice TryOpen(string path)
    {
        IntPtr handle = Native.CreateFile(path,
            Native.GENERIC_READ | Native.GENERIC_WRITE,
            Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE,
            IntPtr.Zero, Native.OPEN_EXISTING, Native.FILE_FLAG_OVERLAPPED, IntPtr.Zero);

        bool canWrite = true;
        if (handle == Native.INVALID_HANDLE_VALUE)
        {
            canWrite = false;
            handle = Native.CreateFile(path, Native.GENERIC_READ,
                Native.FILE_SHARE_READ | Native.FILE_SHARE_WRITE,
                IntPtr.Zero, Native.OPEN_EXISTING, Native.FILE_FLAG_OVERLAPPED, IntPtr.Zero);
            if (handle == Native.INVALID_HANDLE_VALUE) return null;
        }

        var device = new HidDevice(path) { _handle = handle, CanWrite = canWrite };

        var attributes = new Native.HIDD_ATTRIBUTES { Size = MarshalSize<Native.HIDD_ATTRIBUTES>() };
        if (!Native.HidD_GetAttributes(handle, ref attributes))
        {
            device.Dispose();
            return null;
        }
        device.VendorId = attributes.VendorID;
        device.ProductId = attributes.ProductID;
        device.VersionNumber = attributes.VersionNumber;

        var sb = new StringBuilder(512);
        Native.HidD_GetProductString(handle, sb, sb.Capacity);
        device.Product = sb.ToString().Trim();
        sb.Clear();
        Native.HidD_GetManufacturerString(handle, sb, sb.Capacity);
        device.Manufacturer = sb.ToString().Trim();
        sb.Clear();
        Native.HidD_GetSerialNumberString(handle, sb, sb.Capacity);
        device.SerialNumber = sb.ToString().Trim();

        if (Native.HidD_GetPreparsedData(handle, out IntPtr preparsed) && preparsed != IntPtr.Zero)
        {
            try
            {
                var caps = new Native.HIDP_CAPS { Reserved = new ushort[17] };
                if (Native.HidP_GetCaps(preparsed, ref caps) >= 0)
                {
                    device.UsagePage = caps.UsagePage;
                    device.Usage = caps.Usage;
                    device.InputReportLength = caps.InputReportByteLength;
                    device.OutputReportLength = caps.OutputReportByteLength;
                    device.FeatureReportLength = caps.FeatureReportByteLength;
                }
            }
            finally
            {
                Native.HidD_FreePreparsedData(preparsed);
            }
        }

        return device;
    }

    private static int MarshalSize<T>() => System.Runtime.InteropServices.Marshal.SizeOf<T>();

    public static List<HidDevice> Enumerate()
    {
        var devices = new List<HidDevice>();
        Guid guid = HidInterfaceClassGuid;
        IntPtr set = Native.SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero,
            Native.DIGCF_PRESENT | Native.DIGCF_DEVICEINTERFACE);
        if (set == Native.INVALID_HANDLE_VALUE) return devices;

        try
        {
            var interfaceData = new Native.SP_DEVICE_INTERFACE_DATA
            {
                cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.SP_DEVICE_INTERFACE_DATA>()
            };

            for (uint index = 0;
                 Native.SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref guid, index, ref interfaceData);
                 index++)
            {
                Native.SetupDiGetDeviceInterfaceDetail(set, ref interfaceData, IntPtr.Zero, 0, out uint required, IntPtr.Zero);
                if (required == 0) continue;

                IntPtr detail = System.Runtime.InteropServices.Marshal.AllocHGlobal((int)required);
                try
                {
                    // SP_DEVICE_INTERFACE_DETAIL_DATA.cbSize = 8 (x64) / 6 (x86)
                    System.Runtime.InteropServices.Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                    if (!Native.SetupDiGetDeviceInterfaceDetail(set, ref interfaceData, detail, required, out _, IntPtr.Zero))
                        continue;

                    string path = System.Runtime.InteropServices.Marshal.PtrToStringUni(detail + 4) ?? "";
                    if (path.Length == 0) continue;

                    var device = TryOpen(path);
                    if (device != null) devices.Add(device);
                }
                finally
                {
                    System.Runtime.InteropServices.Marshal.FreeHGlobal(detail);
                }
            }
        }
        finally
        {
            Native.SetupDiDestroyDeviceInfoList(set);
        }

        return devices;
    }

    private void EnsureIo()
    {
        if (_readEvent != IntPtr.Zero) return;
        // 自动重置事件：每次等待成功后信号自动清除，否则后续读取会误判为已完成
        _readEvent = Native.CreateEvent(IntPtr.Zero, false, false, IntPtr.Zero);
        _writeEvent = Native.CreateEvent(IntPtr.Zero, false, false, IntPtr.Zero);
        int size = System.Runtime.InteropServices.Marshal.SizeOf<Native.OverlappedRaw>();
        _readOverlapped = System.Runtime.InteropServices.Marshal.AllocHGlobal(size);
        _writeOverlapped = System.Runtime.InteropServices.Marshal.AllocHGlobal(size);
        Native.HidD_SetNumInputBuffers(_handle, 32);
    }

    /// <summary>发送一份输出报告。报告第 0 字节必须是报告 ID。</summary>
    public bool Write(byte[] report, int timeoutMs = 1000)
    {
        if (_handle == Native.INVALID_HANDLE_VALUE || !CanWrite) return false;
        EnsureIo();
        PrepareOverlapped(_writeOverlapped, _writeEvent);

        bool ok = Native.WriteFile(_handle, report, (uint)report.Length, out uint written, _writeOverlapped);
        if (!ok)
        {
            int error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            if (error != Native.ERROR_IO_PENDING) return false;
            if (!WaitFor(_writeEvent, timeoutMs, _writeOverlapped)) return false;
            if (!Native.GetOverlappedResult(_handle, _writeOverlapped, out written, false)) return false;
        }
        return written > 0;
    }

    /// <summary>读取一份输入报告；超时返回 null。</summary>
    public byte[] Read(int timeoutMs)
    {
        if (_handle == Native.INVALID_HANDLE_VALUE) return null;
        EnsureIo();
        PrepareOverlapped(_readOverlapped, _readEvent);

        if (!Native.ReadFile(_handle, _readBuffer, (uint)_readBuffer.Length, out uint read, _readOverlapped))
        {
            int error = System.Runtime.InteropServices.Marshal.GetLastWin32Error();
            if (error != Native.ERROR_IO_PENDING) return null;
            if (!WaitFor(_readEvent, timeoutMs, _readOverlapped)) return null;
            if (!Native.GetOverlappedResult(_handle, _readOverlapped, out read, false)) return null;
        }

        if (read == 0) return null;
        var result = new byte[read];
        Buffer.BlockCopy(_readBuffer, 0, result, 0, (int)read);
        return result;
    }

    /// <summary>清空缓冲区里滞留的输入报告。</summary>
    /// <summary>读出并返回所有待处理的输入报文（含通知）。</summary>
    public List<byte[]> Drain()
    {
        var drained = new List<byte[]>();
        for (int i = 0; i < 8; i++)
        {
            var report = Read(0);
            if (report == null) break;
            drained.Add(report);
        }
        return drained;
    }

    public bool SetFeature(byte[] report) => Native.HidD_SetFeature(_handle, report, report.Length);

    public bool SetOutputReport(byte[] report) => Native.HidD_SetOutputReport(_handle, report, report.Length);

    public byte[] GetFeature(byte[] report)
    {
        var buffer = (byte[])report.Clone();
        return Native.HidD_GetFeature(_handle, buffer, buffer.Length) ? buffer : null;
    }

    private static void PrepareOverlapped(IntPtr overlapped, IntPtr eventHandle)
    {
        var ov = new Native.OverlappedRaw { EventHandle = eventHandle };
        System.Runtime.InteropServices.Marshal.StructureToPtr(ov, overlapped, false);
    }

    private bool WaitFor(IntPtr eventHandle, int timeoutMs, IntPtr overlapped)
    {
        uint result = Native.WaitForSingleObject(eventHandle, (uint)Math.Max(0, timeoutMs));
        if (result == Native.WAIT_TIMEOUT)
        {
            Native.CancelIoEx(_handle, overlapped);
            Native.WaitForSingleObject(eventHandle, 500);
            return false;
        }
        return true;
    }

    public void Dispose()
    {
        if (_handle != Native.INVALID_HANDLE_VALUE)
        {
            Native.CloseHandle(_handle);
            _handle = Native.INVALID_HANDLE_VALUE;
        }
        if (_readEvent != IntPtr.Zero) { Native.CloseHandle(_readEvent); _readEvent = IntPtr.Zero; }
        if (_writeEvent != IntPtr.Zero) { Native.CloseHandle(_writeEvent); _writeEvent = IntPtr.Zero; }
        if (_readOverlapped != IntPtr.Zero) { System.Runtime.InteropServices.Marshal.FreeHGlobal(_readOverlapped); _readOverlapped = IntPtr.Zero; }
        if (_writeOverlapped != IntPtr.Zero) { System.Runtime.InteropServices.Marshal.FreeHGlobal(_writeOverlapped); _writeOverlapped = IntPtr.Zero; }
    }
}
