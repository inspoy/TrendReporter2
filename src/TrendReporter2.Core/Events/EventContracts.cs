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

    Task<IReadOnlyList<DigestCandidate>> LoadDigestCandidatesAsync(DateTimeOffset since, int limit, CancellationToken cancellationToken);

    Task InsertEventScoreSnapshotAsync(EventScoreSnapshot snapshot, CancellationToken cancellationToken);

    Task<bool> InsertPushLogIfMissingAsync(PushLog pushLog, CancellationToken cancellationToken);

    Task UpdatePushLogAsync(PushLog pushLog, CancellationToken cancellationToken);

    Task UpdateEventsAsync(IReadOnlyList<EventAggregate> events, CancellationToken cancellationToken);

    Task<IReadOnlyList<EventAggregate>> LoadMergeCandidateEventsAsync(DateTimeOffset now, int historyHours, int archiveRecallDays, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<EventAggregate>>([]);

    Task<IReadOnlyList<EventItem>> LoadActiveEventItemsAsync(string eventId, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<EventItem>>([]);

    Task BatchUpdateEventItemActiveStateAsync(IReadOnlyList<string> eventItemIds, bool isActive, CancellationToken cancellationToken)
        => Task.CompletedTask;

    Task BatchMigrateEventItemsAsync(IReadOnlyList<EventItem> items, string mergeHistoryId, DateTimeOffset now, CancellationToken cancellationToken)
        => Task.CompletedTask;

    Task BatchSetEventMergedStatusAsync(IReadOnlyList<string> eventIds, string targetEventId, CancellationToken cancellationToken)
        => Task.CompletedTask;
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

public interface IScoringService
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

public interface IAppStateRepository
{
    Task<AppState?> GetAsync(string key, CancellationToken cancellationToken);

    Task UpsertAsync(AppState state, CancellationToken cancellationToken);
}

public interface IEventMergeRepository
{
    Task InsertMergeHistoryAsync(EventMergeHistory mergeHistory, CancellationToken cancellationToken);

    Task<bool> HasBeenProcessedAsync(string sourceEventId, string targetEventId, CancellationToken cancellationToken);

    Task MigrateEventItemsAsync(string sourceEventId, string targetEventId, string mergeHistoryId, DateTimeOffset now, CancellationToken cancellationToken);

    Task DeactivateEventItemsAsync(string eventId, CancellationToken cancellationToken);
}

public interface ISecondaryMergeService
{
    Task<SecondaryMergeRunResult> MergeRunAsync(string runId, DateTimeOffset now, CancellationToken cancellationToken);
}

public sealed record EventCandidate(
    EventAggregate Event,
    double Score,
    IReadOnlyList<string> MatchedFeatures);

public sealed record DigestCandidate(
    EventAggregate Event,
    EventScoreSnapshot Score);

public sealed record ClusterMatchRequest(
    string? RunId,
    ContentItem Item,
    IReadOnlyList<EventCandidate> Candidates)
{
    /// <summary>
    /// 当为 true 时，表示 Item 是临时构建的虚拟条目，并未实际持久化到 content_item 表中，
    /// LLM 用量记录中的 ContentItemId 应设为 null，避免外键约束冲突。
    /// </summary>
    public bool IsVirtualItem { get; init; }
}

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
