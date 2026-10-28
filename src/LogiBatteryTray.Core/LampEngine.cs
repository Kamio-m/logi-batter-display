using System.Drawing;
using Windows.Devices.Enumeration;
using Windows.Devices.Lights;
using Windows.Devices.Lights.Effects;
using WinColor = Windows.UI.Color;

namespace LogiBatteryTray;

/// <summary>智能模式的数据来源。</summary>
internal enum SmartSource
{
    Battery = 0,
    EstimatedBattery = 1,
    Cpu = 2,
}

/// <summary>灯效模式（对应华硕 Aura 的基本灯效）。</summary>
internal enum LampMode
{
    Off = 0,
    Static = 1,
    Breathing = 2,
    Strobe = 3,
    ColorCycle = 4,
    Rainbow = 5,
    Starry = 6,
    Smart = 7,
}

/// <summary>
/// 用 Windows 官方灯效引擎驱动：呼吸/循环/星夜走 CustomEffect（系统按时回调推帧），
/// 闪烁走 BlinkEffect。时序交给系统调度器，避免频繁重启造成的闪烁。
/// </summary>
internal static class LampEngine
{
    private static readonly Random Rng = new();

    private static LampArray _lamp;
    private static LampArrayEffectPlaylist _playlist;

    public static LampMode Mode { get; private set; } = LampMode.Off;
    public static Color BaseColor { get; private set; } = Color.FromArgb(58, 130, 255);
    public static int Brightness { get; private set; } = 80;
    public static double Speed { get; private set; } = 1.0;
    public static string LastError { get; private set; } = "";

    /// <summary>灯效参数变化（供外层保存到设置）。</summary>
    public static event EventHandler Changed;

    /// <summary>供「智能」模式读取当前电量。</summary>
    public static Func<int?> BatteryPercentProvider { get; set; }

    /// <summary>智能模式的数据来源。</summary>
    public static SmartSource Source { get; private set; } = SmartSource.Battery;

    /// <summary>渐变第二个颜色（Aura 的「颜色配置」两色）。</summary>
    public static Color SecondColor { get; private set; } = Color.FromArgb(255, 45, 85);

    /// <summary>是否使用渐变（否则单色）。</summary>
    public static bool UseGradient { get; private set; }

    /// <summary>随机取色（呼吸/闪烁的「随机」勾选）。</summary>
    public static bool UseRandom { get; private set; }

    /// <summary>星夜：背景是否点亮。</summary>
    public static bool StarryBackground { get; private set; }

    /// <summary>星夜：效果随机取色。</summary>
    public static bool StarryRandom { get; private set; } = true;

    /// <summary>智能模式：低点/高点阈值（对应华硕 40/60 那种）。</summary>
    public static int SmartLow { get; private set; } = 40;
    public static int SmartHigh { get; private set; } = 60;
    public static Color SmartLowColor { get; private set; } = Color.FromArgb(52, 199, 110);
    public static Color SmartHighColor { get; private set; } = Color.FromArgb(255, 59, 48);

    public static void ConfigureOptions(Color? second = null, bool? gradient = null, bool? random = null,
        bool? starryBackground = null, bool? starryRandom = null,
        int? smartLow = null, int? smartHigh = null, Color? smartLowColor = null, Color? smartHighColor = null,
        SmartSource? source = null)
    {
        if (source is SmartSource src) Source = src;
        if (second is Color sc) SecondColor = sc;
        if (gradient is bool g) UseGradient = g;
        if (random is bool r) UseRandom = r;
        if (starryBackground is bool sb) StarryBackground = sb;
        if (starryRandom is bool sr) StarryRandom = sr;
        if (smartLow is int sl) SmartLow = Math.Clamp(sl, 0, 100);
        if (smartHigh is int sh) SmartHigh = Math.Clamp(sh, 0, 100);
        if (smartLowColor is Color slc) SmartLowColor = slc;
        if (smartHighColor is Color shc) SmartHighColor = shc;

        // 只有这些参数需要重启灯效；阈值/颜色由运行中的回调实时读取
        bool needsRebuild = second != null || gradient != null || random != null
                          || starryBackground != null || starryRandom != null || source != null;

        if (needsRebuild && Mode != LampMode.Off) Apply();
    }

    public static void Configure(LampMode mode, Color? color = null, int? brightness = null, double? speed = null)
    {
        Mode = mode;
        if (color is Color c) BaseColor = c;
        if (brightness is int b) Brightness = Math.Clamp(b, 1, 100);
        if (speed is double s) Speed = Math.Clamp(s, 0.25, 4.0);

        Apply();
        try { Changed?.Invoke(null, EventArgs.Empty); } catch { }
    }

