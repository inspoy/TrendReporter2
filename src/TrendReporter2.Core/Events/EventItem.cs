namespace TrendReporter2.Core.Events;

public sealed class EventItem
{
    public string Id { get; set; } = string.Empty;

    public string DedupKey { get; set; } = string.Empty;

    public string EventId { get; set; } = string.Empty;

    public string ContentItemId { get; set; } = string.Empty;

    public double Confidence { get; set; }

    public DateTimeOffset MatchedAt { get; set; }

    public string? MatchReason { get; set; }
}
