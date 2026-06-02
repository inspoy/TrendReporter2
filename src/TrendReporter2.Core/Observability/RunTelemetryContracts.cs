using TrendReporter2.Core.Configuration;

namespace TrendReporter2.Core.Observability;

public interface IRunTelemetryRecorder
{
    Task RecordSourceAsync(RunSourceTelemetry telemetry, CancellationToken cancellationToken);

    Task RecordStageAsync(RunStageTelemetry telemetry, CancellationToken cancellationToken);

    Task RecordLlmUsageAsync(LlmUsageRecord usage, CancellationToken cancellationToken);

    Task<LlmUsageSummary> GetLlmUsageSummaryAsync(string runId, CancellationToken cancellationToken);
}

public sealed record RunSourceTelemetry(
    string RunId,
    string SourceId,
    string Category,
    string Source,
    string Status,
    int DurationMs,
    int ItemCount,
    string? Error,
    DateTimeOffset CreatedAt);

public sealed record RunStageTelemetry(
    string Id,
    string RunId,
    string Stage,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    int DurationMs,
    string Status,
    string? Error);

public sealed record LlmUsageRecord(
    string Id,
    string? RunId,
    string Stage,
    string Model,
    string? RequestId,
    string? ContentItemId,
    string? EventId,
    int? InputTokens,
    int? OutputTokens,
    int? CacheReadTokens,
    decimal EstimatedCost,
    int DurationMs,
    bool Success,
    int RetryCount,
    string? Error,
    DateTimeOffset CreatedAt);

public sealed record LlmUsageSummary(int CallCount, decimal EstimatedCost);

public sealed record LlmUsageTokens(int? InputTokens, int? OutputTokens, int? CacheReadTokens);

public static class RunStageNames
{
    public const string Fetch = "fetch";
    public const string Ingest = "ingest";
    public const string Enrich = "enrich";
    public const string Match = "match";
    public const string Score = "score";
    public const string Push = "push";
    public const string Report = "report";
    public const string Tagging = "tagging";
    public const string Embedding = "embedding";
}

public static class LlmUsageStages
{
    public const string Cluster = "cluster";
    public const string Judge = "judge";
    public const string Tagging = "tagging";
    public const string Embedding = "embedding";
}

public static class RunTelemetryStatuses
{
    public const string Succeeded = "succeeded";
    public const string Failed = "failed";
    public const string Skipped = "skipped";
}

public static class LlmUsageCostCalculator
{
    public static decimal EstimateCost(LlmUsageTokens tokens, LLmPricingConfig pricing)
    {
        var input = Cost(tokens.InputTokens, pricing.Input);
        var output = Cost(tokens.OutputTokens, pricing.Output);
        var cacheRead = Cost(tokens.CacheReadTokens, pricing.CacheRead);
        return decimal.Round(input + output + cacheRead, 8, MidpointRounding.AwayFromZero);
    }

    private static decimal Cost(int? tokens, float perMillionPrice)
        => (tokens ?? 0) / 1_000_000m * (decimal)perMillionPrice;
}
