using TrendReporter2.Core.News;

namespace TrendReporter2.Core.Content;

public interface IContentIngestService
{
    Task<ContentIngestResult> IngestAsync(
        string runId,
        IReadOnlyList<NewsItem> items,
        DateTimeOffset capturedAt,
        CancellationToken cancellationToken);
}
