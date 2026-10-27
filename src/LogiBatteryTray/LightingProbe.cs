using System.Runtime.InteropServices;

namespace LogiBatteryTray;

/// <summary>灯光控制能力探测结果。</summary>
internal sealed record LightingCapability(
    string Name,
    string Id,
    string Bus,
    LightingLevel Level,
    string Detail);

internal enum LightingLevel
{
    Unsupported = 0,
    BrightnessOnly = 1,
    FullRgb = 2,
}

/// <summary>
/// 探测各设备的灯光控制能力。
/// 目前只能「读」能力，不能改灯效：华硕键盘灯光走 Armoury Crate 私有通道，未公开。
/// </summary>
internal static class LightingProbe
{
    public static List<LightingCapability> Scan()
    {
        var result = new List<LightingCapability>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        List<HidDevice> devices;
        try { devices = HidDevice.Enumerate(); }
        catch { return result; }

        foreach (var device in devices)
        {
            string id = $"{device.VendorId:X4}:{device.ProductId:X4}";
            string name = string.IsNullOrWhiteSpace(device.Product) ? id : device.Product.Trim();

            string key = id + "|" + name;
            if (!seen.Add(key)) continue;

            var (level, detail) = Classify(device, name);
            result.Add(new LightingCapability(name, id, "HID", level, detail));
        }

        // 罗技设备统一归类：鼠标键盘的 RGB 走 HID++ 0x8071，本程序暂未实现
        if (result.Count == 0)
        {
            result.Add(new LightingCapability(
                "未检测到可查询灯光的外设", "", "", LightingLevel.Unsupported,
                "插上键盘/鼠标后重新打开此页即可刷新"));
        }

        return result
            .OrderByDescending(c => c.Level)
            .ThenBy(c => c.Name, StringComparer.CurrentCulture)
            .ToList();
    }

    private static (LightingLevel Level, string Detail) Classify(HidDevice device, string name)
    {
        // Aura 灯效接口走厂商页 0xFF31 / 0xFF30，产品名通常含 Aura / ROG
        bool auraPage = device.UsagePage is 0xFF31 or 0xFF30;
        bool auraName = name.Contains("Aura", StringComparison.OrdinalIgnoreCase)
                     || name.Contains("ROG", StringComparison.OrdinalIgnoreCase);

        if (auraPage || auraName)
        {
            return (LightingLevel.FullRgb,
                "检测到 Aura 灯效接口（厂商页 0xFF31/0xFF30）。协议未实现，可后续接入。");
        }

        // 罗技设备：HID++ 特性 0x8071 控制 RGB，0x8040 控制亮度
        if (device.VendorId == 0x046D)
        {
            return (LightingLevel.FullRgb,
                "罗技设备通过 HID++ 0x8071（RGB 灯效）/ 0x8040（亮度）控制。本程序暂未实现该特性。");
        }

        // ASUS 笔记本内置键盘走 I2C-HID + Armoury Crate 私有通道
        if (device.VendorId == 0x0B05)
        {
            return (LightingLevel.BrightnessOnly,
                "华硕内置键盘：灯效由 Armoury Crate 私有服务控制，无公开接口。可用 FN 组合键调亮度。");
        }

        if (device.VendorId == 0x3554 || device.VendorId == 0xAAEE || device.VendorId == 0x05E3)
        {
            return (LightingLevel.Unsupported,
                "该接收器没有可写的灯光端点，固件不支持软件调光。");
        }

        return (LightingLevel.Unsupported,
            "未发现灯光控制接口（无 Aura 厂商页，也无可写端点）。");
    }
}
