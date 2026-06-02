namespace TrendReporter2.Core.Events;

public sealed class EventAggregate
{
    public string Id { get; set; } = string.Empty;

    public string Type { get; set; } = EventType.NewsEvent;

    public string CanonicalTitle { get; set; } = string.Empty;

    public string Summary { get; set; } = string.Empty;

    public List<string> Aliases { get; set; } = [];

    public List<string> Entities { get; set; } = [];

    public List<string> Places { get; set; } = [];

    public List<string> KeyTerms { get; set; } = [];

    public List<string> RepresentativeTitles { get; set; } = [];

    public string? CurrentStage { get; set; }

    public string? ProgressSummary { get; set; }

    public List<EventMilestone> Milestones { get; set; } = [];

    public DateTimeOffset? ProgressUpdatedAt { get; set; }

    public string Status { get; set; } = EventStatus.Active;

    public DateTimeOffset FirstSeenAt { get; set; }

    public DateTimeOffset LastSeenAt { get; set; }

    public DateTimeOffset LastActivatedAt { get; set; }

    public DateTimeOffset? LastPushedAt { get; set; }

    public int PushCount { get; set; }

    public double? LastPushScore { get; set; }

    public double? LastPushRankScore { get; set; }

    public int? LastPushSourceCount { get; set; }

    public bool IsBlacklisted { get; set; }

    public string? BlacklistReason { get; set; }

    public string? MergedIntoEventId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class EventMilestone
{
    public DateTimeOffset Time { get; set; }

    public string Kind { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;

    public string? Source { get; set; }

    public string Summary { get; set; } = string.Empty;
}

public static class EventType
{
    public const string NewsEvent = "NewsEvent";
    public const string Topic = "Topic";
}

public static class EventStatus
{
    public const string Active = "Active";
    public const string Stale = "Stale";
    public const string Merged = "Merged";
}
