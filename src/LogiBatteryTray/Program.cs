using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Text;

namespace LogiBatteryTray;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        // 命令行/诊断模式
        if (args.Length > 0 && args[0].StartsWith("--"))
        {
            ConsoleHost.Attach();

            string outPath = null;
            int outIndex = Array.IndexOf(args, "--out");
            if (outIndex >= 0 && outIndex + 1 < args.Length) outPath = args[outIndex + 1];

            TextWriter fileWriter = null;
            if (outPath != null)
            {
                fileWriter = new StreamWriter(outPath, false, new UTF8Encoding(false)) { AutoFlush = true };
                Console.SetOut(new TeeWriter(Console.Out, fileWriter));
            }

            try
            {
                return Cli.Run(args);
            }
            catch (Exception ex)
            {
                Console.WriteLine("诊断模式异常: " + ex);
                return 2;
            }
            finally
            {
                fileWriter?.Dispose();
            }
        }

        using var mutex = new Mutex(true, @"Local\LogiBatteryTray.SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            MessageBox.Show("罗技鼠标电量已经在运行，请在任务栏通知区域查看。",
                "罗技鼠标电量", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 0;
        }

        ApplicationConfiguration.Initialize();

        Application.ThreadException += (_, e) =>
        {
            Log.Write("界面异常: " + e.Exception);
            MessageBox.Show("程序遇到错误：" + e.Exception.Message, "罗技鼠标电量",
                MessageBoxButtons.OK, MessageBoxIcon.Error);
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write("未处理异常: " + e.ExceptionObject);

        Log.Write($"启动 v{typeof(Program).Assembly.GetName().Version}，权限 {RuntimeProbe.IntegrityLevel()}，数据目录 {AppPaths.Directory}");

        try
        {
            Application.Run(new TrayApp());
        }
        catch (Exception ex)
        {
            Log.Write("启动失败: " + ex);
            MessageBox.Show("启动失败：" + ex.Message + "\n\n日志：" + AppPaths.LogFile,
                "罗技鼠标电量", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }

        return 0;
    }
}

/// <summary>同时写到控制台和文件。</summary>
internal sealed class TeeWriter : TextWriter
{
    private readonly TextWriter _first;
    private readonly TextWriter _second;

    public TeeWriter(TextWriter first, TextWriter second)
    {
        _first = first;
        _second = second;
    }

    public override Encoding Encoding => _first.Encoding;
    public override void Write(char value) { _first.Write(value); _second.Write(value); }
    public override void Write(string value) { _first.Write(value); _second.Write(value); }
    public override void WriteLine(string value) { _first.WriteLine(value); _second.WriteLine(value); }
    public override void Flush() { _first.Flush(); _second.Flush(); }
}

internal static class ConsoleHost
{
    /// <summary>WinExe 没有自带控制台，从父进程借用或新建一个。</summary>
    public static void Attach()
    {
        if (Native.GetStdHandle(Native.STD_OUTPUT_HANDLE) == IntPtr.Zero)
        {
            if (!Native.AttachConsole(0xFFFFFFFF)) Native.AllocConsole();
        }

        var stdout = Console.OpenStandardOutput();
        Console.SetOut(new StreamWriter(stdout) { AutoFlush = true });
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch
        {
            // 某些终端不支持改编码，忽略
        }
    }
}

internal static class Cli
{
    public static int Run(string[] args)
    {
        switch (args[0])
        {
            case "--once": return Once();
            case "--features": return Features();
            case "--iconpreview": return IconPreview(args.Length > 1 ? args[1] : "icon-preview.png");
            case "--iconstyles": return IconStyles(args.Length > 1 ? args[1] : "icon-styles.png");
            case "--iconvariants": return IconVariants(args.Length > 1 ? args[1] : "icon-variants.png");
            case "--dump": return DumpFeature(args[1]);
            case "--menu-preview": return MenuPreview(args.Length > 1 ? args[1] : "menu.png");
            case "--ping": return PingIndex(args.Length > 1 ? args[1] : "1");
            case "--popup-preview": return PopupPreview(args.Length > 1 ? args[1] : "popup.png");
            case "--verify-tray": return VerifyTray();
            default:
                Console.WriteLine("用法: LogiBatteryTray.exe [--once | --features | --iconpreview <png> | --popup-preview <png> | --verify-tray]");
                return 0;
        }
    }

