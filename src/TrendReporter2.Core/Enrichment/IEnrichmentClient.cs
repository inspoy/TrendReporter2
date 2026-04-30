using TrendReporter2.Core.Content;

namespace TrendReporter2.Core.Enrichment;

public interface IEnrichmentClient
{
    Task<EnrichmentResult?> EnrichAsync(ContentItem item, CancellationToken cancellationToken);
}
