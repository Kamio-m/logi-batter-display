using System.Text.Json;
using Microsoft.Win32;

namespace LogiBatteryTray;

/// <summary>用户设置，保存在 %APPDATA%\LogiBatteryTray\settings.json。</summary>
internal sealed class AppSettings
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "LogiBatteryTray";

    public int IntervalSeconds { get; set; } = 2;

    /// <summary>Manually tracked devices (battery estimated from charge date).</summary>
    public List<ManualBatteryDevice> ManualDevices { get; set; } = new();

    /// <summary>自定义颜色（十六进制，最多 8 个）。</summary>
    public List<string> CustomColors { get; set; } = new();

    /// <summary>配置版本，用于迁移旧设置。</summary>
    public int SettingsVersion { get; set; }
    public bool Autostart { get; set; }
    public bool LowBatteryNotify { get; set; } = true;
    public string LastDeviceName { get; set; } = "罗技鼠标";

    /// <summary>托盘图标显示哪台设备（path#name；"auto" = 电量最低）。</summary>
    public string SelectedDeviceKey { get; set; } = "";

    /// <summary>悬浮小卡上次拖动后的位置；int.MinValue 表示未设置（默认贴任务栏）。</summary>
    public int WidgetX { get; set; } = int.MinValue;
    public int WidgetY { get; set; } = int.MinValue;

    /// <summary>主题色序号（见 Theme.Accents）。</summary>
    /// <summary>主窗口尺寸（0 表示使用默认值）。</summary>
    public int WindowWidth { get; set; }
    public int WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }

    /// <summary>设备卡片的显示顺序（第一张即托盘显示设备）。</summary>
    public List<string> CardOrder { get; set; } = new();

    public int AccentIndex { get; set; }

    /// <summary>图标形状：0=圆环，1=电池。</summary>
    public int IconShapeMode { get; set; }

    /// <summary>无响应多少秒后判定为“停用”。</summary>
    public int OfflineAsOffSeconds { get; set; } = 0;

    /// <summary>菜单不透明度（40-100）。</summary>
    public int MenuOpacity { get; set; } = 100;

    /// <summary>菜单圆角半径。</summary>
    public int MenuCorner { get; set; } = 12;

    /// <summary>主题：0=跟随系统，1=深色，2=浅色。</summary>
    public int ThemeMode { get; set; }

    /// <summary>上次使用的灯效模式（0=关闭 1=恒亮 2=呼吸 3=闪烁 4=彩色循环 5=彩虹 6=星夜 7=智能）。</summary>
    public int LampEffectMode { get; set; }

    /// <summary>灯效颜色（#RRGGBB）。</summary>
    public string LampColor { get; set; } = "#3A82FF";

    public int LampBrightness { get; set; } = 80;

    public double LampSpeed { get; set; } = 1.0;

    /// <summary>图标风格（已固定为系统风格，保留字段兼容旧配置）。</summary>
    public int IconStyle { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public string LoadError { get; private set; }

    public static AppSettings Load()
    {
        var settings = new AppSettings();
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                string json = File.ReadAllText(AppPaths.SettingsFile);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json);
                if (loaded != null) settings = loaded;

                // 旧版本默认 5 分钟刷新，升级后改成实时（5 秒）
                if (settings.SettingsVersion < 3)
                {
                    settings.IntervalSeconds = 2;
                    settings.SettingsVersion = 3;
                    try { settings.Save(); } catch { }
                }
            }
        }
        catch (Exception ex)
        {
            settings.LoadError = ex.Message;
        }

        settings.IntervalSeconds = Math.Clamp(settings.IntervalSeconds, 1, 3600);
        return settings;
    }

    public void Save()
    {
        try
        {
            string json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(AppPaths.SettingsFile, json);
        }
        catch (Exception ex)
        {
            Log.Write("保存设置失败: " + ex.Message);
        }
    }

    // ---------- 包身份探测 ----------
    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern int GetCurrentPackageFamilyName(ref int length, System.Text.StringBuilder name);

    private static string PackageFamilyName()
    {
        try
        {
            int length = 0;
            GetCurrentPackageFamilyName(ref length, null);
            if (length <= 1) return null;

            var builder = new System.Text.StringBuilder(length);
            int result = GetCurrentPackageFamilyName(ref length, builder);
            return result == 0 ? builder.ToString() : null;
        }
        catch
        {
            return null;
        }
    }

    private static string StartupScriptPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Startup), "LogiBatteryTray.cmd");

    /// <summary>当前程序是否已登记开机启动（启动脚本或 Run 键任一存在）。</summary>
    public static bool IsAutostartEnabled()
    {
        try
        {
            if (File.Exists(StartupScriptPath)) return true;

            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
            return key?.GetValue(RunValueName) != null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 写入/删除自启项。打包应用（MSIX）无法用 Run 键直接启动（WindowsApps 路径受限），
    /// 改为在启动文件夹写一个 .cmd，通过 shell:AppsFolder + AUMID 拉起。
    /// </summary>
    public static bool SetAutostart(bool enabled)
    {
        try
        {
            string family = PackageFamilyName();

            if (enabled)
            {
                if (!string.IsNullOrEmpty(family))
                {
                    string aumid = family + "!LogiBatteryTray";
                    string content = "@echo off\r\nstart \"\" explorer.exe shell:AppsFolder\\" + aumid + "\r\n";
                    File.WriteAllText(StartupScriptPath, content, new System.Text.UTF8Encoding(false));
                    return true;
                }

                string exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe)) return false;

                using var runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, true);
                if (runKey == null) return false;
                runKey.SetValue(RunValueName, $"\"{exe}\" --silent");
                return true;
            }

            if (File.Exists(StartupScriptPath)) File.Delete(StartupScriptPath);

            using (var runKey = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
                runKey?.DeleteValue(RunValueName, false);

            return true;
        }
        catch (Exception ex)
        {
            Log.Write("设置开机启动失败: " + ex.Message);
            return false;
        }
    }
}
