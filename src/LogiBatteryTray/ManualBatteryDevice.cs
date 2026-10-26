namespace LogiBatteryTray;

/// <summary>
/// 手动登记的设备（硬件不提供电量时用「上次充满 + 续航天数」估算剩余电量）。
/// </summary>
public sealed class ManualBatteryDevice
{
    public string Name { get; set; } = "设备";

    /// <summary>标称续航天数（从满电用到空电）。</summary>
    public int LifeDays { get; set; } = 60;

    /// <summary>上次充满/换电池的时间（UTC）。</summary>
    public DateTime LastChargedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>估算剩余百分比（0-100）。</summary>
    public int RemainingPercent(DateTime nowUtc)
    {
        int days = Math.Max(1, LifeDays);
        double used = (nowUtc - LastChargedUtc).TotalDays;
        if (used < 0) used = 0;
        double left = 1.0 - used / days;
        return (int)Math.Clamp(Math.Round(left * 100), 0, 100);
    }

    /// <summary>估算剩余天数（可为 0）。</summary>
    public int RemainingDays(DateTime nowUtc)
    {
        int days = Math.Max(1, LifeDays);
        double used = Math.Max(0, (nowUtc - LastChargedUtc).TotalDays);
        return (int)Math.Max(0, Math.Round(days - used));
    }

    public string Describe(DateTime nowUtc) =>
        $"约剩 {RemainingDays(nowUtc)} 天 · 续航 {LifeDays} 天";
}
