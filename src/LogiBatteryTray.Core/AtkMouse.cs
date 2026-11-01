using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace LogiBatteryTray;

/// <summary>
/// ATK / VXE / VGN 2.4G 无线鼠标电量读取（私有协议逆向结果）。
///
/// 协议 2（适用于 VID 0x373B + 用途页 0xFF05，64 字节输出报告）：
///   发送 Report ID 0x08，载荷 [0x7D, 0x72, 0x02, 0x00, 0x01, 0x07, 0x01]
///   回复校验：byte[1] == 0x72 且 byte[5] == 0x07
///   电量百分比在回复 byte[7]
///
/// 协议 1（用途页 0xFF02，17 字节输出报告）：
///   载荷 [0x04, 0x00 × 15, 0x49]，电量在回复 byte[6]
/// </summary>
public static class AtkMouse
{
    public const ushort VendorId = 0x373B;

    /// <summary>接收器是否插着（不读取电量，很快）。</summary>
    public static bool IsPresent()
    {
        try
        {
            return HidDevice.Enumerate().Any(d => d.VendorId == VendorId &&
                                                  (d.UsagePage == 0xFF05 || d.UsagePage == 0xFF02));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>尝试读取电量；成功返回 true 并给出 0-100 的百分比。</summary>
    public static bool TryRead(out int percent, out string message)
    {
        percent = 0;
        message = "";

        var candidates = HidDevice.Enumerate()
            .Where(d => d.VendorId == VendorId && d.CanWrite)
            .Where(d => d.UsagePage == 0xFF05 || d.UsagePage == 0xFF02)
            .OrderByDescending(d => d.OutputReportLength)
            .ToList();

        if (candidates.Count == 0)
        {
            message = "未检测到 ATK 接收器";
            return false;
        }

        foreach (var device in candidates)
        {
            try
            {
                bool ok = device.UsagePage == 0xFF05
                    ? TryProtocol2(device, out percent, out message)
                    : TryProtocol1(device, out percent, out message);

                if (ok) return true;
            }
            catch (Exception ex)
            {
                message = "读取异常: " + ex.Message;
            }
            finally
            {
                device.Dispose();
            }
        }

        return false;
    }

    private const int ReportId = 0x08;

    /// <summary>协议 2：64 字节输出报告，命令 0x72。</summary>
    private static bool TryProtocol2(HidDevice device, out int percent, out string message)
    {
        percent = 0;
        message = "";

        int length = device.OutputReportLength > 0 ? device.OutputReportLength : 64;
        if (length < 8) length = 64;

        byte[] request = new byte[length];
        request[0] = ReportId;
        byte[] payload = { 0x7D, 0x72, 0x02, 0x00, 0x01, 0x07, 0x01 };
        Array.Copy(payload, 0, request, 1, Math.Min(payload.Length, length - 1));

        for (int attempt = 0; attempt < 3; attempt++)
        {
            device.Drain();

            if (!device.SetOutputReport(request) && !device.Write(request))
            {
                message = "发送命令失败";
                continue;
            }

            var deadline = DateTime.UtcNow.AddMilliseconds(150);
            while (DateTime.UtcNow < deadline)
            {
                var reply = device.Read(80);
                if (reply == null || reply.Length < 8) continue;

                // 回复可能带或不带 report id 前缀，两种都试
                int offset = reply[0] == ReportId ? 0 : -1;
                int b1 = reply[offset + 1], b5 = reply[offset + 5], b7 = reply[offset + 7];

                if (b1 == 0x72 && b5 == 0x07 && b7 <= 100)
                {
                    percent = b7;
                    message = $"ATK 鼠标电量 {percent}%";
                    return true;
                }
            }
        }

        message = "ATK 鼠标无响应（可能已休眠，移动一下鼠标）";
        return false;
    }

    /// <summary>协议 1：17 字节输出报告，命令 0x49。</summary>
    private static bool TryProtocol1(HidDevice device, out int percent, out string message)
    {
        percent = 0;
        message = "";

        int length = device.OutputReportLength > 0 ? device.OutputReportLength : 17;
        byte[] request = new byte[length];
        request[0] = ReportId;
        request[1] = 0x04;
        request[length - 1] = 0x49;

        for (int attempt = 0; attempt < 3; attempt++)
        {
            device.Drain();
            if (!device.Write(request)) continue;

            var deadline = DateTime.UtcNow.AddMilliseconds(150);
            while (DateTime.UtcNow < deadline)
            {
                var reply = device.Read(80);
                if (reply == null || reply.Length < 7) continue;

                int offset = reply[0] == ReportId ? 0 : -1;
                int level = reply[offset + 6];
                if (level <= 100)
                {
                    percent = level;
                    message = $"ATK 鼠标电量 {percent}%";
                    return true;
                }
            }
        }

        message = "ATK 鼠标无响应（协议 1）";
        return false;
    }
}
