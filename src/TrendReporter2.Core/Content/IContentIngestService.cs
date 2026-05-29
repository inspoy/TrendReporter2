using TrendReporter2.Core.Sources;

namespace TrendReporter2.Core.Content;

public interface IContentIngestService
{
    Task<ContentIngestResult> IngestAsync(
        string runId,
        IReadOnlyList<FetchedContentItem> items,
        DateTimeOffset capturedAt,
        CancellationToken cancellationToken);
}
