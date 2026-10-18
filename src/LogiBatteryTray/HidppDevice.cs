using System.Text;

namespace LogiBatteryTray;

/// <summary>一次电量读取结果。</summary>
public sealed class BatteryReading
{
    public int? Percent { get; init; }
    public bool Charging { get; init; }
    public bool Full { get; init; }
    public int? VoltageMillivolts { get; init; }
    public int? RawLevel { get; init; }
    public string Source { get; init; } = "";

    public string StatusText =>
        Full ? "已充满" :
        Charging ? "充电中" :
        "使用中";

    public override string ToString() =>
        Percent.HasValue ? $"{Percent.Value}% ({StatusText})" : $"未知 ({StatusText})";
}

public sealed class HidppFeature
{
    public ushort Id { get; init; }
    public byte Index { get; init; }
    public byte Type { get; init; }
    public byte Version { get; init; }
}

/// <summary>
/// HID++ 2.0 客户端。
/// 报文：短 0x10（7 字节）/ 长 0x11（20 字节）。
/// 注意：特性 ID 是大端序；Software ID 必须位于 0x08–0x0F（高位置 1）。
/// 0x0D 与 0x0F 被 G HUB / 罗技固件占用，这里固定使用 0x0B（与 Solaar 一致）。
/// </summary>
public sealed class HidppDevice
{
    private const byte ShortReportId = 0x10;
    private const byte LongReportId = 0x11;
    private const byte SoftwareId = 0x0B;

    private const ushort FeatureRoot = 0x0000;
    private const ushort FeatureFeatureSet = 0x0001;
    private const ushort FeatureDeviceName = 0x0005;
    private const ushort FeatureBatteryLevelStatus = 0x1000;
    private const ushort FeatureBatteryVoltage = 0x1001;
    private const ushort FeatureBatteryStatus = 0x1004;

    private readonly HidDevice _hid;
    private readonly byte _reportId;
    private readonly int _reportLength;
    private readonly Dictionary<ushort, byte> _featureIndex = new();

    public byte DeviceIndex { get; }
    public string LastError { get; private set; } = "";
    public byte[] LastResponse { get; private set; }
    public List<HidppFeature> Features { get; } = new();

    public HidppDevice(HidDevice hid, byte deviceIndex)
    {
        _hid = hid;
        DeviceIndex = deviceIndex;
        bool useLong = hid.OutputReportLength >= 20;
        _reportId = useLong ? LongReportId : ShortReportId;
        _reportLength = useLong ? 20 : 7;
    }

    private byte[] BuildReport(byte featureIndex, byte function, byte[] parameters)
    {
        var report = new byte[_reportLength];
        report[0] = _reportId;
        report[1] = DeviceIndex;
        report[2] = featureIndex;
        report[3] = (byte)((function << 4) | SoftwareId);
        if (parameters != null)
            Array.Copy(parameters, 0, report, 4, Math.Min(parameters.Length, _reportLength - 4));
        return report;
    }

    /// <summary>发送一条特性请求并等待应答；成功返回参数区，失败返回 null。</summary>
    public byte[] Call(byte featureIndex, byte function, params byte[] parameters)
        => Call(featureIndex, function, 2500, parameters);

