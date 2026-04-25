namespace TrendReporter2.Core.Content;

public sealed class ContentItem
{
    public string Id { get; set; } = string.Empty;

    public string DedupKey { get; set; } = string.Empty;

    public string Source { get; set; } = string.Empty;

    public string Category { get; set; } = string.Empty;

    public string Type { get; set; } = "News";

    public string SourceItemId { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Url { get; set; } = string.Empty;

    public string? MobileUrl { get; set; }

    public DateTimeOffset? PubTime { get; set; }

    public string? HoverText { get; set; }

    public string? Summary { get; set; }

    public string? SummarySource { get; set; }

    public bool NeedEnrichment { get; set; }

    public string EnrichmentStatus { get; set; } = "None";

    public DateTimeOffset? EnrichmentTriedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public string? LastSeenRunId { get; set; }

    public DateTimeOffset? LastSeenAt { get; set; }

    public int LastSeenRank { get; set; }

    public string RawPayload { get; set; } = "{}";
}