    /// <summary>读一次电量并打印。</summary>
    private static int Once()
    {
        var service = new BatteryService();
        bool ok = service.Refresh();

        foreach (var device in service.Devices)
        {
            string battery = device.Online && device.Battery != null
                ? device.Battery.ToString()
                : "离线 " + device.Problem;
            Console.WriteLine($"{device.Name}  idx=0x{device.DeviceIndex:X2}  协议 {device.ProtocolVersion:F1}  {battery}");
        }

        if (!ok)
        {
            Console.WriteLine("读取失败: " + (service.Problem ?? "未知原因"));
            return 1;
        }
        return 0;
    }

    /// <summary>枚举设备的全部 HID++ 2.0 特性（排查兼容性时使用）。</summary>
    private static int Features()
    {
        var hid = HidDevice.Enumerate().FirstOrDefault(d =>
            d.VendorId == 0x046D && d.OutputReportLength >= 20 && d.CanWrite &&
            (d.UsagePage == 0xFF00 || d.UsagePage == 0xFF43));

        if (hid == null)
        {
            Console.WriteLine("没有找到罗技长报文接口");
            return 1;
        }

        foreach (byte index in new byte[] { 1, 2, 3, 4, 5, 6, 0xFF })
        {
            var device = new HidppDevice(hid, index);
            if (!device.TryGetProtocolVersion(out double version)) continue;

            Console.WriteLine($"=== 设备 0x{index:X2}  HID++ {version:F1} ===");
            device.EnumerateFeatures();
            foreach (var feature in device.Features)
                Console.WriteLine($"  0x{feature.Id:X4}  index=0x{feature.Index:X2}  type={feature.Type}  ver={feature.Version}");

            Console.WriteLine("  名称: " + (device.ReadDeviceName() ?? "(未知)"));
            var battery = device.ReadBattery();
            Console.WriteLine("  电量: " + (battery?.ToString() ?? "读取失败 " + device.LastError));
            Console.WriteLine();
        }

        hid.Dispose();
        return 0;
    }