    /// <summary>停止当前灯效（不改变 Mode，供内部重建使用）。</summary>
    private static void StopPlaylist()
    {
        try { _playlist?.Stop(); } catch { }
        _playlist = null;
    }

    /// <summary>对外停止灯效。</summary>
    /// <summary>重新接管灯效（切走窗口被系统抢回控制权时调用）。</summary>
    public static void Reapply()
    {
        if (Mode == LampMode.Off) return;
        Apply();
    }

    public static void Stop()
    {
        StopPlaylist();
        Mode = LampMode.Off;
    }

    /// <summary>设置系统级亮度（0-100），会同步到设备。</summary>
    public static bool SetBrightness(int percent)
    {
        try
        {
            var lamp = EnsureLamp();
            lamp.BrightnessLevel = Math.Clamp(percent, 0, 100) / 100.0;
            Brightness = Math.Clamp(percent, 1, 100);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>读取设备当前亮度（0-100），读不到返回 -1。</summary>
    public static int ReadBrightness()
    {
        try
        {
            var lamp = EnsureLamp();
            return (int)Math.Round(lamp.BrightnessLevel * 100);
        }
        catch
        {
            return -1;
        }
    }
    private static LampArray EnsureLamp()
    {
        if (_lamp != null) return _lamp;

        var infos = DeviceInformation.FindAllAsync(LampArray.GetDeviceSelector())
            .AsTask().GetAwaiter().GetResult();

        if (infos.Count == 0) throw new InvalidOperationException("未找到 LampArray 设备");

        _lamp = LampArray.FromIdAsync(infos[0].Id).AsTask().GetAwaiter().GetResult();
        return _lamp ?? throw new InvalidOperationException("无法打开 LampArray 设备");
    }

    private static void Apply()
    {
        try
        {
            var mode = Mode;
            StopPlaylist();

            if (mode == LampMode.Off)
            {
                // 用「黑色常亮」占住设备：单纯停止会让设备回落到厂商默认灯效
                try
                {
                    var darkLamp = EnsureLamp();
                    var darkIndexes = Enumerable.Range(0, Math.Max(1, darkLamp.LampCount)).ToArray();

                    _playlist = new LampArrayEffectPlaylist
                    {
                        RepetitionMode = LampArrayRepetitionMode.Forever,
                    };

                    _playlist.Append(new LampArraySolidEffect(darkLamp, darkIndexes)
                    {
                        Color = WinColor.FromArgb(255, 0, 0, 0),
                        Duration = TimeSpan.Zero,
                    });

                    _playlist.Start();
                }
                catch { }

                return;
            }

            var lamp = EnsureLamp();
            int[] indexes = Enumerable.Range(0, Math.Max(1, lamp.LampCount)).ToArray();

            _playlist = new LampArrayEffectPlaylist
            {
                RepetitionMode = LampArrayRepetitionMode.Forever,
            };

            switch (mode)
            {
                case LampMode.Static when !UseGradient:
                    _playlist.Append(new LampArraySolidEffect(lamp, indexes)
                    {
                        Color = Scale(BaseColor),
                        Duration = TimeSpan.Zero,
                    });
                    break;



                default:
                    _playlist.Append(BuildAnimated(lamp, indexes, mode));
                    break;
            }

            _playlist.Start();
            LastError = "";
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            _playlist = null;
        }
    }

    /// <summary>呼吸/循环/彩虹/星夜/智能：由系统按 UpdateInterval 回调推帧。</summary>
    private static LampArrayCustomEffect BuildAnimated(LampArray lamp, int[] indexes, LampMode mode)
    {
        var baseColor = BaseColor;
        var secondColor = SecondColor;
        bool useGradient = UseGradient, useRandom = UseRandom;
        int smartLow = SmartLow, smartHigh = SmartHigh;
        var smartLowColor = SmartLowColor;
        var smartHighColor = SmartHighColor;
        double speed = Speed;
        int brightness = Brightness;

        var effect = new LampArrayCustomEffect(lamp, indexes)
        {
            UpdateInterval = TimeSpan.FromMilliseconds(40),
            Duration = TimeSpan.FromDays(1),
        };

        long frame = 0;
        long lastRandomCycle = -1;
        Color randomColor = baseColor;
        byte starR = 0, starG = 0, starB = 0;

        effect.UpdateRequested += (_, args) =>
        {
            try
            {
            // 用自增帧计数推算时间：等间隔回调，不依赖 SinceStarted（部分设备恒为 0）
            double elapsed = frame++ * 0.04;
            Color color;

            switch (mode)
            {
                case LampMode.Static:
                {
                    // 单灯区：用缓慢的两色过渡来表现「渐变」
                    double wave2 = (Math.Sin(elapsed * 2 * Math.PI / (6.0 / speed)) + 1) / 2;
                    color = Blend(baseColor, secondColor, wave2);
                    break;
                }

                case LampMode.Breathing:
                {
                    double period = 3.0 / speed;
                    double wave = (Math.Sin(elapsed * 2 * Math.PI / period) + 1) / 2;
                    double factor = 0.15 + 0.85 * wave;

                    Color source = baseColor;
                    if (useRandom)
                    {
                        long cycle = (long)(elapsed / period);
                        if (cycle != lastRandomCycle)
                        {
                            lastRandomCycle = cycle;
                            randomColor = FromHsv(Rng.Next(0, 360), 0.9, 1.0);
                        }

                        source = randomColor;
                    }
                    else if (useGradient) source = Blend(baseColor, secondColor, wave);

                    color = Mix(source, factor);
                    break;
                }

                case LampMode.Strobe:
                {
                    // 亮 600ms / 灭 600ms，两端各 150ms 渐变
                    double period = 1.2 / speed;      // 秒
                    double phase = elapsed % period;
                    double half = period / 2;
                    double ramp = Math.Min(0.15, half / 3);

                    double factor;
                    if (phase < ramp) factor = phase / ramp;                       // 渐亮
                    else if (phase < half - ramp) factor = 1.0;                    // 保持亮
                    else if (phase < half) factor = (half - phase) / ramp;         // 渐暗
                    else factor = 0.0;                                             // 熄灭

                    Color strobeColor = baseColor;
                    if (useRandom)
                    {
                        long cycle = (long)(elapsed / period);
                        if (cycle != lastRandomCycle)
                        {
                            lastRandomCycle = cycle;
                            randomColor = FromHsv(Rng.Next(0, 360), 0.9, 1.0);
                        }

                        strobeColor = randomColor;
                    }
                    else if (useGradient) strobeColor = Blend(baseColor, secondColor, elapsed % 1.0);

                    color = Mix(strobeColor, Math.Clamp(factor, 0, 1));
                    break;
                }

                case LampMode.ColorCycle:
                {
                    double hue = elapsed * 60 * speed;
                    color = FromHsv(hue % 360, 1.0, 1.0);
                    break;
                }

                case LampMode.Rainbow:
                {
                    double hue = elapsed * 100 * speed;
                    color = FromHsv(hue % 360, 0.85, 1.0);
                    break;
                }

                case LampMode.Smart:
                {
                    // 实时读取，拖动阈值滑块时无需重启灯效
                    int percent = Source switch
                    {
                        SmartSource.Cpu => CpuUsage.Read(),
                        SmartSource.EstimatedBattery => BatteryPercentProvider?.Invoke() ?? 100,
                        _ => BatteryPercentProvider?.Invoke() ?? 100,
                    };
                    int low = SmartLow;
                    int high = SmartHigh;
                    double t2 = Math.Clamp((percent - low) / (double)Math.Max(1, high - low), 0, 1);
                    color = Blend(SmartLowColor, SmartHighColor, t2);
                    break;
                }

                default:
                    color = baseColor;
                    break;
            }

            double scale = brightness / 100.0;
                args.SetColor(WinColor.FromArgb(255,
                    (byte)Math.Clamp(Math.Round(color.R * scale), 0, 255),
                    (byte)Math.Clamp(Math.Round(color.G * scale), 0, 255),
                    (byte)Math.Clamp(Math.Round(color.B * scale), 0, 255)));
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
            }
        };

        return effect;
    }

    private static WinColor Scale(Color color) => WinColor.FromArgb(255, color.R, color.G, color.B);

    private static Color Blend(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromArgb(
            (byte)Math.Round(a.R + (b.R - a.R) * t),
            (byte)Math.Round(a.G + (b.G - a.G) * t),
            (byte)Math.Round(a.B + (b.B - a.B) * t));
    }

    private static Color Mix(Color color, double factor) => Color.FromArgb(
        (byte)Math.Clamp(Math.Round(color.R * factor), 0, 255),
        (byte)Math.Clamp(Math.Round(color.G * factor), 0, 255),
        (byte)Math.Clamp(Math.Round(color.B * factor), 0, 255));

    private static Color FromHsv(double h, double s, double v)
    {
        h = ((h % 360) + 360) % 360;
        double c = v * s;
        double x = c * (1 - Math.Abs((h / 60) % 2 - 1));
        double m = v - c;

        (double r, double g, double b) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };

        return Color.FromArgb(
            (int)Math.Round((r + m) * 255),
            (int)Math.Round((g + m) * 255),
            (int)Math.Round((b + m) * 255));
    }
}
