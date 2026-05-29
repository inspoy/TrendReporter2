namespace TrendReporter2.Core.Sources;

public sealed class FetchedContentItem
{
    public string SourceId { get; init; } = string.Empty;

    public string SourceItemId { get; init; } = string.Empty;

    public string DedupKey { get; init; } = string.Empty;

    public string ContentKind { get; init; } = string.Empty;

    public string Category { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public string Url { get; init; } = string.Empty;

    public string? MobileUrl { get; init; }

    public DateTimeOffset? PublishedAt { get; init; }

    public int? Rank { get; init; }

    public int? SourceListSize { get; init; }

    public string? HoverText { get; init; }

    public string? SummaryText { get; init; }

    public string RawPayload { get; init; } = "{}";
}
