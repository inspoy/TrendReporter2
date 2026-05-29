namespace TrendReporter2.Core.Fetch;

public sealed class FetchRun
{
    public string Id { get; set; } = string.Empty;

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset? FinishedAt { get; set; }

    public string Status { get; set; } = FetchRunStatuses.Running;

    public int SourceCount { get; set; }

    public int SuccessSourceCount { get; set; }

    public int FailureSourceCount { get; set; }

    public int FetchedItemCount { get; set; }

    public int EnrichedItemCount { get; set; }

    public int MatchedEventCount { get; set; }

    public int PushedEventCount { get; set; }

    public decimal EstimatedLlmCost { get; set; }

    public List<string> Errors { get; set; } = [];
}

public static class FetchRunStatuses
{
    public const string Running = "Running";
    public const string Succeeded = "Succeeded";
    public const string Partial = "Partial";
    public const string Failed = "Failed";
}
