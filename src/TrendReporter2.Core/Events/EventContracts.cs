using TrendReporter2.Core.Content;

namespace TrendReporter2.Core.Events;

public interface IEventRepository
{
    Task<IReadOnlyList<ContentItem>> LoadUnmappedRunContentItemsAsync(string runId, CancellationToken cancellationToken);

    Task<IReadOnlyList<EventAggregate>> LoadRecallCandidatesAsync(DateTimeOffset now, int historyHours, int staleHours, int archiveRecallDays, CancellationToken cancellationToken);

    Task MarkStaleEventsAsync(DateTimeOffset now, int staleHours, CancellationToken cancellationToken);

    Task<EventAggregate?> GetEventAsync(string eventId, CancellationToken cancellationToken);

    Task UpsertEventAsync(EventAggregate eventAggregate, CancellationToken cancellationToken);

    Task<bool> MapEventItemIfMissingAsync(EventItem eventItem, CancellationToken cancellationToken);

    Task<IReadOnlyList<RunEventScoringInput>> LoadRunEventScoringInputsAsync(string runId, CancellationToken cancellationToken);

    Task<IReadOnlyList<EventScoreSnapshot>> LoadRecentScoreSnapshotsAsync(IReadOnlyList<string> eventIds, DateTimeOffset since, CancellationToken cancellationToken);

    Task InsertEventScoreSnapshotAsync(EventScoreSnapshot snapshot, CancellationToken cancellationToken);

    Task<bool> InsertPushLogIfMissingAsync(PushLog pushLog, CancellationToken cancellationToken);

    Task UpdatePushLogAsync(PushLog pushLog, CancellationToken cancellationToken);

    Task UpdateEventsAsync(IReadOnlyList<EventAggregate> events, CancellationToken cancellationToken);
}

public interface IEventCandidateService
{
    Task<IReadOnlyList<EventCandidate>> RecallAsync(ContentItem item, DateTimeOffset now, CancellationToken cancellationToken);
}

public interface IClusterLlmClient
{
    bool IsConfigured { get; }

    Task<ClusterMatchResult> MatchAsync(ClusterMatchRequest request, CancellationToken cancellationToken);
}

public interface IEventMatcher
{
    Task<EventMatchRunResult> MatchRunAsync(string runId, DateTimeOffset now, CancellationToken cancellationToken);
}

public interface IEventScoringService
{
    Task<EventScoringRunResult> ScoreAndPushRunAsync(string runId, DateTimeOffset runStartedAt, DateTimeOffset now, CancellationToken cancellationToken);
}

public interface IJudgeLlmClient
{
    bool IsConfigured { get; }

    Task<JudgeResult> JudgeAsync(JudgeRequest request, CancellationToken cancellationToken);
}

public interface IPusher
{
    string Type { get; }

    bool IsConfigured { get; }

    Task<PushResult> PushAsync(PushMessage message, CancellationToken cancellationToken);
}

public sealed record EventCandidate(
    EventAggregate Event,
    double Score,
    IReadOnlyList<string> MatchedFeatures);

public sealed record ClusterMatchRequest(
    ContentItem Item,
    IReadOnlyList<EventCandidate> Candidates);

public sealed record ClusterMatchResult(
    string Decision,
    string? EventId,
    string? CanonicalTitle,
    string? Summary,
    double Confidence,
    string? Reason)
{
    public static ClusterMatchResult CreateNew(string? reason = null)
        => new(ClusterDecisions.Unrelated, null, null, null, 0, reason ?? "创建新事件");
}

public sealed record EventMatchRunResult(
    int CandidateCount,
    int CreatedEventCount,
    int MergedEventCount,
    int ReactivatedEventCount,
    int MappedItemCount,
    int SkippedAlreadyMappedCount);

public static class ClusterDecisions
{
    public const string SameEvent = "same_event";
    public const string FollowUp = "follow_up";
    public const string RelatedButDistinct = "related_but_distinct";
    public const string Unrelated = "unrelated";
}
