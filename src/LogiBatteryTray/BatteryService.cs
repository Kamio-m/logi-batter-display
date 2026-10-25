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

    /// <summary>是否检测到罗技接收器（HID 接口）。拔掉接收器时为 false。</summary>
    public bool ReceiverConnected { get; private set; }

    /// <summary>充电速率估算（百分比/小时），非充电时为 null。</summary>
    public double? ChargeRatePerHour { get; private set; }

    private DateTime? _sampleTime;
    private int? _samplePercent;
    private DateTime _lastRediscover = DateTime.MinValue;
    private byte _lastIndex = 1;
    private string _lastPath;

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
                Problem = ReceiverConnected ? "未检测到鼠标，移动一下鼠标即可连接" : "请连接设备";
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
                    // 接收器还插着（鼠标休眠/充电）：保留上次记录；
                    // 接收器被拔掉则清空，界面显示“请连接设备”
                    if (ReceiverConnected) _devices.AddRange(previous);
                    Problem = !ReceiverConnected
                        ? "请连接设备"
                        : previous.Count == 0 ? "未检测到鼠标" : "鼠标休眠中，移动鼠标即可刷新";
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
                                    || PoweredOffAfterSeconds <= 0
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

    /// <summary>仅供预览：塞一个假设备。</summary>
    internal void AddPreviewDevice(string name, BatteryReading battery)
    {
        ReceiverConnected = true;
        _devices.Add(new LogiDevice
        {
            Name = name,
            Battery = battery,
            Online = true,
            UpdatedAt = DateTime.Now,
            ProtocolVersion = 4.2,
        });
    }

    /// <summary>直接连事件里给出的 HID 接口，跳过整轮枚举（插接收器时用）。</summary>
    public bool TryConnect(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;

        try
        {
            using var hid = HidDevice.TryOpen(path);
            if (hid == null) return false;

            foreach (byte index in IndexOrder())
            {
                var protocol = new HidppDevice(hid, index);
                if (!protocol.TryGetProtocolVersion(out double version, 300) || version < 2.0) continue;

                var battery = protocol.ReadBattery(600);
                if (battery == null) continue;

                string name = protocol.ReadDeviceName(500);
                if (string.IsNullOrWhiteSpace(name)) name = "罗技鼠标";

                _lastIndex = index;
                ReceiverConnected = true;
                Problem = null;
                LastUpdated = DateTime.Now;

                _devices.Clear();
                _devices.Add(new LogiDevice
                {
                    Name = name,
                    Path = path,
                    DeviceIndex = index,
                    Battery = battery,
                    Online = true,
                    UpdatedAt = DateTime.Now,
                    ProtocolVersion = version,
                });

                return true;
            }
        }
        catch
        {
            // 回落正常发现流程
        }

        return false;
    }
    /// <summary>接收器插拔后清空状态，强制下次刷新重新发现。</summary>
    public void Reset()
    {
        _devices.Clear();
        ReceiverConnected = false;
        Problem = null;
    }

    /// <summary>发现顺序：上次成功的索引优先，然后 1-6。</summary>
    private IEnumerable<byte> IndexOrder()
    {
        yield return _lastIndex;
        for (byte index = 1; index <= 6; index++)
            if (index != _lastIndex) yield return index;
    }
    /// <summary>试着在某个接口+索引上读出电量，成功就加入列表。</summary>
    private bool ProbeCandidate(HidDevice hid, byte index, HashSet<string> seen, List<LogiDevice> found)
    {
        var protocol = new HidppDevice(hid, index);
        if (!protocol.TryGetProtocolVersion(out double version, 300) || version < 2.0) return false;

        var battery = protocol.ReadBattery(700);
        if (battery == null) return false;

        string name = protocol.ReadDeviceName(500);
        if (string.IsNullOrWhiteSpace(name)) name = "罗技鼠标";

        // 直连（插线）时设备对 1-6 每个序号都应答，按“接口+名称”去重
        string key = hid.Path + "#" + name;
        if (!seen.Add(key)) return false;

        _lastIndex = index;
        _lastPath = hid.Path;

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

        return true;
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

        // 上次可用的接口排到最前面
        if (!string.IsNullOrEmpty(_lastPath))
        {
            int position = candidates.FindIndex(c => string.Equals(c.Path, _lastPath, StringComparison.OrdinalIgnoreCase));
            if (position > 0)
            {
                var preferred = candidates[position];
                candidates.RemoveAt(position);
                candidates.Insert(0, preferred);
            }
        }

        try
        {
            // 第一遍：每个接口只试上次成功的索引（绝大多数情况一次就中，~几十毫秒）
            foreach (var hid in candidates)
                ProbeCandidate(hid, _lastIndex, seen, found);

            // 第二遍：还没找到才扫 1-6（换了设备序号或插线直连时才会走到）
            if (found.Count == 0)
            {
                foreach (var hid in candidates)
                {
                    for (byte index = 1; index <= 6; index++)
                    {
                        if (index == _lastIndex) continue;
                        if (ProbeCandidate(hid, index, seen, found)) break;
                    }
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

        ReceiverConnected = candidates.Count > 0;

        _devices.Clear();
        _devices.AddRange(found);
        if (_devices.Count > 0)
        {
            LastUpdated = DateTime.Now;
            Problem = null;
        }
    }
}
