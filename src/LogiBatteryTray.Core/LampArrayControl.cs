using Windows.Devices.Enumeration;
using Windows.Devices.Lights;
using Windows.Devices.Lights.Effects;
using WinColor = Windows.UI.Color;

namespace LogiBatteryTray;

/// <summary>
/// 通过 Windows 动态光效（LampArray）接口控制灯效。
/// 走系统标准协议，不需要厂商私有指令。
/// </summary>
internal static class LampArrayControl
{
    private static readonly List<LampArrayEffectPlaylist> Playlists = new();

    /// <summary>停止本程序播放的所有灯效，把控制权还给系统/厂商软件。</summary>
    public static void StopAll()
    {
        foreach (var playlist in Playlists)
        {
            try { playlist.Stop(); } catch { }
        }

        Playlists.Clear();

        _solidPlaylist = null;
        _solidEffect = null;
        _lamp = null;
        _lampId = "";
    }

    private static byte _lastR, _lastG, _lastB;
    private static bool _hasApplied;

    /// <summary>直接写颜色（不走效果引擎），缓存设备避免每帧枚举。</summary>
    public static bool SetColorDirect(byte r, byte g, byte b, out string message)
    {
        message = "";

        try
        {
            if (_lamp == null)
            {
                var infos = DeviceInformation.FindAllAsync(LampArray.GetDeviceSelector())
                    .AsTask().GetAwaiter().GetResult();

                if (infos.Count == 0) { message = "系统未找到 LampArray 设备"; return false; }

                _lampId = infos[0].Id;
                _lamp = LampArray.FromIdAsync(_lampId).AsTask().GetAwaiter().GetResult();
                if (_lamp == null) { message = "无法打开设备"; return false; }
            }

            _lamp.SetColor(WinColor.FromArgb(255, r, g, b));
            return true;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            _lamp = null;
            return false;
        }
    }
    /// <summary>设备当前是否允许本进程控制。</summary>
    public static bool IsAvailable()
    {
        try
        {
            var infos = DeviceInformation.FindAllAsync(LampArray.GetDeviceSelector())
                .AsTask().GetAwaiter().GetResult();

            foreach (var info in infos)
            {
                var lamp = LampArray.FromIdAsync(info.Id).AsTask().GetAwaiter().GetResult();
                if (lamp != null && lamp.IsAvailable) return true;
            }
        }
        catch { }

        return false;
    }

    /// <summary>仅在颜色变化或需要重新接管时才播放，避免每秒重启导致闪烁。</summary>
    public static bool ApplyIfNeeded(byte r, byte g, byte b, out string message)
    {
        message = "";

        if (_hasApplied && _lastR == r && _lastG == g && _lastB == b && _solidPlaylist != null)
            return true;

        bool ok = PlaySolid(r, g, b, out message);
        if (ok)
        {
            _lastR = r; _lastG = g; _lastB = b;
            _hasApplied = true;
        }
        else
        {
            _hasApplied = false;
        }

        return ok;
    }
    /// <summary>每台 LampArray 的可用状态（诊断用）。</summary>
    public static List<string> State()
    {
        var list = new List<string>();

        try
        {
            var infos = DeviceInformation.FindAllAsync(LampArray.GetDeviceSelector())
                .AsTask().GetAwaiter().GetResult();

            foreach (var info in infos)
            {
                var lamp = LampArray.FromIdAsync(info.Id).AsTask().GetAwaiter().GetResult();
                if (lamp == null) { list.Add($"{info.Name}: 无法打开"); continue; }

                list.Add($"{info.Name} | 灯珠 {lamp.LampCount} | 可用 {lamp.IsAvailable} | 已启用 {lamp.IsEnabled} | 已连接 {lamp.IsConnected} | 亮度 {lamp.BrightnessLevel:0.00}");
            }
        }
        catch (Exception ex)
        {
            list.Add("失败: " + ex.Message);
        }

        return list;
    }

    private static bool _watcherAttached;
    private static bool _availableCache = true;
    private static bool _cacheReady;

    /// <summary>控制权变化回调（true = 拿到控制权）。</summary>
    public static event Action<bool> AvailabilityChanged;

    /// <summary>后台预热：启动时调用，避免首次读取时枚举设备卡顿。</summary>
    public static void WarmUp() => Task.Run(() => { try { AttachAvailabilityWatcher(); } catch { } });

