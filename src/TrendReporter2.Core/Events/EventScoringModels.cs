using TrendReporter2.Core.Content;

namespace TrendReporter2.Core.Events;

public class EventScore
{
    public string EventId { get; set; } = string.Empty;

    public string RunId { get; set; } = string.Empty;

    public DateTimeOffset CalculatedAt { get; set; }

    public double CoverageScore { get; set; }

    public double RankScore { get; set; }

    public double TrendScore { get; set; }

    public double PersistenceScore { get; set; }

    public double LlmBoostScore { get; set; }

    public double ReactivationBonus { get; set; }

    public double TotalScore { get; set; }

    public int UniqueSourceCount { get; set; }

    public double AvgRank { get; set; }

    public double AvgNormalizedRank { get; set; }

    public double HeatValue { get; set; }

    public double SmoothedHeatValue { get; set; }

    public int TrendEvidenceCount { get; set; }

    public string? CurrentStage { get; set; }

    public List<string> TriggerReasons { get; set; } = [];
}

public sealed class EventScoreSnapshot : EventScore
{
    public string Id { get; set; } = string.Empty;
}

public sealed record RunEventScoringInput(
    EventAggregate Event,
    IReadOnlyList<RunEventContentEvidence> Evidence);

public sealed record RunEventContentEvidence(
    ContentItem ContentItem,
    ContentSnapshot Snapshot,
    DateTimeOffset MatchedAt);

public sealed record EventScoringRunResult(
    int ScoredEventCount,
    int EligibleEventCount,
    int PushedEventCount);

public static class TriggerReasons
{
    public const string CoverageRank = "coverage_rank";
    public const string RisingTrend = "rising_trend";
    public const string Reactivation = "reactivation";
    public const string FirstPush = "first_push";
    public const string SourceIncrease = "source_increase";
    public const string RankImprovement = "rank_improvement";
    public const string ScoreImprovement = "score_improvement";
    public const string JudgeHighImportance = "judge_high_importance";
}

public static class EventProgressStages
{
    public const string Initial = "Initial";
    public const string Expanding = "Expanding";
    public const string Escalating = "Escalating";
    public const string FollowUp = "FollowUp";
    public const string Cooling = "Cooling";
}
