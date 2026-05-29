using TrendReporter2.Core.Content;
using TrendReporter2.Core.News;
using TrendReporter2.Core.Sources;

namespace TrendReporter2.Core.Enrichment;

public interface IEnrichmentPolicy
{
    bool NeedEnrichment(NewsItem item);

    bool NeedEnrichment(FetchedContentItem item);

    bool NeedEnrichment(ContentItem item);
}
