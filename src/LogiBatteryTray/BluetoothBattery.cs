using System.Runtime.InteropServices;
using System.Text;

namespace LogiBatteryTray;

/// <summary>一个蓝牙设备的电量（来自 Windows 已缓存的 PnP 属性）。</summary>
internal sealed record BluetoothBatteryInfo(string Name, int Percent, DateTime? Updated, string Address);

/// <summary>
/// 读取 Windows 已经拿到的蓝牙设备电量。
/// 数据位于 HFP 设备节点的 PnP 属性 {104EA319-6EE2-4701-BD47-8DDBF425BBE5} / 2（BYTE 百分比），
/// 由系统通过 HFP 的 AT 指令（或 LE 电量服务）更新，这里只做读取，无需管理员权限。
/// </summary>
internal static class BluetoothBattery
{
    private static readonly Guid BluetoothPropertySet = new("104ea319-6ee2-4701-bd47-8ddbf425bbe5");
    private static readonly Guid DevicePropertySet = new("a45c254e-df1c-4efd-8020-67d146a850e0");
    private const uint PropertyBatteryLevel = 2;
    private const uint PropertyFriendlyName = 14;

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDevInfoData
    {
        public int Size;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DevPropKey
    {
        public Guid FormatId;
        public uint PropertyId;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(IntPtr classGuid, string enumerator, IntPtr parent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref SpDevInfoData data);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInstanceId(IntPtr set, ref SpDevInfoData data,
        StringBuilder instanceId, int size, out int required);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceProperty(IntPtr set, ref SpDevInfoData data, ref DevPropKey key,
        out uint type, byte[] buffer, int size, out int required, int flags);

    [DllImport("setupapi.dll")]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    private const uint DigcfPresent = 0x02;
    private const uint DigcfAllClasses = 0x04;

    /// <summary>枚举所有带电量属性的蓝牙设备。</summary>
    public static IReadOnlyList<BluetoothBatteryInfo> ReadAll()
    {
        var results = new List<BluetoothBatteryInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        IntPtr set = SetupDiGetClassDevs(IntPtr.Zero, null, IntPtr.Zero, DigcfPresent | DigcfAllClasses);
        if (set == IntPtr.Zero) return results;

        try
        {
            var data = new SpDevInfoData { Size = Marshal.SizeOf<SpDevInfoData>() };

            for (uint index = 0; SetupDiEnumDeviceInfo(set, index, ref data); index++)
            {
                var idBuilder = new StringBuilder(512);
                if (!SetupDiGetDeviceInstanceId(set, ref data, idBuilder, idBuilder.Capacity, out _)) continue;

                string instanceId = idBuilder.ToString();
                if (instanceId.IndexOf("BTHENUM", StringComparison.OrdinalIgnoreCase) < 0) continue;

                byte[] battery = GetProperty(set, ref data, BluetoothPropertySet, PropertyBatteryLevel);
                if (battery == null || battery.Length == 0) continue;

                int percent = battery[0];
                if (percent < 0 || percent > 100) continue;

                string address = ExtractAddress(instanceId);
                if (!seen.Add(address)) continue;

                string name = ReadName(set, ref data) ?? "蓝牙设备";
                var updated = ReadUpdated(set, ref data);

                results.Add(new BluetoothBatteryInfo(name, percent, updated, address));
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }

        return results.OrderBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static byte[] GetProperty(IntPtr set, ref SpDevInfoData data, Guid formatId, uint propertyId)
    {
        var key = new DevPropKey { FormatId = formatId, PropertyId = propertyId };
        var buffer = new byte[2048];
        if (!SetupDiGetDeviceProperty(set, ref data, ref key, out _, buffer, buffer.Length, out int size, 0))
            return null;
        if (size <= 0 || size > buffer.Length) return null;

        var value = new byte[size];
        Buffer.BlockCopy(buffer, 0, value, 0, size);
        return value;
    }

    private static string ReadName(IntPtr set, ref SpDevInfoData data)
    {
        byte[] raw = GetProperty(set, ref data, DevicePropertySet, PropertyFriendlyName);
        if (raw == null || raw.Length < 2) return null;

        string name = Encoding.Unicode.GetString(raw).TrimEnd('\0');
        int cut = name.IndexOf(" Hands-Free", StringComparison.OrdinalIgnoreCase);
        if (cut > 0) name = name[..cut].Trim();
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    private static DateTime? ReadUpdated(IntPtr set, ref SpDevInfoData data)
    {
        byte[] raw = GetProperty(set, ref data, BluetoothPropertySet, 7);
        if (raw == null || raw.Length < 8) return null;

        long fileTime = BitConverter.ToInt64(raw, 0);
        if (fileTime <= 0) return null;

        try { return DateTime.FromFileTime(fileTime); }
        catch { return null; }
    }

    /// <summary>从实例 ID 末尾取出 12 位蓝牙地址。</summary>
    private static string ExtractAddress(string instanceId)
    {
        string[] parts = instanceId.Split('_');
        for (int i = parts.Length - 1; i >= 0; i--)
        {
            string part = parts[i].Trim();
            if (part.Length == 12 && part.All(Uri.IsHexDigit)) return part.ToUpperInvariant();
        }
        return instanceId;
    }
}
