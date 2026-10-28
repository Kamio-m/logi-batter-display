using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Security.Principal;
using System.Windows.Forms;

namespace LogiBatteryTray.Setup;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        try
        {
            if (!IsAdmin())
            {
                RelaunchElevated();
                return;
            }

            string temp = Path.Combine(Path.GetTempPath(), "LogiBatteryTraySetup");
            Directory.CreateDirectory(temp);

            string msix = Path.Combine(temp, "LogiBatteryTray.msix");
            string cer = Path.Combine(temp, "LogiBatteryTray.cer");

            Extract("app.msix", msix);
            Extract("app.cer", cer);

            // 导入证书（根 + 受信任人）
            Run("certutil", $"-addstore -f Root \"{cer}\"");
            Run("certutil", $"-addstore -f TrustedPeople \"{cer}\"");

            // 安装
            int code = Run("powershell", $"-NoProfile -ExecutionPolicy Bypass -Command \"Add-AppxPackage -Path '{msix}' -ForceApplicationShutdown\"");

            if (code != 0)
            {
                MessageBox.Show("安装失败，错误码 " + code + "。\n请尝试手动安装 msix。", "LogiBatteryTray",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            MessageBox.Show("安装完成！\n\n· 开机自启已内置，无需额外设置\n· 开始菜单搜索 LogiBatteryTray 启动",
                "LogiBatteryTray", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show("安装程序出错：" + ex.Message, "LogiBatteryTray",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private static bool IsAdmin()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var principal = new WindowsPrincipal(identity);
        return principal.IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static void RelaunchElevated()
    {
        var info = new ProcessStartInfo(Environment.ProcessPath)
        {
            UseShellExecute = true,
            Verb = "runas",
        };

        try { Process.Start(info); }
        catch { MessageBox.Show("需要管理员权限才能安装。", "LogiBatteryTray", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }

    private static void Extract(string resourceName, string path)
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException("缺少资源: " + resourceName);
        using var file = File.Create(path);
        stream.CopyTo(file);
    }

    private static int Run(string exe, string arguments)
    {
        var info = new ProcessStartInfo(exe, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        using var process = Process.Start(info);
        process.WaitForExit();
        return process.ExitCode;
    }
}