namespace TrendReporter2.Core.Events;

public sealed record JudgeRequest(
    string? RunId,
    EventAggregate Event,
    EventScore Score,
    IReadOnlyList<RunEventContentEvidence> Evidence,
    IReadOnlyList<string> TriggerReasons);

public sealed record JudgeResult(
    string? Importance,
    double BoostScore,
    IReadOnlyList<string> Labels,
    string? Reason,
    string? Summary,
    string? Stage,
    string? ProgressSummary)
{
    public static JudgeResult Neutral(string? reason = null) => new(null, 0, [], reason, null, null, null);
}
