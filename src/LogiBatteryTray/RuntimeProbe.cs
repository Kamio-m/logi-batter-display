using System.Reflection;
using System.Runtime.InteropServices;

namespace LogiBatteryTray;

/// <summary>运行时诊断：权限级别与托盘图标是否真的注册到系统。</summary>
internal static class RuntimeProbe
{
    private const int TokenQuery = 0x0008;
    private const int TokenIntegrityLevel = 25;

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, int access, out IntPtr token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr token, int infoClass, IntPtr buffer, int length, out int returnLength);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool ConvertSidToStringSid(IntPtr sid, out IntPtr stringSid);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct NotifyIconIdentifier
    {
        public int Size;
        public IntPtr Window;
        public uint Id;
        public Guid Guid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("shell32.dll", SetLastError = true)]
    private static extern int Shell_NotifyIconGetRect(ref NotifyIconIdentifier identifier, out Rect rect);

    /// <summary>返回当前进程的完整性级别（低完整性进程无法向任务栏注册图标）。</summary>
    public static string IntegrityLevel()
    {
        IntPtr token;
        if (!OpenProcessToken(GetCurrentProcess(), TokenQuery, out token)) return "未知";

        IntPtr buffer = Marshal.AllocHGlobal(256);
        try
        {
            if (!GetTokenInformation(token, TokenIntegrityLevel, buffer, 256, out _)) return "未知";

            IntPtr sid = Marshal.ReadIntPtr(buffer);
            if (!ConvertSidToStringSid(sid, out IntPtr sidText)) return "未知";

            string text = Marshal.PtrToStringUni(sidText) ?? "";
            LocalFree(sidText);

            return text switch
            {
                _ when text.EndsWith("-4096") => "低完整性（受限）",
                _ when text.EndsWith("-8192") => "普通",
                _ when text.EndsWith("-12288") => "管理员",
                _ when text.EndsWith("-16384") => "系统",
                _ => text,
            };
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            CloseHandle(token);
        }
    }

    /// <summary>向系统查询图标在托盘中的矩形；成功说明图标确实出现在通知区域。</summary>
    public static string TrayState(NotifyIcon icon)
    {
        try
        {
            var windowField = typeof(NotifyIcon).GetField("_window",
                BindingFlags.NonPublic | BindingFlags.Instance);
            var idField = typeof(NotifyIcon).GetField("_id",
                BindingFlags.NonPublic | BindingFlags.Instance);

            if (windowField?.GetValue(icon) is not NativeWindow window || idField == null)
                return "无法读取图标句柄";

            var identifier = new NotifyIconIdentifier
            {
                Size = Marshal.SizeOf<NotifyIconIdentifier>(),
                Window = window.Handle,
                Id = Convert.ToUInt32(idField.GetValue(icon)),
            };

            int result = Shell_NotifyIconGetRect(ref identifier, out Rect rect);
            return result == 0
                ? $"已注册，位置 ({rect.Left},{rect.Top})-({rect.Right},{rect.Bottom})"
                : $"注册失败 0x{result:X8}";
        }
        catch (Exception ex)
        {
            return "检查失败: " + ex.Message;
        }
    }
}