    /// <summary>预览不同电量状态下的图标（正常 / 低电 / 充电）。</summary>
    private static int IconVariants(string path)
    {
        var variants = new (string Name, BatteryReading Battery)[]
        {
            ("85% 正常", new BatteryReading { Percent = 85 }),
            ("15% 低电（内胆变黄）", new BatteryReading { Percent = 15 }),
            ("60% 充电中", new BatteryReading { Percent = 60, Charging = true }),
            ("百分比模式 85%", new BatteryReading { Percent = 85 }),
        };
        var sizes = new[] { 16, 24, 32 };
        const int zoom = 6;
        const int cell = 176;
        const int labelWidth = 230;
        const int rowHeight = 150;

        int width = labelWidth + cell * sizes.Length;
        int height = rowHeight * variants.Length * 2;

        using var sheet = new Bitmap(width, height);
        using var g = Graphics.FromImage(sheet);
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        for (int theme = 0; theme < 2; theme++)
        {
            bool light = theme == 1;
            for (int row = 0; row < variants.Length; row++)
            {
                int y = (theme * variants.Length + row) * rowHeight;
                var back = light ? Color.FromArgb(243, 243, 246) : Color.FromArgb(32, 32, 34);
                var fore = light ? Color.FromArgb(30, 30, 32) : Color.FromArgb(240, 240, 245);

                using (var bg = new SolidBrush(back))
                    g.FillRectangle(bg, 0, y, width, rowHeight);

                using (var font = new Font("Segoe UI", 10.5f, FontStyle.Regular, GraphicsUnit.Point))
                using (var brush = new SolidBrush(fore))
                    g.DrawString($"{variants[row].Name}\n{(light ? "浅色任务栏" : "深色任务栏")}", font, brush,
                        new RectangleF(12, y + 24, labelWidth - 16, rowHeight - 30));

                for (int c = 0; c < sizes.Length; c++)
                {
                    IconFactory.Style = BatteryIconStyle.OutlinePlug;
                    bool percentMode = variants[row].Name.StartsWith("百分比模式");
                    using var icon = IconFactory.Create(sizes[c], variants[row].Battery, percentMode, light);
                    using var bitmap = icon.ToBitmap();
                    int drawSize = sizes[c] * zoom;
                    int x = labelWidth + c * cell + (cell - drawSize) / 2;
                    int top = y + (rowHeight - drawSize) / 2;

                    using (var back2 = new SolidBrush(back))
                        g.FillRectangle(back2, x - 6, top - 6, drawSize + 12, drawSize + 12);

                    g.InterpolationMode = InterpolationMode.NearestNeighbor;
                    g.PixelOffsetMode = PixelOffsetMode.Half;
                    g.DrawImage(bitmap, new Rectangle(x, top, drawSize, drawSize));

                    using var small = new Font("Segoe UI", 8.5f, FontStyle.Regular, GraphicsUnit.Point);
                    using var smallBrush = new SolidBrush(fore);
                    g.DrawString($"{sizes[c]}px", small, smallBrush, x, y + rowHeight - 22);
                }
            }
        }

        sheet.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        Console.WriteLine("已生成 " + path);
        return 0;
    }
    /// <summary>向指定设备索引发一次请求，打印应答/错误码（排查用）。</summary>
    private static int PingIndex(string indexText)
    {
        byte index = byte.Parse(indexText);
        var hid = HidDevice.Enumerate().FirstOrDefault(d =>
            d.VendorId == 0x046D && d.OutputReportLength >= 20 && d.CanWrite &&
            (d.UsagePage == 0xFF00 || d.UsagePage == 0xFF43));
        if (hid == null) { Console.WriteLine("没有找到接口"); return 1; }

        var device = new HidppDevice(hid, index);
        bool ok = device.TryGetProtocolVersion(out double version);
        Console.WriteLine($"索引 {index}: 协议={(ok ? version.ToString("F1") : "无")}  错误=\"{device.LastError}\"");
        if (ok)
        {
            var payload = device.Call(0x06, 0, 1500);   // 电池特性 0x1004 的 index 在不同设备不同，这里只测通路
            Console.WriteLine($"  电池特性 index={payload?.Length}  错误=\"{device.LastError}\"");
        }
        hid.Dispose();
        return 0;
    }
    /// <summary>把右键菜单渲染成图片，检查圆角/勾选/配色。</summary>
    private static int MenuPreview(string path)
    {
        using var menu = new ContextMenuStrip { ShowImageMargin = false, ShowCheckMargin = true };
        menu.Items.Add(new ToolStripMenuItem("PRO X Wireless   97%   充电中 · 约 +8%/小时") { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("刷新"));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("图标显示百分比") { Checked = true });
        menu.Items.Add(new ToolStripMenuItem("低电量提醒") { Checked = true });
        menu.Items.Add(new ToolStripMenuItem("开机自动启动") { Checked = true });
        var interval = new ToolStripMenuItem("刷新间隔");
        interval.DropDownItems.Add(new ToolStripMenuItem("5 分钟") { Checked = true });
        interval.DropDownItems.Add(new ToolStripMenuItem("15 分钟"));
        menu.Items.Add(interval);
        var personalize = new ToolStripMenuItem("个性化");
        personalize.DropDownItems.Add(new ToolStripMenuItem("主题"));
        personalize.DropDownItems.Add(new ToolStripMenuItem("不透明度"));
        personalize.DropDownItems.Add(new ToolStripMenuItem("圆角"));
        menu.Items.Add(personalize);

        var renderer = MenuStyle.CreateRenderer(false, 12);
        menu.Renderer = renderer;
        menu.Padding = new Padding(6, 6, 6, 6);

        menu.Show(new Point(100, 100));
        Application.DoEvents();
        Thread.Sleep(150);
        Application.DoEvents();

        using var bmp = new Bitmap(menu.Width, menu.Height);
        menu.DrawToBitmap(bmp, new Rectangle(0, 0, menu.Width, menu.Height));
        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        menu.Close();

        Console.WriteLine($"已生成 {path}（{menu.Width}x{menu.Height}）");
        return 0;
    }
    /// <summary>打印某个特性的功能 0/1 原始应答（排查用）。</summary>
    private static int DumpFeature(string hex)
    {
        ushort id = Convert.ToUInt16(hex.Replace("0x", ""), 16);
        var hid = HidDevice.Enumerate().FirstOrDefault(d =>
            d.VendorId == 0x046D && d.OutputReportLength >= 20 && d.CanWrite &&
            (d.UsagePage == 0xFF00 || d.UsagePage == 0xFF43));
        if (hid == null) { Console.WriteLine("没有找到接口"); return 1; }

        var device = new HidppDevice(hid, 1);
        if (!device.TryGetProtocolVersion(out double version)) { Console.WriteLine("设备无响应"); return 1; }
        byte index = device.GetFeatureIndex(id);
        Console.WriteLine($"特性 0x{id:X4} -> index 0x{index:X2} (HID++ {version:F1})");
        if (index == 0) { Console.WriteLine("不支持"); return 0; }

        foreach (byte function in new byte[] { 0, 1, 2, 3 })
        {
            var payload = device.Call(index, function, 1200);
            Console.WriteLine($"  功能 {function}: " + (payload == null ? "无应答 " + device.LastError : BitConverter.ToString(payload)));
        }
        Console.WriteLine("  名称: " + (device.ReadDeviceName() ?? "?"));
        hid.Dispose();
        return 0;
    }
    /// <summary>生成三套图标风格的对比图。</summary>
    private static int IconStyles(string path)
    {
        var styles = new (BatteryIconStyle Style, string Name)[]
        {
            (BatteryIconStyle.OutlinePlug, "A 描边 + 插头"),
            (BatteryIconStyle.OutlineBolt, "B 描边 + 闪电"),
            (BatteryIconStyle.FilledCapsule, "C 实心 + 数字"),
        };
        var samples = new (int Size, string Label, BatteryReading Battery)[]
        {
            (16, "85% · 16px", new BatteryReading { Percent = 85 }),
            (24, "85% · 24px", new BatteryReading { Percent = 85 }),
            (32, "85% · 32px", new BatteryReading { Percent = 85 }),
            (24, "充电 · 24px", new BatteryReading { Percent = 60, Charging = true }),
            (24, "12% · 24px", new BatteryReading { Percent = 12 }),
        };

        const int scale = 4;
        const int cell = 96;
        const int labelWidth = 150;
        int rowHeight = 110;
        int width = labelWidth + cell * samples.Length;
        int height = rowHeight * styles.Length * 2;

        using var sheet = new Bitmap(width, height);
        using var g = Graphics.FromImage(sheet);
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        for (int theme = 0; theme < 2; theme++)
        {
            bool light = theme == 1;
            for (int row = 0; row < styles.Length; row++)
            {
                int y = (theme * styles.Length + row) * rowHeight;
                var back = light ? Color.FromArgb(243, 243, 246) : Color.FromArgb(32, 32, 34);
                var fore = light ? Color.FromArgb(30, 30, 32) : Color.FromArgb(240, 240, 245);

                using (var bg = new SolidBrush(back))
                    g.FillRectangle(bg, 0, y, width, rowHeight);

                using (var font = new Font("Segoe UI", 11f, FontStyle.Regular, GraphicsUnit.Point))
                using (var brush = new SolidBrush(fore))
                    g.DrawString($"{styles[row].Name}\n{(light ? "浅色任务栏" : "深色任务栏")}", font, brush,
                        new RectangleF(12, y + 18, labelWidth - 16, rowHeight - 24));

                for (int c = 0; c < samples.Length; c++)
                {
                    IconFactory.Style = styles[row].Style;
                    using var icon = IconFactory.Create(samples[c].Size, samples[c].Battery, true, light);
                    using var bitmap = icon.ToBitmap();
                    int drawSize = samples[c].Size * scale;
                    int x = labelWidth + c * cell + (cell - drawSize) / 2;
                    int top = y + (rowHeight - drawSize) / 2;
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.DrawImage(bitmap, new Rectangle(x, top, drawSize, drawSize));

                    using var small = new Font("Segoe UI", 8.5f, FontStyle.Regular, GraphicsUnit.Point);
                    using var smallBrush = new SolidBrush(fore);
                    g.DrawString(samples[c].Label, small, smallBrush, labelWidth + c * cell + 8, y + rowHeight - 22);
                }
            }
        }

        sheet.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        IconFactory.Style = BatteryIconStyle.OutlinePlug;
        Console.WriteLine("已生成 " + path);
        return 0;
    }

