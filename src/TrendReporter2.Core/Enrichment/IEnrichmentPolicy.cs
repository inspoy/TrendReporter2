using TrendReporter2.Core.Content;
using TrendReporter2.Core.News;

namespace TrendReporter2.Core.Enrichment;

public interface IEnrichmentPolicy
{
    bool NeedEnrichment(NewsItem item);

    bool NeedEnrichment(ContentItem item);
}

