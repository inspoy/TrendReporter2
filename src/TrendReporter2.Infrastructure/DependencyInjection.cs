using Microsoft.Extensions.DependencyInjection;
using TrendReporter2.Core.Content;
using TrendReporter2.Core.Configuration;
using TrendReporter2.Core.Enrichment;
using TrendReporter2.Core.Events;
using TrendReporter2.Core.Fetch;
using TrendReporter2.Core.Persistence;
using TrendReporter2.Infrastructure.Configuration;
using TrendReporter2.Infrastructure.Enrichment;
using TrendReporter2.Infrastructure.Persistence;

namespace TrendReporter2.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddTrendReporterInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton<IAppConfigLoader, YamlAppConfigLoader>();
        services.AddSingleton<LiteDbConnectionFactory>();
        services.AddSingleton<ITrendDatabaseInitializer, LiteDbInitializer>();
        services.AddSingleton<IEnrichmentPolicy, EnrichmentPolicy>();
        services.AddSingleton<IContentIngestService, ContentIngestService>();
        services.AddSingleton<IEnrichmentService, EnrichmentService>();
        services.AddSingleton<IEventRepository, LiteDbEventRepository>();
        services.AddSingleton<IEventCandidateService, EventCandidateService>();
        services.AddSingleton<IEventMatcher, EventMatcher>();
        services.AddSingleton<IFetchRunRepository, FetchRunRepository>();

        return services;
    }
}
