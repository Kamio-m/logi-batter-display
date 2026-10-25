using System.Text.Json;
using Microsoft.Win32;

namespace LogiBatteryTray;

/// <summary>用户设置，保存在 %APPDATA%\LogiBatteryTray\settings.json。</summary>
internal sealed class AppSettings
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "LogiBatteryTray";

    public int IntervalSeconds { get; set; } = 2;

    /// <summary>配置版本，用于迁移旧设置。</summary>
    public int SettingsVersion { get; set; }
    public bool ShowPercentInIcon { get; set; }
    public bool Autostart { get; set; }
    public bool LowBatteryNotify { get; set; } = true;
    public string LastDeviceName { get; set; } = "罗技鼠标";

    /// <summary>悬浮小卡上次拖动后的位置；int.MinValue 表示未设置（默认贴任务栏）。</summary>
    public int WidgetX { get; set; } = int.MinValue;
    public int WidgetY { get; set; } = int.MinValue;

    /// <summary>图标形状：0=圆环，1=电池。</summary>
    public int IconShapeMode { get; set; }

    /// <summary>无响应多少秒后判定为“停用”。</summary>
    public int OfflineAsOffSeconds { get; set; } = 120;

    /// <summary>菜单不透明度（40-100）。</summary>
    public int MenuOpacity { get; set; } = 100;

    /// <summary>菜单圆角半径。</summary>
    public int MenuCorner { get; set; } = 12;

    /// <summary>主题：0=跟随系统，1=深色，2=浅色。</summary>
    public int ThemeMode { get; set; }

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

    /// <summary>当前程序是否已登记开机启动。</summary>
    public static bool IsAutostartEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
            return key?.GetValue(RunValueName) != null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>写入/删除 HKCU Run 项。</summary>
    public static bool SetAutostart(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true);
            if (key == null) return false;

            if (enabled)
            {
                string exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe)) return false;
                key.SetValue(RunValueName, $"\"{exe}\"");
            }
            else
            {
                key.DeleteValue(RunValueName, false);
            }
            return true;
        }
        catch (Exception ex)
        {
            Log.Write("设置开机启动失败: " + ex.Message);
            return false;
        }
    }
}
