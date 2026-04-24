namespace TrendReporter2.Core.Configuration;

public static class TimeZoneResolver
{
    private static readonly IReadOnlyDictionary<string, string> WindowsFallbacks =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Asia/Shanghai"] = "China Standard Time",
            ["UTC"] = "UTC"
        };

    public static TimeZoneInfo Find(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return TimeZoneInfo.Local;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (TimeZoneNotFoundException) when (WindowsFallbacks.TryGetValue(id, out var windowsId))
        {
            return TimeZoneInfo.FindSystemTimeZoneById(windowsId);
        }
    }
}
