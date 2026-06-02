using TrendReporter2.Core.Events;

namespace TrendReporter2.Core.Events;

public sealed record EventMergeCandidate(
    EventAggregate SourceEvent,
    EventAggregate TargetEvent,
    double Similarity,
    IReadOnlyList<string> MatchedReasons);

public sealed record EventMergeDecision(
    string Decision,
    double Confidence,
    string Reason)
{
    public bool ShouldMerge => string.Equals(Decision, ClusterDecisions.SameEvent, StringComparison.Ordinal);

    public static EventMergeDecision SameEvent(double confidence, string reason)
        => new(ClusterDecisions.SameEvent, confidence, reason);

    public static EventMergeDecision RelatedButDistinct(double confidence, string reason)
        => new(ClusterDecisions.RelatedButDistinct, confidence, reason);

    public static EventMergeDecision Unrelated(double confidence, string reason)
        => new(ClusterDecisions.Unrelated, confidence, reason);
}

public sealed class EventMergeHistory
{
    public string Id { get; set; } = string.Empty;

    public string SourceEventId { get; set; } = string.Empty;

    public string TargetEventId { get; set; } = string.Empty;

    public double Confidence { get; set; }

    public string Reason { get; set; } = string.Empty;

    public string DecidedBy { get; set; } = string.Empty;

    public string EvidenceSnapshot { get; set; } = "{}";

    public DateTimeOffset CreatedAt { get; set; }
}

public static class MergeDecidedBy
{
    public const string Rule = "rule";
    public const string Llm = "llm";
    public const string Manual = "manual";
}

public sealed record SecondaryMergeRunResult(
    int CandidatePairCount,
    int HardFilterExcludedCount,
    int LlmDecidedCount,
    int MergedCount);

public sealed record SecondaryMergeLlmRequest(
    string? RunId,
    EventAggregate SourceEvent,
    EventAggregate TargetEvent,
    EventMergeCandidate Candidate);

public sealed record SecondaryMergeLlmResponse(
    string Decision,
    double Confidence,
    string Reason);
