using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TrendReporter2.Core.Configuration;
using TrendReporter2.Core.Enrichment;
using TrendReporter2.Infrastructure.Configuration;
using TrendReporter2.Infrastructure.Persistence;

namespace TrendReporter2.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddTrendReporterInfrastructure(this IServiceCollection services)
    {
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

        return services;
    }
}
