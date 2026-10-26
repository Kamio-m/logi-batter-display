using System.Runtime.InteropServices;

namespace LogiBatteryTray;

/// <summary>
/// 通用 HID 电量探测：用 HidP_GetValueCaps 枚举设备所有「值」用法，
/// 找标准电池用量（Page 0x06 Usage 0x20）和电池系统页（0x84），
/// 顺便列出厂商自定义页面，判断非罗技设备能否读电量。
/// </summary>
internal static class HidScan
{
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;

    private const int Input = 0;
    private const int Feature = 2;

    [DllImport("hid.dll")]
    private static extern int HidP_GetValueCaps(int reportType, [Out] ValueCaps[] valueCaps, ref int length, IntPtr preparsedData);

    [DllImport("hid.dll")]
    private static extern int HidP_GetUsageValue(int reportType, ushort usagePage, ushort linkCollection,
        ushort usage, out uint usageValue, IntPtr preparsedData, byte[] report, int reportLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct ValueCaps
    {
        public ushort UsagePage;
        public byte ReportId;
        public byte IsAlias;
        public ushort BitField;
        public ushort LinkCollection;
        public ushort LinkUsage;
        public ushort LinkUsagePage;
        public byte IsRange;
        public byte IsStringRange;
        public byte IsDesignatorRange;
        public byte IsAbsolute;
        public byte HasNull;
        public byte Reserved;
        public ushort BitSize;
        public ushort ReportCount;
        public ushort R1, R2, R3, R4, R5;
        public ushort UsageMin;
        public ushort UsageMax;
        public ushort S1, S2, D1, D2, I1, I2;
    }

    /// <summary>抓取指定设备的输入报告，用来分析厂商私有协议。</summary>
    public static int RunSniff(string[] args)
    {
        string filter = args.Length > 1 ? args[1] : "";
        int seconds = args.Length > 2 && int.TryParse(args[2], out int s) ? s : 10;

        var devices = HidDevice.Enumerate()
            .Where(d => d.Path.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .ToList();

        Console.WriteLine($"开始抓取 {seconds} 秒，匹配 {devices.Count} 个接口");
        foreach (var d in devices)
            Console.WriteLine($"  - {d.Product} {d.VendorId:X4}:{d.ProductId:X4} usage {d.UsagePage:X4}:{d.Usage:X4} in={d.InputReportLength}");

        var deadline = DateTime.Now.AddSeconds(seconds);
        var buffer = new System.Text.StringBuilder();

        while (DateTime.Now < deadline)
        {
            foreach (var device in devices)
            {
                if (device.InputReportLength <= 0) continue;

                byte[] report = device.Read(150);
                if (report == null || report.Length == 0) continue;

                string hex = BitConverter.ToString(report.Take(Math.Min(report.Length, 24)).ToArray());
                string line = $"{DateTime.Now:HH:mm:ss.fff}  {device.VendorId:X4}:{device.ProductId:X4} u{device.UsagePage:X4}:{device.Usage:X4}  {hex}";
                Console.WriteLine(line);
                buffer.AppendLine(line);
            }
        }

        Console.WriteLine($"抓取结束，共 {buffer.ToString().Split('\n').Length - 1} 条报告");
        return 0;
    }
    /// <summary>向指定接口发送一份输出报告并等待回包（用于验证厂商私有协议）。</summary>
    public static int RunWrite(string[] args)
    {
        string filter = args.Length > 1 ? args[1] : "";
        string hex = args.Length > 2 ? args[2] : "00-12-07";
        int readMs = args.Length > 3 && int.TryParse(args[3], out int ms) ? ms : 800;

        byte[] packet = hex.Split('-', ',')
            .Where(s => s.Length > 0)
            .Select(s => Convert.ToByte(s, 16))
            .ToArray();

        Console.WriteLine($"发送: {BitConverter.ToString(packet)}  等待 {readMs}ms");

        foreach (var device in HidDevice.Enumerate().Where(d => d.Path.Contains(filter, StringComparison.OrdinalIgnoreCase)))
        {
            Console.WriteLine($"→ {device.Product} u{device.UsagePage:X4}:{device.Usage:X4} in={device.InputReportLength} canWrite={device.CanWrite}");

            byte[] full = new byte[Math.Max(packet.Length, device.InputReportLength)];
            Array.Copy(packet, full, packet.Length);

            bool ok = device.Write(full, 800);
            Console.WriteLine($"   写入: {(ok ? "成功" : "失败")}");

            byte[] response = device.Read(readMs);
            Console.WriteLine($"   回包: {(response == null ? "无" : BitConverter.ToString(response))}");
        }

        return 0;
    }
    // ---------- Raw Input 旁路抓包 ----------
    private const int WmInput = 0x00FF;
    private const uint RidInput = 0x10000003;
    private const uint RidevInputSink = 0x00000100;
    private const uint RidevPageOnly = 0x00000020;

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTHEADER
    {
        public uint dwType;
        public uint dwSize;
        public IntPtr hDevice;
        public IntPtr wParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDevice
    {
        public ushort UsagePage;
        public ushort Usage;
        public uint Flags;
        public IntPtr Target;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(RawInputDevice[] devices, uint count, uint size);

    [DllImport("user32.dll")]
    private static extern uint GetRawInputData(IntPtr rawInput, uint command, IntPtr data, ref uint size, uint headerSize);

    private sealed class SniffForm : Form
    {
        public readonly List<string> Lines = new();
        public int Limit = 200;

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmInput)
            {
                uint size = 0;
                uint headerSize = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
                GetRawInputData(m.LParam, RidInput, IntPtr.Zero, ref size, headerSize);
                if (size > 0)
                {
                    IntPtr buffer = Marshal.AllocHGlobal((int)size);
                    try
                    {
                        if (GetRawInputData(m.LParam, RidInput, buffer, ref size, headerSize) == size)
                        {
                            var header = Marshal.PtrToStructure<RAWINPUTHEADER>(buffer);
                            int offset = (int)headerSize;
                            int dataSize = (int)size - offset;
                            if (dataSize > 0 && dataSize <= 128 && Lines.Count < Limit)
                            {
                                var data = new byte[dataSize];
                                Marshal.Copy(buffer + offset, data, 0, dataSize);
                                Lines.Add($"{DateTime.Now:HH:mm:ss.fff}  type={header.dwType}  {BitConverter.ToString(data)}");
                            }
                        }
                    }
                    finally { Marshal.FreeHGlobal(buffer); }
                }
            }

            base.WndProc(ref m);
        }
    }

    public static int RunRaw(string[] args)
    {
        int seconds = args.Length > 1 && int.TryParse(args[1], out int s) ? s : 20;

        var form = new SniffForm
        {
            Text = "raw",
            ShowInTaskbar = false,
            Opacity = 0,
            Size = new Size(1, 1),
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-3000, -3000),
        };

        form.Show();
        Application.DoEvents();

        var usages = new (ushort Page, ushort Usage, uint Flags)[]
        {
            (0x01, 0x02, RidevInputSink),            // 标准鼠标，验证用
            (0xFFB5, 0x0001, RidevInputSink),        // 华硕鼠标私有
            (0xFF02, 0x0002, RidevInputSink),
            (0xFFBC, 0x0088, RidevInputSink),
            (0xFF01, 0x0000, RidevInputSink | RidevPageOnly),   // 键盘私有整页
            (0xFF59, 0x0000, RidevInputSink | RidevPageOnly),
        };

        var devices = usages.Select(u => new RawInputDevice
        {
            UsagePage = u.Page,
            Usage = u.Usage,
            Flags = u.Flags,
            Target = form.Handle,
        }).ToArray();

        bool ok = RegisterRawInputDevices(devices, (uint)devices.Length, (uint)Marshal.SizeOf<RawInputDevice>());
        Console.WriteLine($"注册 Raw Input: {(ok ? "成功" : "失败 " + Marshal.GetLastWin32Error())}");
        Console.WriteLine($"目标窗口: 0x{form.Handle.ToInt64():X}");

        var deadline = DateTime.Now.AddSeconds(seconds);
        while (DateTime.Now < deadline)
        {
            Application.DoEvents();
            Thread.Sleep(15);
        }

        Console.WriteLine($"抓取结束，共 {form.Lines.Count} 条");
        foreach (var line in form.Lines.Take(60)) Console.WriteLine(line);

        form.Close();
        form.Dispose();
        return 0;
    }
    /// <summary>逐个读取设备的特征报告（只读，不发写指令），看里面有没有电量类字段。</summary>
    public static int RunRead(string[] args)
    {
        string filter = args.Length > 1 ? args[1] : "";

        foreach (var device in HidDevice.Enumerate().Where(d => d.Path.Contains(filter, StringComparison.OrdinalIgnoreCase)))
        {
            Console.WriteLine();
            Console.WriteLine($"### {device.Product} u{device.UsagePage:X4}:{device.Usage:X4} in={device.InputReportLength} feat={device.FeatureReportLength}");

            if (device.FeatureReportLength <= 0)
            {
                Console.WriteLine("    无特征报告，跳过");
                continue;
            }

            for (byte id = 0; id <= 12; id++)
            {
                var buffer = new byte[device.FeatureReportLength];
                buffer[0] = id;
                try
                {
                    if (!Native.HidD_GetFeature(HandleOf(device), buffer, buffer.Length)) continue;
                }
                catch { continue; }

                Console.WriteLine($"    特征 id={id}: {BitConverter.ToString(buffer)}");
            }
        }

        return 0;
    }

    private static IntPtr HandleOf(HidDevice device)
    {
        // HidDevice 句柄不公开，这里重新打开一个只读句柄
        return Open(device.Path);
    }
    public static int Run(string[] args)
    {
        string filter = args.Length > 1 ? args[1] : "";

        var devices = HidDevice.Enumerate();
        Console.WriteLine($"发现 {devices.Count} 个 HID 接口" + (filter.Length > 0 ? $"（过滤: {filter}）" : ""));

        foreach (var device in devices)
        {
            if (filter.Length > 0 && !device.Path.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;

            Console.WriteLine();
            Console.WriteLine($"### {device.Product}  VID_{device.VendorId:X4}&PID_{device.ProductId:X4}");
            Console.WriteLine($"    用法 {device.UsagePage:X4}:{device.Usage:X4}  输入 {device.InputReportLength}B  特征 {device.FeatureReportLength}B");

            IntPtr handle = Open(device.Path);
            if (handle == IntPtr.Zero)
            {
                Console.WriteLine($"    [诊断] 打开失败 err={Marshal.GetLastWin32Error()}");
                continue;
            }

            try
            {
                if (!Native.HidD_GetPreparsedData(handle, out IntPtr preparsed) || preparsed == IntPtr.Zero)
                {
                    Console.WriteLine($"    [诊断] 预解析失败 err={Marshal.GetLastWin32Error()}");
                    continue;
                }

                try
                {
                    var featureCaps = GetCaps(Feature, preparsed);
                    var inputCaps = GetCaps(Input, preparsed);

                    var battery = featureCaps.Concat(inputCaps)
                        .Where(c => c.UsagePage == 0x06 && c.UsageMin == 0x20)
                        .ToList();
                    var power = featureCaps.Concat(inputCaps).Where(c => c.UsagePage == 0x84).ToList();
                    var vendor = featureCaps.Concat(inputCaps).Where(c => c.UsagePage >= 0xFF00).ToList();

                    Console.WriteLine($"    值用法: 特征 {featureCaps.Count} 个 / 输入 {inputCaps.Count} 个");

                    if (battery.Count > 0)
                    {
                        Console.WriteLine("    ★ 找到标准电池用量（0x06:0x20）:");
                        foreach (var c in battery)
                            Console.WriteLine($"       id={c.ReportId} 位宽={c.BitSize}");

                        foreach (var c in battery.Where(c => featureCaps.Contains(c)))
                        {
                            byte[] report = ReadFeature(handle, c.ReportId, Math.Max(device.FeatureReportLength, 2));
                            if (report == null) { Console.WriteLine("       读取特征报告失败"); continue; }

                            int status = HidP_GetUsageValue(Feature, 0x06, c.LinkCollection, 0x20,
                                out uint value, preparsed, report, report.Length);
                            Console.WriteLine($"       → 电量 = {(status >= 0 ? value.ToString() : "读取失败")} (status={status})");
                        }
                    }
                    else
                    {
                        Console.WriteLine("    ✗ 没有标准电池用量（0x06:0x20）");
                    }

                    if (power.Count > 0)
                    {
                        Console.WriteLine("    电池系统页（0x84）用法: " +
                            string.Join(",", power.Select(p => $"{p.UsageMin:X2}@id{p.ReportId}").Distinct().Take(8)));
                    }

                    if (vendor.Count > 0)
                    {
                        Console.WriteLine("    厂商自定义页: " +
                            string.Join(",", vendor.Select(v => $"{v.UsagePage:X4}:{v.UsageMin:X2}@id{v.ReportId}").Distinct().Take(10)));
                    }
                }
                finally
                {
                    Native.HidD_FreePreparsedData(preparsed);
                }
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        return 0;
    }

    private static List<ValueCaps> GetCaps(int reportType, IntPtr preparsed)
    {
        int count = 256;
        var caps = new ValueCaps[count];
        if (HidP_GetValueCaps(reportType, caps, ref count, preparsed) < 0) return new List<ValueCaps>();
        return caps.Take(Math.Min(count, caps.Length)).ToList();
    }

    private static byte[] ReadFeature(IntPtr handle, byte reportId, int length)
    {
        var buffer = new byte[Math.Max(length, 2)];
        buffer[0] = reportId;
        return Native.HidD_GetFeature(handle, buffer, buffer.Length) ? buffer : null;
    }

    private static IntPtr Open(string path)
    {
        IntPtr handle = Native.CreateFile(path, GenericRead | GenericWrite,
            FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (handle == Native.INVALID_HANDLE_VALUE || handle == IntPtr.Zero)
        {
            handle = Native.CreateFile(path, GenericRead, FileShareRead | FileShareWrite,
                IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        }
        return handle == Native.INVALID_HANDLE_VALUE ? IntPtr.Zero : handle;
    }
}
