namespace TrendReporter2.Core.Enrichment;

public sealed class EnrichmentResult
{
    public string Summary { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public string Url { get; init; } = string.Empty;

    public IReadOnlyList<string> Tags { get; init; } = [];

    public string RawPayload { get; init; } = "{}";
}
