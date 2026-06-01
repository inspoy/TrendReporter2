using Microsoft.Extensions.DependencyInjection;
using Dapper;
using Npgsql;
using TrendReporter2.Core.Configuration;
using TrendReporter2.Core.Content;
using TrendReporter2.Core.Enrichment;
using TrendReporter2.Core.Events;
using TrendReporter2.Core.Fetch;
using TrendReporter2.Core.Observability;
using TrendReporter2.Core.Reports;
using TrendReporter2.Core.Sources;
using TrendReporter2.Core.Tags;
using TrendReporter2.Infrastructure.Configuration;
using TrendReporter2.Infrastructure.Enrichment;
using TrendReporter2.Infrastructure.Persistence;
using TrendReporter2.Infrastructure.Reports;

namespace TrendReporter2.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddTrendReporterInfrastructure(this IServiceCollection services)
    {
        DefaultTypeMap.MatchNamesWithUnderscores = true;

        services.AddSingleton<IAppConfigLoader, YamlAppConfigLoader>();
        services.AddSingleton(static serviceProvider =>
        {
            var config = serviceProvider.GetRequiredService<AppConfig>();
            var connectionString = config.Database?.ConnectionString;
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new InvalidOperationException("database.connectionString 不能为空。");
            }

            return NpgsqlDataSource.Create(connectionString);
        });
        services.AddSingleton<SqlMigrationRunner>();
        services.AddSingleton<IEnrichmentPolicy, EnrichmentPolicy>();
        services.AddSingleton<ITagService, TagService>();
        services.AddSingleton<ISourceRegistry, SourceRegistry>();
        services.AddSingleton<ISourceRepository, PostgresSourceRepository>();
        services.AddSingleton<PostgresContentRepository>();
        services.AddSingleton<IContentIngestService>(static serviceProvider => serviceProvider.GetRequiredService<PostgresContentRepository>());
        services.AddSingleton<IEnrichmentService, EnrichmentService>();
        services.AddSingleton<IEventRepository, PostgresEventRepository>();
        services.AddSingleton<ITagRepository, PostgresTagRepository>();
        services.AddSingleton<PostgresReportRepository>();
        services.AddSingleton<IReportReadModelQuery>(static serviceProvider => serviceProvider.GetRequiredService<PostgresReportRepository>());
        services.AddSingleton<IReportSnapshotRepository>(static serviceProvider => serviceProvider.GetRequiredService<PostgresReportRepository>());
        services.AddSingleton<IStaticHtmlReportRenderer, StaticHtmlReportRenderer>();
        services.AddSingleton<IAppStateRepository, PostgresAppStateRepository>();
        services.AddSingleton<IFetchRunRepository, PostgresFetchRunRepository>();
        services.AddSingleton<IRunTelemetryRecorder, PostgresRunTelemetryRecorder>();
        services.AddSingleton<IEventCandidateService, EventCandidateService>();
        services.AddSingleton<IEventMatcher, EventMatcher>();
        services.AddSingleton<IEventScoringService, EventScoringService>();

        return services;
    }
}
