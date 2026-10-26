namespace LogiBatteryTray;

/// <summary>
/// 手动登记的设备（硬件不提供电量时用「上次充满 + 续航时长」估算剩余电量）。
/// </summary>
public sealed class ManualBatteryDevice
{
    public string Name { get; set; } = "设备";

    /// <summary>标称续航时长（天，可含小数，例如 2 小时 = 0.0833）。</summary>
    public double LifeDays { get; set; } = 60;

    /// <summary>上次充满/换电池的时间（UTC）。</summary>
    public DateTime LastChargedUtc { get; set; } = DateTime.UtcNow;

    private double SafeLifeDays => Math.Max(1.0 / 1440.0, LifeDays);   // 最少 1 分钟

    /// <summary>估算剩余百分比（0-100）。</summary>
    public int RemainingPercent(DateTime nowUtc)
    {
        double life = SafeLifeDays;
        double used = (nowUtc - LastChargedUtc).TotalDays;
        if (used < 0) used = 0;

        double left = 1.0 - used / life;
        return (int)Math.Clamp(Math.Round(left * 100), 0, 100);
    }

    /// <summary>估算剩余时长（天，可为小数）。</summary>
    public double RemainingDays(DateTime nowUtc)
    {
        double life = SafeLifeDays;
        double used = Math.Max(0, (nowUtc - LastChargedUtc).TotalDays);
        return Math.Max(0, life - used);
    }

    /// <summary>把天数格式化成「X 天 / X 小时 / X 分钟」。</summary>
    public static string FormatDuration(double days)
    {
        if (days >= 1) return $"{days:0.#} 天";

        double hours = days * 24;
        if (hours >= 1) return $"{hours:0.#} 小时";

        return $"{Math.Max(1, Math.Round(days * 1440)):0} 分钟";
    }

    public string Describe(DateTime nowUtc) =>
        $"约剩 {FormatDuration(RemainingDays(nowUtc))} · 续航 {FormatDuration(SafeLifeDays)}";
}
