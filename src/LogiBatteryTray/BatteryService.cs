namespace LogiBatteryTray;

/// <summary>发现到的罗技设备及其电量状态。</summary>
public sealed class LogiDevice
{
    public string Name { get; set; } = "罗技鼠标";
    public string Path { get; set; } = "";
    public byte DeviceIndex { get; set; }
    public bool Online { get; set; }

    /// <summary>设备已关闭，区别于休眠。</summary>
    public bool PoweredOff { get; set; }

    /// <summary>开始无响应的时刻。</summary>
    public DateTime? OfflineSince { get; set; }
    public BatteryReading Battery { get; set; }
    public string Problem { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public double ProtocolVersion { get; set; }

    public string Key => $"{Path}#{DeviceIndex}";
}

/// <summary>负责发现设备、读取电量并维护状态。</summary>
public sealed class BatteryService
{
    private readonly List<LogiDevice> _devices = new();
    private int _failures;

    public IReadOnlyList<LogiDevice> Devices => _devices;
    public LogiDevice Primary => _devices.FirstOrDefault(d => d.Online) ?? _devices.FirstOrDefault();
    public DateTime? LastUpdated { get; private set; }

    /// <summary>充电速率估算（百分比/小时），非充电时为 null。</summary>
    public double? ChargeRatePerHour { get; private set; }

    private DateTime? _sampleTime;
    private int? _samplePercent;
    private DateTime _lastRediscover = DateTime.MinValue;

    /// <summary>连续无响应超过这个时长，判定为设备已关闭（协议层无法区分关机与休眠）。</summary>
    public int PoweredOffAfterSeconds { get; set; } = 120;
    public string Problem { get; private set; }

    public bool Refresh()
    {
        try
        {
            if (_devices.Count == 0) Discover();

            if (_devices.Count == 0)
            {
                Problem = "未检测到鼠标，移动一下鼠标即可连接";
                return false;
            }

            bool anyOk = false;
            bool anyInterfaceAlive = false;

            foreach (var device in _devices)
            {
                var result = ReadInto(device);
                if (result == ReadResult.Ok) anyOk = true;
                if (result != ReadResult.InterfaceGone) anyInterfaceAlive = true;
            }

            if (anyOk)
            {
                _failures = 0;
                Problem = null;
                LastUpdated = DateTime.Now;
                UpdateChargeRate();
                return true;
            }

            // 接口还在但读不到：插线充电时鼠标会从接收器切到直连，
            // 接收器仍枚举但不再应答，所以定期重扫一次找到新设备
            bool rediscover = !anyInterfaceAlive || (DateTime.Now - _lastRediscover).TotalSeconds >= 15;
            if (rediscover)
            {
                _lastRediscover = DateTime.Now;
                var previous = _devices.ToList();

                _devices.Clear();
                Discover();

                foreach (var device in _devices)
                {
                    if (ReadInto(device) == ReadResult.Ok) anyOk = true;
                }

                if (anyOk)
                {
                    _failures = 0;
                    Problem = null;
                    LastUpdated = DateTime.Now;
                    UpdateChargeRate();
                    return true;
                }

                if (_devices.Count == 0)
                {
                    // 扫不到（充电/休眠中）：保留上次记录，继续显示“休眠 · 上次 x%”
                    _devices.AddRange(previous);
                    Problem = previous.Count == 0
                        ? "未检测到鼠标（请确认接收器插好或数据线连接正常）"
                        : "鼠标休眠中，移动鼠标即可刷新";
                    _failures++;
                    return false;
                }

                Problem = "鼠标休眠中，移动鼠标即可刷新";
                _failures++;
                return false;
            }

            _failures++;
            Problem = "鼠标休眠中，移动鼠标即可刷新";
            return false;
        }
        catch (Exception ex)
        {
            Problem = ex.Message;
            Log.Write("刷新失败: " + ex);
            return false;
        }
    }

