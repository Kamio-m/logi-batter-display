using System.Runtime.InteropServices;

namespace LogiBatteryTray;

/// <summary>用 GetSystemTimes 估算 CPU 总占用（无需额外依赖）。</summary>
internal static class CpuUsage
{
    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low;
        public uint High;
        public ulong Value => ((ulong)High << 32) | Low;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);

    private static readonly object Gate = new();
    private static ulong _prevIdle, _prevKernel, _prevUser;
    private static int _lastPercent;

    /// <summary>返回 0-100 的 CPU 占用；采样间隔过短时返回上次结果。</summary>
    public static int Read()
    {
        lock (Gate)
        {
            if (!GetSystemTimes(out var idle, out var kernel, out var user)) return _lastPercent;

            ulong idleValue = idle.Value;
            ulong kernelValue = kernel.Value;
            ulong userValue = user.Value;

            if (_prevKernel == 0 && _prevUser == 0 && _prevIdle == 0)
            {
                _prevIdle = idleValue;
                _prevKernel = kernelValue;
                _prevUser = userValue;
                return _lastPercent;
            }

            ulong idleDelta = idleValue - _prevIdle;
            ulong kernelDelta = kernelValue - _prevKernel;
            ulong userDelta = userValue - _prevUser;

            ulong total = kernelDelta + userDelta;
            if (total == 0) return _lastPercent;

            // kernel 时间包含 idle，所以 busy = total - idle
            double busy = (double)(total - idleDelta) / total * 100.0;
            _lastPercent = (int)Math.Clamp(Math.Round(busy), 0, 100);

            _prevIdle = idleValue;
            _prevKernel = kernelValue;
            _prevUser = userValue;

            return _lastPercent;
        }
    }
}