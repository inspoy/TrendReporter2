using TrendReporter2.Core.Content;

namespace TrendReporter2.Core.Enrichment;

public interface ITavilyClient
{
    Task<EnrichmentResult?> EnrichAsync(ContentItem item, CancellationToken cancellationToken);
}

