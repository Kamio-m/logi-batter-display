namespace LogiBatteryTray;

/// <summary>
/// 扫描系统里「读不出电量」的外设（非罗技），用于让用户登记成估算设备。
/// </summary>
internal static class PeripheralScanner
{
    public sealed record Candidate(string Name, string Id);

    public static List<Candidate> Scan()
    {
        var result = new List<Candidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        List<HidDevice> devices;
        try { devices = HidDevice.Enumerate(); }
        catch { return result; }

        foreach (var device in devices)
        {
            // 罗技设备走 HID++，不需要估算；这里只列读不到电量的
            if (device.VendorId == 0x046D) continue;

            bool interesting =
                (device.UsagePage == 0x01 && (device.Usage == 0x02 || device.Usage == 0x06)) ||
                (device.UsagePage == 0x0C && device.Usage == 0x01);

            if (!interesting) continue;

            string name = string.IsNullOrWhiteSpace(device.Product)
                ? $"未知设备 {device.VendorId:X4}:{device.ProductId:X4}"
                : device.Product.Trim();

            // 同一个接收器的多个接口只保留一条
            string id = $"{device.VendorId:X4}:{device.ProductId:X4}:{name}";
            if (!seen.Add(id)) continue;

            result.Add(new Candidate(name, id));
        }

        return result
            .OrderBy(c => c.Name, StringComparer.CurrentCulture)
            .ToList();
    }
}
