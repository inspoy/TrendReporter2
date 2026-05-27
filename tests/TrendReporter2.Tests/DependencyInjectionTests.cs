using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using TrendReporter2.Core.Configuration;
using TrendReporter2.Infrastructure;

namespace TrendReporter2.Tests;

public sealed class DependencyInjectionTests
{
    [Fact]
    public void AddTrendReporterInfrastructure_RegistersSingletonNpgsqlDataSource()
    {
        var services = new ServiceCollection();
        services.AddSingleton(ValidConfig());
        services.AddLogging();

        services.AddTrendReporterInfrastructure();

        var dataSourceDescriptors = services.Where(descriptor => descriptor.ServiceType == typeof(NpgsqlDataSource)).ToList();
        Assert.Single(dataSourceDescriptors);
        Assert.Equal(ServiceLifetime.Singleton, dataSourceDescriptors.Single().Lifetime);

        using var provider = services.BuildServiceProvider();
        var first = provider.GetRequiredService<NpgsqlDataSource>();
        var second = provider.GetRequiredService<NpgsqlDataSource>();

        Assert.Same(first, second);
    }

    private static AppConfig ValidConfig()
        => new()
        {
            Database = new DatabaseConfig
            {
                Provider = "postgres",
                ConnectionString = "Host=localhost;Port=5432;Database=trend;Username=trend;Password=secret"
            }
        };
}
