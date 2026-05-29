namespace TrendReporter2.Core.Sources;

public static class ContentKind
{
    public const string RankedNews = "ranked_news";
    public const string FlashFeed = "flash_feed";
    public const string Topic = "topic";

    public static bool IsDefined(string value)
        => value is RankedNews or FlashFeed or Topic;
}