    public byte[] Call(byte featureIndex, byte function, int timeoutMs, params byte[] parameters)
    {
        var report = BuildReport(featureIndex, function, parameters);
        LastResponse = null;

        _hid.Drain();
        if (!_hid.Write(report)) { LastError = "写入失败"; return null; }

        long deadline = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            int remaining = (int)(deadline - Environment.TickCount64);
            if (remaining <= 0) { LastError = "设备无响应"; return null; }

            var response = _hid.Read(remaining);
            if (response == null) { LastError = "设备无响应"; return null; }
            if (response.Length < 7) continue;

            // 接收器可能用 0x01 或 0x81 回复同一个设备号
            if ((response[1] & 0x7F) != (DeviceIndex & 0x7F)) continue;
            if ((response[3] & 0x0F) != SoftwareId) continue;

            LastResponse = response;

            if (response[2] == 0xFF)
            {
                LastError = $"HID++ 错误 0x{response[4]:X2} ({BitConverter.ToString(response)})";
                return null;
            }
            if (response[2] != featureIndex) continue;

            int payloadLength = response.Length - 4;
            var payload = new byte[payloadLength];
            Buffer.BlockCopy(response, 4, payload, 0, payloadLength);
            return payload;
        }
    }

    public bool TryGetProtocolVersion(out double version)
    {
        version = 0;
        var payload = Call(0x00, 0x01, 900, 0x00, 0x00, 0x00);
        if (payload == null || payload.Length < 3) return false;
        version = payload[0] + payload[1] / 10.0;
        return true;
    }

    public byte GetFeatureIndex(ushort featureId)
    {
        if (featureId == FeatureRoot) return 0;
        if (_featureIndex.TryGetValue(featureId, out byte cached)) return cached;

        var payload = Call(0x00, 0x00, 2000, (byte)(featureId >> 8), (byte)(featureId & 0xFF));
        if (payload == null || payload.Length < 1) return 0;

        byte index = payload[0];
        _featureIndex[featureId] = index;
        return index;
    }

    /// <summary>通过 FEATURE_SET 枚举设备的全部特性。</summary>
    public void EnumerateFeatures()
    {
        Features.Clear();
        byte featureSet = GetFeatureIndex(FeatureFeatureSet);
        if (featureSet == 0) return;

        var countPayload = Call(featureSet, 0x00, 2000);
        if (countPayload == null || countPayload.Length < 1) return;
        int count = countPayload[0];

        for (int i = 0; i < count; i++)
        {
            var info = Call(featureSet, 0x01, 1500, (byte)i);
            if (info == null || info.Length < 2) continue;

            ushort id = (ushort)((info[0] << 8) | info[1]);
            byte type = info.Length > 2 ? info[2] : (byte)0;
            byte version = info.Length > 3 ? info[3] : (byte)0;
            byte index = GetFeatureIndex(id);

            Features.Add(new HidppFeature { Id = id, Index = index, Type = type, Version = version });
        }
    }

    public string ReadDeviceName()
    {
        byte feature = GetFeatureIndex(FeatureDeviceName);
        if (feature == 0) return null;

        // 0x0005 功能 1：按索引取名称字符串；功能 0 只返回名称数量
        var payload = Call(feature, 0x01, 2000, 0x00);
        if (payload == null || payload.Length == 0)
            payload = Call(feature, 0x00, 2000);
        if (payload == null || payload.Length == 0) return null;

        string name = Encoding.UTF8.GetString(payload).TrimEnd('\0').Trim();
        var builder = new StringBuilder(name.Length);
        foreach (char c in name)
            if (!char.IsControl(c)) builder.Append(c);
        name = builder.ToString().Trim();
        return name.Length > 0 ? name : null;
    }

    /// <summary>读取电量。优先 BATTERY_LEVEL_STATUS(0x1000)，再尝试 BATTERY_STATUS(0x1004) 与电压(0x1001)。</summary>
    public BatteryReading ReadBattery()
    {
        byte levelFeature = GetFeatureIndex(FeatureBatteryLevelStatus);
        if (levelFeature > 0)
        {
            var payload = Call(levelFeature, 0x00);
            if (payload != null && payload.Length >= 3)
            {
                int percent = payload[0];
                int rawLevel = payload[1];
                int chargingStatus = payload[2];
                int externalPower = payload.Length > 3 ? payload[3] : 0;

                bool charging = chargingStatus is 1 or 3;
                bool full = chargingStatus == 2;
                if (!charging && !full && externalPower == 1 && percent >= 100) full = true;

                if (percent <= 100)
                {
                    return new BatteryReading
                    {
                        Percent = percent,
                        RawLevel = rawLevel,
                        Charging = charging,
                        Full = full,
                        Source = "BATTERY_LEVEL_STATUS",
                    };
                }
            }
        }

        // 0x1004 Unified Battery：功能 0 是"能力位域"，功能 1 才是电量信息
        // 应答格式：[百分比, 档位位域(1/2/4/8), 状态(0放电,1充电,2慢充,3充满,4错误), 外部供电]
        byte statusFeature = GetFeatureIndex(FeatureBatteryStatus);
        if (statusFeature > 0)
        {
            var payload = Call(statusFeature, 0x01);
            if (payload != null && payload.Length >= 3)
            {
                int percentage = payload[0];
                int levelBits = payload[1];
                int status = payload[2];

                int? percent = percentage is > 0 and <= 100 ? percentage : null;
                if (percent == null)
                {
                    percent = levelBits switch
                    {
                        1 => 5,    // critical
                        2 => 20,   // low
                        4 => 60,   // good
                        8 => 100,  // full
                        _ => null,
                    };
                }

                if (percent != null)
                {
                    return new BatteryReading
                    {
                        Percent = percent,
                        RawLevel = levelBits,
                        Charging = status is 1 or 2,
                        Full = status == 3,
                        Source = "UNIFIED_BATTERY",
                    };
                }
            }
        }

        byte voltageFeature = GetFeatureIndex(FeatureBatteryVoltage);
        if (voltageFeature > 0)
        {
            var payload = Call(voltageFeature, 0x00);
            if (payload != null && payload.Length >= 2)
            {
                int millivolts = payload[0] | (payload[1] << 8);
                int flags = payload.Length > 2 ? payload[2] : 0;
                int? percent = payload.Length > 3 && payload[3] is > 0 and <= 100 ? payload[3] : null;

                return new BatteryReading
                {
                    Percent = percent,
                    Charging = (flags & 0x01) != 0,
                    VoltageMillivolts = millivolts,
                    Source = "BATTERY_VOLTAGE",
                };
            }
        }

        return null;
    }
}