    /// <summary>订阅系统的 AvailabilityChanged 事件（系统会主动通知控制权变化）。</summary>
    public static void AttachAvailabilityWatcher()
    {
        if (_watcherAttached) return;

        try
        {
            var infos = DeviceInformation.FindAllAsync(LampArray.GetDeviceSelector())
                .AsTask().GetAwaiter().GetResult();

            if (infos.Count == 0) return;

            if (_lamp == null)
                _lamp = LampArray.FromIdAsync(infos[0].Id).AsTask().GetAwaiter().GetResult();

            if (_lamp == null) return;

            _availableCache = _lamp.IsAvailable;
            _cacheReady = true;

            _lamp.AvailabilityChanged += (_, _) =>
            {
                try
                {
                    _availableCache = _lamp.IsAvailable;
                    AvailabilityChanged?.Invoke(_availableCache);
                }
                catch { }
            };

            _watcherAttached = true;
        }
        catch { }
    }
    /// <summary>
    /// 读取「背景光控件」排序：HKCU\Software\Microsoft\Lighting\Devices\*\Providers
    /// 值为 "1","2","3"... 表示优先级，1 为最高。
    /// </summary>
    public static bool IsFirstPriority(out string firstProvider)
    {
        firstProvider = "";
        try
        {
            string family = PackageFamilyName();
            if (string.IsNullOrEmpty(family)) return true;      // 非打包版无意义

            using var devices = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Lighting\Devices");
            if (devices == null) return true;

            bool anyChecked = false;

            foreach (string deviceKey in devices.GetSubKeyNames())
            {
                using var providers = devices.OpenSubKey(deviceKey + @"\Providers");
                if (providers == null) continue;

                string first = null;
                foreach (string valueName in providers.GetValueNames())
                {
                    if (valueName == "1") first = providers.GetValue(valueName) as string;
                }

                if (string.IsNullOrEmpty(first)) continue;

                anyChecked = true;
                if (first.Contains("LogiBatteryTray", StringComparison.OrdinalIgnoreCase)) return true;

                firstProvider = first;
            }

            return !anyChecked;      // 没有任何设备设置 → 视为可用
        }
        catch
        {
            return true;
        }
    }

