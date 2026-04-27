namespace TrendReporter2.Core.Enrichment;

public interface IEnrichmentService
{
    Task<EnrichmentRunResult> EnrichRunAsync(string runId, DateTimeOffset startedAt, CancellationToken cancellationToken);
}

public sealed record EnrichmentRunResult(
    int CandidateCount,
    int AttemptedCount,
    int SucceededCount,
    int FailedCount,
    int SkippedCount);