    /// <summary>根据电量变化估算充电速率（设备不提供电压/电流）。</summary>
    private void UpdateChargeRate()
    {
        var battery = Primary?.Battery;
        if (battery?.Percent is not int percent) { ChargeRatePerHour = null; return; }
        if (!battery.Charging) { ChargeRatePerHour = null; _samplePercent = null; _sampleTime = null; return; }

        if (_samplePercent is int previous && _sampleTime is DateTime time)
        {
            double hours = (DateTime.Now - time).TotalHours;
            int delta = percent - previous;
            if (hours >= 1.0 / 120.0 && delta >= 0)
            {
                double rate = delta / hours;
                if (rate >= 0 && rate < 200)
                    ChargeRatePerHour = ChargeRatePerHour is double old ? old * 0.6 + rate * 0.4 : rate;
            }
        }

        if (_samplePercent != percent)
        {
            _samplePercent = percent;
            _sampleTime = DateTime.Now;
        }
    }

    private ReadResult ReadInto(LogiDevice device)
    {
        try
        {
            using var hid = HidDevice.TryOpen(device.Path);
            if (hid == null)
            {
                device.Online = false;
                device.Problem = "接口已关闭";
                return ReadResult.InterfaceGone;
            }

            var protocol = new HidppDevice(hid, device.DeviceIndex);
            var battery = protocol.ReadBattery();
            if (battery == null)
            {
                device.Online = false;
                device.Problem = protocol.LastError;

                device.OfflineSince ??= DateTime.Now;
                bool? connection = protocol.LastConnectionState(device.DeviceIndex);
                device.PoweredOff = connection == false
                                    || protocol.LastError.Contains("0x0B")
                                    || protocol.LastError.Contains("0x0A")
                                    || DateTime.Now - device.OfflineSince.Value >= TimeSpan.FromSeconds(PoweredOffAfterSeconds);
                return ReadResult.NoResponse;
            }

            device.PoweredOff = false;
            device.OfflineSince = null;
            device.Battery = battery;
            device.Online = true;
            device.Problem = null;
            device.UpdatedAt = DateTime.Now;
            return ReadResult.Ok;
        }
        catch (Exception ex)
        {
            device.Online = false;
            device.Problem = ex.Message;
            return ReadResult.NoResponse;
        }
    }

    private enum ReadResult
    {
        Ok,
        NoResponse,
        InterfaceGone,
    }

    /// <summary>扫描罗技 HID 接口，找出支持电量特性的鼠标。</summary>
    public void Discover()
    {
        var found = new List<LogiDevice>();
        var seen = new HashSet<string>();

        var candidates = new List<HidDevice>();
        foreach (var hid in HidDevice.Enumerate())
        {
            bool usable = hid.VendorId == 0x046D
                          && hid.CanWrite
                          && hid.OutputReportLength >= 20
                          && (hid.UsagePage == 0xFF00 || hid.UsagePage == 0xFF43);
            if (usable) candidates.Add(hid);
            else hid.Dispose();
        }

        // 没有长报文接口时再退回短报文接口（少数直连设备）
        if (candidates.Count == 0)
        {
            foreach (var hid in HidDevice.Enumerate())
            {
                bool usable = hid.VendorId == 0x046D
                              && hid.CanWrite
                              && hid.OutputReportLength >= 7
                              && (hid.UsagePage == 0xFF00 || hid.UsagePage == 0xFF43);
                if (usable) candidates.Add(hid);
                else hid.Dispose();
            }
        }

        try
        {
            foreach (var hid in candidates)
            {
                for (byte index = 1; index <= 6; index++)
                {
                    var protocol = new HidppDevice(hid, index);
                    if (!protocol.TryGetProtocolVersion(out double version) || version < 2.0) continue;

                    var battery = protocol.ReadBattery();
                    if (battery == null) continue;

                    string name = protocol.ReadDeviceName();
                    if (string.IsNullOrWhiteSpace(name)) name = "罗技鼠标";

                    // 直连（插线）时设备对 1-6 每个序号都应答，按“接口+名称”去重
                    string key = hid.Path + "#" + name;
                    if (!seen.Add(key)) continue;

                    found.Add(new LogiDevice
                    {
                        Name = name,
                        Path = hid.Path,
                        DeviceIndex = index,
                        Battery = battery,
                        Online = true,
                        UpdatedAt = DateTime.Now,
                        ProtocolVersion = version,
                    });
                }
            }
        }
        finally
        {
            foreach (var hid in candidates)
            {
                try { hid.Dispose(); } catch { }
            }
        }

        _devices.Clear();
        _devices.AddRange(found);
        if (_devices.Count > 0)
        {
            LastUpdated = DateTime.Now;
            Problem = null;
        }
    }
}
