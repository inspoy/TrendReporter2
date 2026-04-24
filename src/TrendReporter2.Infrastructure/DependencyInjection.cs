using Microsoft.Extensions.DependencyInjection;
using TrendReporter2.Core.Configuration;
using TrendReporter2.Core.Persistence;
using TrendReporter2.Infrastructure.Configuration;
using TrendReporter2.Infrastructure.Persistence;

namespace TrendReporter2.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddTrendReporterInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton<IAppConfigLoader, YamlAppConfigLoader>();
        services.AddSingleton<ITrendDatabaseInitializer, LiteDbInitializer>();

        return services;
    }
}