    /// <summary>生成图标预览图，便于检查不同尺寸下的可读性。</summary>
    private static int IconPreview(string path)
    {
        var cases = new (string Label, BatteryReading Battery)[]
        {
            ("85% 使用中", new BatteryReading { Percent = 85, Source = "demo" }),
            ("15% 充电中", new BatteryReading { Percent = 15, Charging = true, Source = "demo" }),
            ("8% 低电量", new BatteryReading { Percent = 8, Source = "demo" }),
            ("未知", null),
        };
        int[] sizes = { 16, 20, 24, 32 };
        const int scale = 6;

        int cellWidth = 40 * scale;
        int cellHeight = 44 * scale;
        int width = cellWidth * cases.Length;
        int height = cellHeight * sizes.Length;

        using var sheet = new Bitmap(width, height);
        using var graphics = Graphics.FromImage(sheet);
        graphics.Clear(Color.FromArgb(255, 32, 32, 34));
        graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
        graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;

        for (int row = 0; row < sizes.Length; row++)
        {
            for (int column = 0; column < cases.Length; column++)
            {
                using var icon = IconFactory.Create(sizes[row], cases[column].Battery, true, false);
                using var cell = new Bitmap(cellWidth, cellHeight);
                using (var cellGraphics = Graphics.FromImage(cell))
                {
                    cellGraphics.Clear(Color.FromArgb(255, 32, 32, 34));
                    cellGraphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    cellGraphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    using var iconBitmap = icon.ToBitmap();
                    int drawSize = sizes[row] * scale;
                    cellGraphics.DrawImage(iconBitmap,
                        new Rectangle((cellWidth - drawSize) / 2, 4 * scale, drawSize, drawSize));
                }
                graphics.DrawImage(cell, new Point(column * cellWidth, row * cellHeight));
            }
        }

        sheet.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        Console.WriteLine("已生成 " + path);
        return 0;
    }

