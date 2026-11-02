namespace AIClockBridge;

/// Shanghai/Shenzhen A-share trading days. The firmware owns the intraday
/// windows (9:30-11:30, 13:00-15:00 Beijing time); the bridge only answers
/// "is today a trading day at all", because holidays change every year and a
/// bridge update is far easier to ship than a reflash.
///
/// A-shares never trade on weekends, including the make-up working Saturdays/
/// Sundays (调休补班) around holidays, so the rule is: weekday AND not listed.
/// For a year with no table yet the weekday rule alone applies, and
/// <see cref="HasYear"/> lets the tray say so.
///
/// To add a year: copy the exchange's "XXXX年部分节假日休市安排" notice
/// (published each December) as inclusive ranges - weekend dates inside a
/// range are harmless.
public static class ChinaMarketCalendar
{
    static readonly TimeSpan BeijingOffset = TimeSpan.FromHours(8);

    static readonly Dictionary<int, (DateOnly From, DateOnly To)[]> Closures = new()
    {
        // 上交所/深交所 2026年部分节假日休市安排 (2025-12-22)
        [2026] = new[]
        {
            (new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 3)),   // 元旦
            (new DateOnly(2026, 2, 15), new DateOnly(2026, 2, 23)), // 春节
            (new DateOnly(2026, 4, 4), new DateOnly(2026, 4, 6)),   // 清明
            (new DateOnly(2026, 5, 1), new DateOnly(2026, 5, 5)),   // 劳动节
            (new DateOnly(2026, 6, 19), new DateOnly(2026, 6, 21)), // 端午
            (new DateOnly(2026, 9, 25), new DateOnly(2026, 9, 27)), // 中秋
            (new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 7)), // 国庆
        },
    };

    public static bool HasYear(int year) => Closures.ContainsKey(year);

    public static DateOnly BeijingDate(long unixSeconds) =>
        DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds(unixSeconds).ToOffset(BeijingOffset).DateTime);

    public static bool IsTradingDay(DateOnly date)
    {
        if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday) return false;
        if (!Closures.TryGetValue(date.Year, out var ranges)) return true;
        foreach (var (from, to) in ranges)
            if (date >= from && date <= to) return false;
        return true;
    }

    public static bool IsTradingDay(long unixSeconds) => IsTradingDay(BeijingDate(unixSeconds));
}