    private static string PackageFamilyName()
    {
        try
        {
            int length = 0;
            GetCurrentPackageFamilyName(ref length, null);
            if (length <= 1) return null;

            var builder = new System.Text.StringBuilder(length);
            return GetCurrentPackageFamilyName(ref length, builder) == 0 ? builder.ToString() : null;
        }
        catch
        {
            return null;
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetCurrentPackageFamilyName(ref int length, System.Text.StringBuilder name);
    /// <summary>快速读取控制权（复用已缓存设备，不枚举，微秒级）。</summary>
    public static bool IsAvailableFast()
    {
        try
        {
            if (_cacheReady) return _availableCache;

            // 尚未初始化：立刻枚举一次并缓存
            AttachAvailabilityWatcher();
            return _cacheReady ? _availableCache : true;
        }
        catch
        {
            return true;
        }
    }

    public static List<string> Probe()
    {
        var list = new List<string>();

        try
        {
            var infos = DeviceInformation.FindAllAsync(LampArray.GetDeviceSelector())
                .AsTask().GetAwaiter().GetResult();

            foreach (var info in infos)
                list.Add($"{info.Name} | {info.Id}");
        }
        catch (Exception ex)
        {
            list.Add("枚举失败: " + ex.Message);
        }

        return list;
    }

    /// <summary>用系统灯效引擎播放静态色（会自动接管设备的 AutonomousMode）。</summary>
    /// <summary>列出灯效类的构造函数（诊断用）。</summary>
    public static List<string> Describe()
    {
        var lines = new List<string>();
        var asm = typeof(LampArraySolidEffect).Assembly;
        var types = asm.GetTypes()
            .Where(t => t.Namespace == "Windows.Devices.Lights.Effects" && !t.Name.StartsWith("<"))
            .Take(20)
            .ToArray();

        foreach (var type in types)
        {
            lines.Add("== " + type.FullName + " ==");
            foreach (var ctor in type.GetConstructors())
                lines.Add("  ctor(" + string.Join(", ", ctor.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}")) + ")");

            foreach (var method in type.GetMethods().Where(m => m.DeclaringType == type))
                lines.Add("  " + method.Name + "(" + string.Join(", ", method.GetParameters().Select(p => p.ParameterType.Name)) + ")");
        }

        return lines;
    }

    private static LampArrayEffectPlaylist _solidPlaylist;
    private static LampArraySolidEffect _solidEffect;
    private static string _solidDeviceId = "";

    /// <summary>
    /// 播放/更新常亮颜色。复用同一个灯效对象，只改 Color 属性，
    /// 避免每帧重启灯效导致的「一顿一顿」刷新感。
    /// </summary>
    private static LampArray _lamp;
    private static string _lampId = "";

    /// <summary>
    /// 播放/更新常亮颜色：复用已缓存的设备与灯效对象，只改 Color。
    /// 避免每帧枚举设备（那会阻塞界面导致点不动）。
    /// </summary>
    public static bool PlaySolid(byte r, byte g, byte b, out string message)
    {
        message = "";

        try
        {
            var color = WinColor.FromArgb(255, r, g, b);

            // 已启动：只改颜色，零枚举、零重启
            if (_solidEffect != null && _solidPlaylist != null)
            {
                _solidEffect.Color = color;
                return true;
            }

            // 首次或失效后重建（只在这里枚举设备）
            var infos = DeviceInformation.FindAllAsync(LampArray.GetDeviceSelector())
                .AsTask().GetAwaiter().GetResult();

            if (infos.Count == 0) { message = "系统未找到 LampArray 设备"; return false; }

            var info = infos[0];
            _lamp = LampArray.FromIdAsync(info.Id).AsTask().GetAwaiter().GetResult();
            if (_lamp == null) { message = "无法打开设备"; return false; }

            _lampId = info.Id;

            _solidPlaylist = new LampArrayEffectPlaylist
            {
                RepetitionMode = LampArrayRepetitionMode.Forever,
            };

            _solidEffect = new LampArraySolidEffect(_lamp, Enumerable.Range(0, Math.Max(1, _lamp.LampCount)).ToArray())
            {
                Color = color,
                Duration = TimeSpan.Zero,   // 无限时长，改 Color 才会实时生效
                CompletionBehavior = LampArrayEffectCompletionBehavior.KeepState,
            };

            _solidPlaylist.Append(_solidEffect);
            _solidPlaylist.Start();
            Playlists.Add(_solidPlaylist);

            message = $"{info.Name}（{_lamp.LampCount} 颗灯珠） ";
            return true;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            return false;
        }
    }
    /// <summary>连续写入颜色若干次，用于对抗神光同步的覆盖。</summary>
    public static bool Flash(byte r, byte g, byte b, int times, int intervalMs, out string message)
    {
        message = "";
        try
        {
            var infos = DeviceInformation.FindAllAsync(LampArray.GetDeviceSelector())
                .AsTask().GetAwaiter().GetResult();

            if (infos.Count == 0) { message = "系统未找到 LampArray 设备"; return false; }

            var lamps = new List<LampArray>();
            foreach (var info in infos)
            {
                var lamp = LampArray.FromIdAsync(info.Id).AsTask().GetAwaiter().GetResult();
                if (lamp != null) { lamps.Add(lamp); message += $"{info.Name}（{lamp.LampCount} 颗灯珠） "; }
            }

            for (int i = 0; i < times; i++)
            {
                foreach (var lamp in lamps)
                    lamp.SetColor(WinColor.FromArgb(255, r, g, b));

                Thread.Sleep(intervalMs);
            }

            return lamps.Count > 0;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            return false;
        }
    }

    public static bool SetColor(byte r, byte g, byte b, out string message)
    {
        message = "";
        try
        {
            var infos = DeviceInformation.FindAllAsync(LampArray.GetDeviceSelector())
                .AsTask().GetAwaiter().GetResult();

            if (infos.Count == 0)
            {
                message = "系统未找到 LampArray 设备";
                return false;
            }

            bool any = false;
            foreach (var info in infos)
            {
                var lamp = LampArray.FromIdAsync(info.Id).AsTask().GetAwaiter().GetResult();
                if (lamp == null) continue;

                lamp.SetColor(WinColor.FromArgb(255, r, g, b));
                any = true;
                message += $"{info.Name}（{lamp.LampCount} 颗灯珠） ";
            }

            return any;
        }
        catch (Exception ex)
        {
            message = ex.Message;
            return false;
        }
    }
}
