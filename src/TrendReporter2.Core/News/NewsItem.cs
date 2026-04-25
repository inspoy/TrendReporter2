namespace TrendReporter2.Core.News;

public sealed class NewsItem
{
    public string Source { get; init; } = string.Empty;

    public string Category { get; init; } = string.Empty;

    public string SourceItemId { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public string Url { get; init; } = string.Empty;

    public string? MobileUrl { get; init; }

    public DateTimeOffset? PubTime { get; init; }

    public string? HoverText { get; init; }

    public int Rank { get; init; }

    public int SourceListSize { get; init; }

    public string RawPayload { get; init; } = "{}";
}
