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
        Type[] types = { typeof(LampArraySolidEffect), typeof(LampArrayEffectPlaylist) };

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

    public static bool PlaySolid(byte r, byte g, byte b, out string message)
    {
        message = "";
        try
        {
            var infos = DeviceInformation.FindAllAsync(LampArray.GetDeviceSelector())
                .AsTask().GetAwaiter().GetResult();

            if (infos.Count == 0) { message = "系统未找到 LampArray 设备"; return false; }

            foreach (var info in infos)
            {
                var lamp = LampArray.FromIdAsync(info.Id).AsTask().GetAwaiter().GetResult();
                if (lamp == null) continue;

                StopAll();

                var playlist = new LampArrayEffectPlaylist
                {
                    RepetitionMode = LampArrayRepetitionMode.Forever,
                };

                var effect = new LampArraySolidEffect(lamp, Enumerable.Range(0, Math.Max(1, lamp.LampCount)).ToArray())
                {
                    Color = WinColor.FromArgb(255, r, g, b),
                    Duration = TimeSpan.FromMilliseconds(500),
                    CompletionBehavior = LampArrayEffectCompletionBehavior.KeepState,
                };

                playlist.Append(effect);
                playlist.Start();
                Playlists.Add(playlist);

                message += $"{info.Name}（{lamp.LampCount} 颗灯珠） ";
            }

            return message.Length > 0;
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
