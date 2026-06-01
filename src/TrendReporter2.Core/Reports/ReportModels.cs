namespace TrendReporter2.Core.Reports;

public sealed class ReportPayload
{
    public string ReportType { get; init; } = ReportTypes.DigestHtml;

    public DateTimeOffset GeneratedAt { get; init; }

    public DateTimeOffset WindowStart { get; init; }

    public DateTimeOffset WindowEnd { get; init; }

    public string SlotTime { get; init; } = string.Empty;

    public List<ReportEventItem> Events { get; init; } = [];
}

public sealed class ReportEventItem
{
    public string EventId { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public string Summary { get; init; } = string.Empty;

    public string? Stage { get; init; }

    public string? ProgressSummary { get; init; }

    public double TotalScore { get; init; }

    public double HeatValue { get; init; }

    public int UniqueSourceCount { get; init; }

    public IReadOnlyList<string> TriggerReasons { get; init; } = [];

    public IReadOnlyList<string> Tags { get; init; } = [];

    public List<ReportContentItem> ContentItems { get; init; } = [];
}

public sealed class ReportContentItem
{
    public string ContentItemId { get; init; } = string.Empty;

    public string Source { get; init; } = string.Empty;

    public DateTimeOffset? PublishedAt { get; init; }

    public string Title { get; init; } = string.Empty;

    public string Url { get; init; } = string.Empty;
}

public sealed class ReportSnapshot
{
    public string Id { get; init; } = string.Empty;

    public string ReportType { get; init; } = ReportTypes.DigestHtml;

    public DateTimeOffset SlotTime { get; init; }

    public DateTimeOffset GeneratedAt { get; init; }

    public string FilePath { get; init; } = string.Empty;

    public string? PublicUrl { get; init; }

    public int EventCount { get; init; }

    public string PayloadJson { get; init; } = "{}";
}

public sealed record RenderedReport(string FilePath, string? PublicUrl);

public interface IReportReadModelQuery
{
    Task<ReportPayload> BuildDigestReportAsync(DateTimeOffset windowStart, DateTimeOffset windowEnd, string slotTime, int limit, CancellationToken cancellationToken);
}

public interface IStaticHtmlReportRenderer
{
    Task<RenderedReport> RenderAsync(ReportPayload payload, CancellationToken cancellationToken);
}

public interface IReportSnapshotRepository
{
    Task UpsertAsync(ReportSnapshot snapshot, CancellationToken cancellationToken);
}

public static class ReportTypes
{
    public const string DigestHtml = "digest_html";
}
