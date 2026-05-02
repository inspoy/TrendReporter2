using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TrendReporter2.App.DataView;
using TrendReporter2.App.Scheduling;
using TrendReporter2.Core.Configuration;
using TrendReporter2.Core.Enrichment;
using TrendReporter2.Core.Events;
using TrendReporter2.Core.Jobs;
using TrendReporter2.Core.News;
using TrendReporter2.Core.Persistence;
using TrendReporter2.Infrastructure;
using TrendReporter2.Infrastructure.Enrichment;
using TrendReporter2.Infrastructure.Configuration;
using TrendReporter2.Infrastructure.Llm;
using TrendReporter2.Infrastructure.News;
using TrendReporter2.Infrastructure.Persistence;

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

if (options.Mode == CliMode.DataView)
{
    try
    {
        ExecuteDataView(config, options.DataView ?? throw new InvalidOperationException("data-view options were not parsed."));
    }
    catch (Exception ex) when (ex is ArgumentException or FileNotFoundException or InvalidOperationException or LiteDB.LiteException)
    {
        Console.Error.WriteLine(ex.Message);
        Environment.ExitCode = 1;
    }

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
builder.Services.AddTrendReporterInfrastructure();
builder.Services.AddHttpClient();
builder.Services.AddHttpClient<INewsSourceClient, NewsNowClient>();
builder.Services.AddHttpClient<IEnrichmentClient, WebExtractEnrichmentClient>();
builder.Services.AddHttpClient<IClusterLlmClient, OpenAiClusterLlmClient>();
builder.Services.AddSingleton<IFetchJob, FetchJob>();
builder.Services.AddSingleton<IDigestJob, EmptyDigestJob>();

if (!options.ValidateOnly && !options.FetchOnce)
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

if (options.FetchOnce)
{
    logger.LogInformation("Fetch-once mode started.");
    await host.Services.GetRequiredService<IFetchJob>().RunAsync(CancellationToken.None);
    logger.LogInformation("Fetch-once mode completed.");
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

static void ExecuteDataView(AppConfig config, DataViewOptions options)
{
    var reader = new DataViewReader(config, new LiteDbConnectionFactory(config));
    var result = reader.Read(options.Collection, options.Limit);
    var output = options.Json ? DataViewRenderer.RenderJson(result) : DataViewRenderer.RenderTable(result);
    Console.WriteLine(output);
}

internal sealed record CliOptions(string ConfigPath, CliMode Mode, DataViewOptions? DataView)
{
    public bool ValidateOnly => Mode == CliMode.Validate;

    public bool FetchOnce => Mode == CliMode.FetchOnce;

    public static CliOptions Parse(string[] args)
    {
        var configPath = "config.yaml";
        var mode = CliMode.Background;
        DataViewOptions? dataView = null;
        string? collection = null;
        var limit = 20;
        var json = false;
        var expectCollection = false;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            if (expectCollection)
            {
                if (arg.StartsWith("--", StringComparison.Ordinal))
                {
                    throw new ArgumentException("data-view requires a collection name. Usage: TrendReporter2.App data-view <collection> [--limit <n>] [--json] [--config <path>].");
                }

                collection = arg;
                expectCollection = false;
                continue;
            }

            if (arg.Equals("data-view", StringComparison.OrdinalIgnoreCase))
            {
                if (mode is CliMode.Validate or CliMode.FetchOnce)
                {
                    throw new ArgumentException("data-view cannot be combined with validate or fetch-once.");
                }

                if (mode == CliMode.DataView)
                {
                    throw new ArgumentException("data-view may only be specified once.");
                }

                mode = CliMode.DataView;
                expectCollection = true;
                continue;
            }

            if (arg.Equals("validate", StringComparison.OrdinalIgnoreCase))
            {
                if (mode == CliMode.DataView)
                {
                    throw new ArgumentException("validate cannot be combined with data-view.");
                }

                if (mode == CliMode.FetchOnce)
                {
                    throw new ArgumentException("Choose only one mode: validate, fetch-once, or data-view.");
                }

                mode = CliMode.Validate;
                continue;
            }

            if (arg.Equals("fetch-once", StringComparison.OrdinalIgnoreCase))
            {
                if (mode == CliMode.DataView)
                {
                    throw new ArgumentException("fetch-once cannot be combined with data-view.");
                }

                if (mode == CliMode.Validate)
                {
                    throw new ArgumentException("Choose only one mode: validate, fetch-once, or data-view.");
                }

                mode = CliMode.FetchOnce;
                continue;
            }

            if (arg.Equals("--config", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length)
                {
                    throw new ArgumentException("--config requires a file path.");
                }

                configPath = args[++i];
                continue;
            }

            if (arg.Equals("--limit", StringComparison.OrdinalIgnoreCase))
            {
                if (mode != CliMode.DataView)
                {
                    throw new ArgumentException("Unknown argument '--limit'. Usage: TrendReporter2.App [validate | fetch-once | data-view <collection> [--limit <n>] [--json] [--config <path>]].");
                }

                if (i + 1 >= args.Length)
                {
                    throw new ArgumentException("--limit must be an integer from 1 to 1000.");
                }

                var limitText = args[++i];
                if (!int.TryParse(limitText, out limit) || limit is < 1 or > 1000)
                {
                    throw new ArgumentException("--limit must be an integer from 1 to 1000.");
                }

                continue;
            }

            if (arg.Equals("--json", StringComparison.OrdinalIgnoreCase))
            {
                if (mode != CliMode.DataView)
                {
                    throw new ArgumentException("Unknown argument '--json'. Usage: TrendReporter2.App [validate | fetch-once | data-view <collection> [--limit <n>] [--json] [--config <path>]].");
                }

                json = true;
                continue;
            }

            throw new ArgumentException($"Unknown argument '{arg}'. Usage: TrendReporter2.App [validate | fetch-once | data-view <collection> [--limit <n>] [--json] [--config <path>]].");
        }

        if (expectCollection)
        {
            throw new ArgumentException("data-view requires a collection name. Usage: TrendReporter2.App data-view <collection> [--limit <n>] [--json] [--config <path>].");
        }

        if (mode == CliMode.DataView)
        {
            if (collection is null)
            {
                throw new ArgumentException("data-view requires a collection name. Usage: TrendReporter2.App data-view <collection> [--limit <n>] [--json] [--config <path>].");
            }

            if (!TrendCollectionNames.All.Contains(collection))
            {
                throw new ArgumentException($"Unknown collection '{collection}'. Valid collections: {string.Join(", ", TrendCollectionNames.All)}.");
            }

            dataView = new DataViewOptions(collection, limit, json);
        }

        return new CliOptions(Path.GetFullPath(configPath), mode, dataView);
    }
}

internal enum CliMode
{
    Background,
    Validate,
    FetchOnce,
    DataView
}

internal sealed record DataViewOptions(string Collection, int Limit, bool Json);
