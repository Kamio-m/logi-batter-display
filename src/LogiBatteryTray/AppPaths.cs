using System.Text;

namespace LogiBatteryTray;

/// <summary>设置与日志的存放位置。优先 %APPDATA%，不可写时退回程序目录。</summary>
internal static class AppPaths
{
    public static string Directory { get; } = ResolveDirectory();

    public static string SettingsFile => Path.Combine(Directory, "settings.json");

    public static string LogFile => Path.Combine(Directory, "log.txt");

    private static string ResolveDirectory()
    {
        string preferred = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "LogiBatteryTray");

        try
        {
            System.IO.Directory.CreateDirectory(preferred);
            string probe = Path.Combine(preferred, ".write-test");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return preferred;
        }
        catch
        {
            return AppContext.BaseDirectory;
        }
    }
}

/// <summary>极简日志，出错时才写入，避免频繁磁盘操作。</summary>
internal static class Log
{
    private static readonly object Gate = new();

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                string path = AppPaths.LogFile;
                var info = new FileInfo(path);
                if (info.Exists && info.Length > 512 * 1024)
                {
                    string backup = path + ".old";
                    File.Delete(backup);
                    File.Move(path, backup);
                }

                string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}";
                File.AppendAllText(path, line, Encoding.UTF8);
            }
        }
        catch
        {
            // 日志失败不影响主流程
        }
    }
}
