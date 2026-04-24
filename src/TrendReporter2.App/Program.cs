using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TrendReporter2.App.Scheduling;
using TrendReporter2.Core.Configuration;
using TrendReporter2.Core.Jobs;
using TrendReporter2.Core.Persistence;
using TrendReporter2.Infrastructure;
using TrendReporter2.Infrastructure.Configuration;

CliOptions options;
AppConfig config;

try
{
    options = CliOptions.Parse(args);
    var configLoader = new YamlAppConfigLoader();
    config = configLoader.Load(options.ConfigPath);
}
catch (Exception ex) when (ex is ArgumentException or FileNotFoundException or AppConfigValidationException or InvalidOperationException)
{
    Console.Error.WriteLine(ex.Message);
    Environment.ExitCode = 1;
    return;
}

var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(console =>
{
    console.SingleLine = true;
    console.TimestampFormat = "yyyy-MM-dd HH:mm:ss ";
});

builder.Services.AddSingleton(config);
builder.Services.AddTrendReporterInfrastructure(); // 核心，把所有服务的具体实现注册进去
builder.Services.AddHttpClient();
builder.Services.AddSingleton<IFetchJob, EmptyFetchJob>();
builder.Services.AddSingleton<IDigestJob, EmptyDigestJob>();

if (!options.ValidateOnly)
{
    builder.Services.AddHostedService<FetchSchedulerService>();
    builder.Services.AddHostedService<DigestSchedulerService>();
}

using var host = builder.Build();
var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("TrendReporter2.App");

LogConfigSummary(logger, config, options);
host.Services.GetRequiredService<ITrendDatabaseInitializer>().Initialize();

if (options.ValidateOnly)
{
    logger.LogInformation("Validation mode completed successfully.");
    return;
}

logger.LogInformation("TrendReporter2 background service is starting.");
await host.RunAsync();

static void LogConfigSummary(ILogger logger, AppConfig config, CliOptions options)
{
    var sourceCount = config.NewsNow.Sources.Values.Sum(sources => sources.Count);
    logger.LogInformation(
        "Configuration loaded from {ConfigPath}. NewsNowBaseUrl={NewsNowBaseUrl}, Categories={CategoryCount}, Sources={SourceCount}, FetchIntervalSeconds={FetchInterval}.",
        options.ConfigPath,
        config.NewsNow.BaseUrl,
        config.NewsNow.Sources.Count,
        sourceCount,
        config.Analysis.FetchInterval);
}

internal sealed record CliOptions(string ConfigPath, bool ValidateOnly)
{
    public static CliOptions Parse(string[] args)
    {
        var configPath = "config.yaml"; // 默认的配置文件路径
        var validateOnly = false;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            if (arg.Equals("--validate", StringComparison.OrdinalIgnoreCase))
            {
                validateOnly = true;
                continue;
            }

            if (arg.Equals("--config", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length)
                {
                    throw new ArgumentException("--config requires a file path.");
                }

                configPath = args[++i];
            }
        }

        return new CliOptions(Path.GetFullPath(configPath), validateOnly);
    }
}
