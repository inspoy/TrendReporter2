using TrendReporter2.Core.Configuration;

namespace TrendReporter2.Core.Events;

public static class EventBlacklistPolicy
{
    public static bool Apply(EventAggregate eventAggregate, FilterConfig filters)
    {
        var text = string.Join(' ', eventAggregate.CanonicalTitle, eventAggregate.Summary);
        var keyword = filters.BlacklistKeywords
            .FirstOrDefault(keyword => !string.IsNullOrWhiteSpace(keyword) && text.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        if (keyword is null)
        {
            return false;
        }

        eventAggregate.IsBlacklisted = true;
        eventAggregate.BlacklistReason = $"匹配到黑名单关键词: {keyword}";
        return true;
    }
}