    /// <summary>把真实数据渲染到弹出面板并保存为图片。</summary>
    private static int PopupPreview(string path)
    {
        var service = new BatteryService();
        service.Refresh();

        using var form = new DetailsForm(service, () => { });
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new Point(-3000, -3000);
        form.Show();
        Application.DoEvents();

        using var bitmap = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(bitmap, new Rectangle(0, 0, form.Width, form.Height));
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        form.Close();

        Console.WriteLine($"已生成 {path}（{form.Width}x{form.Height}）");
        return 0;
    }

    /// <summary>创建一个真实托盘图标，并向系统查询它的屏幕矩形。</summary>
    private static int VerifyTray()
    {
        using var holder = new Form
        {
            ShowInTaskbar = false,
            FormBorderStyle = FormBorderStyle.None,
            StartPosition = FormStartPosition.Manual,
            Size = new Size(1, 1),
            Location = new Point(-3000, -3000),
        };
        holder.Show();

        using var icon = new NotifyIcon
        {
            Visible = true,
            Text = "LogiBatteryTray 托盘测试",
            Icon = IconFactory.Create(32, new BatteryReading { Percent = 85 }, true, false),
        };

        var until = DateTime.Now.AddSeconds(2);
        while (DateTime.Now < until)
        {
            Application.DoEvents();
            Thread.Sleep(30);
        }

        var fields = typeof(NotifyIcon).GetFields(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var windowField = fields.FirstOrDefault(f => f.Name == "_window");
        var idField = fields.FirstOrDefault(f => f.Name == "_id");

        if (windowField?.GetValue(icon) is not NativeWindow window || idField == null)
        {
            Console.WriteLine("无法读取 NotifyIcon 内部句柄");
            return 1;
        }

        uint id = Convert.ToUInt32(idField.GetValue(icon));
        var identifier = new NOTIFYICONIDENTIFIER
        {
            cbSize = Marshal.SizeOf<NOTIFYICONIDENTIFIER>(),
            hWnd = window.Handle,
            uID = id,
        };

        int result = Shell_NotifyIconGetRect(ref identifier, out RECT rect);
        Console.WriteLine($"图标句柄=0x{identifier.hWnd:X} id={id}");
        Console.WriteLine($"Shell_NotifyIconGetRect = 0x{result:X8}");
        Console.WriteLine($"托盘图标矩形: ({rect.Left},{rect.Top}) - ({rect.Right},{rect.Bottom})  " +
                          $"尺寸 {rect.Right - rect.Left}x{rect.Bottom - rect.Top}");

        icon.Visible = false;
        holder.Close();
        return result == 0 ? 0 : 1;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NOTIFYICONIDENTIFIER
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public Guid guidItem;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("shell32.dll", SetLastError = true)]
    private static extern int Shell_NotifyIconGetRect(ref NOTIFYICONIDENTIFIER identifier, out RECT rect);
}
