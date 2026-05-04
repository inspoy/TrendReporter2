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
using TrendReporter2.Infrastructure.Push;

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
        ExecuteDataView(config, options.DataView ?? throw new InvalidOperationException("data-view 选项未被正确解析。"));
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
builder.Logging.AddFilter("System.Net.Http.HttpClient", LogLevel.Warning);
builder.Logging.AddFilter("Microsoft.Extensions.Http", LogLevel.Warning);

builder.Services.AddSingleton(config);
builder.Services.AddTrendReporterInfrastructure();
builder.Services.AddHttpClient();
builder.Services.AddHttpClient<INewsSourceClient, NewsNowClient>();
builder.Services.AddHttpClient<IEnrichmentClient, WebExtractEnrichmentClient>();
builder.Services.AddHttpClient<IClusterLlmClient, ClusterLlmClient>();
builder.Services.AddHttpClient<IJudgeLlmClient, JudgeLlmClient>();
builder.Services.AddHttpClient<IPusher, UnipushPusher>();
builder.Services.AddSingleton<IFetchJob, FetchJob>();
builder.Services.AddSingleton<IDigestJob, DigestJob>();

if (!options.ValidateOnly && !options.FetchOnce && !options.DigestOnce)
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
    logger.LogInformation("验证模式已成功完成。");
    return;
}

if (options.FetchOnce)
{
    logger.LogInformation("单次抓取模式已启动。");
    await host.Services.GetRequiredService<IFetchJob>().RunAsync(CancellationToken.None);
    logger.LogInformation("单次抓取模式已完成。");
    return;
}

if (options.DigestOnce)
{
    var timeZone = TimeZoneResolver.Find(config.System.TimeZone);
    var localNow = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, timeZone);
    var slotTime = localNow.ToString("HH:mm");
    var localDate = DateOnly.FromDateTime(localNow.DateTime);
    logger.LogInformation("单次摘要推送模式已启动。本地日期={LocalDate}，时段={SlotTime}。", localDate, slotTime);
    await host.Services.GetRequiredService<IDigestJob>().RunAsync(localDate, slotTime, localNow, CancellationToken.None);
    logger.LogInformation("单次摘要推送模式已完成。");
    return;
}

logger.LogInformation("TrendReporter2 后台服务启动中。");
await host.RunAsync();

static void LogConfigSummary(ILogger logger, AppConfig config, CliOptions options)
{
    var sourceCount = config.NewsNow.Sources.Values.Sum(sources => sources.Count);
    logger.LogInformation(
        "配置已从 {ConfigPath} 加载。NewsNowBaseUrl={NewsNowBaseUrl}，分类数={CategoryCount}，来源数={SourceCount}，抓取间隔秒={FetchInterval}。",
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

    public bool DigestOnce => Mode == CliMode.DigestOnce;

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
                    throw new ArgumentException("data-view 需要指定集合名称。用法: TrendReporter2.App data-view <collection> [--limit <n>] [--json] [--config <path>]。");
                }

                collection = arg;
                expectCollection = false;
                continue;
            }

            if (arg.Equals("data-view", StringComparison.OrdinalIgnoreCase))
            {
                if (mode is CliMode.Validate or CliMode.FetchOnce or CliMode.DigestOnce)
                {
                    throw new ArgumentException("data-view 不能与 validate、fetch-once 或 digest-once 同时使用。");
                }

                if (mode == CliMode.DataView)
                {
                    throw new ArgumentException("data-view 只能指定一次。");
                }

                mode = CliMode.DataView;
                expectCollection = true;
                continue;
            }

            if (arg.Equals("validate", StringComparison.OrdinalIgnoreCase))
            {
                if (mode == CliMode.DataView)
                {
                    throw new ArgumentException("validate 不能与 data-view 同时使用。");
                }

                if (mode is CliMode.FetchOnce or CliMode.DigestOnce)
                {
                    throw new ArgumentException("请只选择一种模式: validate、fetch-once、digest-once 或 data-view。");
                }

                mode = CliMode.Validate;
                continue;
            }

            if (arg.Equals("fetch-once", StringComparison.OrdinalIgnoreCase))
            {
                if (mode == CliMode.DataView)
                {
                    throw new ArgumentException("fetch-once 不能与 data-view 同时使用。");
                }

                if (mode is CliMode.Validate or CliMode.DigestOnce)
                {
                    throw new ArgumentException("请只选择一种模式: validate、fetch-once、digest-once 或 data-view。");
                }

                mode = CliMode.FetchOnce;
                continue;
            }

            if (arg.Equals("digest-once", StringComparison.OrdinalIgnoreCase))
            {
                if (mode == CliMode.DataView)
                {
                    throw new ArgumentException("digest-once 不能与 data-view 同时使用。");
                }

                if (mode is CliMode.Validate or CliMode.FetchOnce)
                {
                    throw new ArgumentException("请只选择一种模式: validate、fetch-once、digest-once 或 data-view。");
                }

                mode = CliMode.DigestOnce;
                continue;
            }

            if (arg.Equals("--config", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 >= args.Length)
                {
                    throw new ArgumentException("--config 需要指定文件路径。");
                }

                configPath = args[++i];
                continue;
            }

            if (arg.Equals("--limit", StringComparison.OrdinalIgnoreCase))
            {
                if (mode != CliMode.DataView)
                {
                    throw new ArgumentException("未知参数 '--limit'。用法: TrendReporter2.App [validate | fetch-once | digest-once | data-view <collection> [--limit <n>] [--json] [--config <path>]]。");
                }

                if (i + 1 >= args.Length)
                {
                    throw new ArgumentException("--limit 必须是 1 到 1000 之间的整数。");
                }

                var limitText = args[++i];
                if (!int.TryParse(limitText, out limit) || limit is < 1 or > 1000)
                {
                    throw new ArgumentException("--limit 必须是 1 到 1000 之间的整数。");
                }

                continue;
            }

            if (arg.Equals("--json", StringComparison.OrdinalIgnoreCase))
            {
                if (mode != CliMode.DataView)
                {
                    throw new ArgumentException("未知参数 '--json'。用法: TrendReporter2.App [validate | fetch-once | digest-once | data-view <collection> [--limit <n>] [--json] [--config <path>]]。");
                }

                json = true;
                continue;
            }

            throw new ArgumentException($"未知参数 '{arg}'。用法: TrendReporter2.App [validate | fetch-once | digest-once | data-view <collection> [--limit <n>] [--json] [--config <path>]]。");
        }

        if (expectCollection)
        {
            throw new ArgumentException("data-view 需要指定集合名称。用法: TrendReporter2.App data-view <collection> [--limit <n>] [--json] [--config <path>]。");
        }

        if (mode == CliMode.DataView)
        {
            if (collection is null)
            {
                throw new ArgumentException("data-view 需要指定集合名称。用法: TrendReporter2.App data-view <collection> [--limit <n>] [--json] [--config <path>]。");
            }

            if (!TrendCollectionNames.All.Contains(collection))
            {
                throw new ArgumentException($"未知集合 '{collection}'。有效集合: {string.Join(", ", TrendCollectionNames.All)}。");
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
    DigestOnce,
    DataView
}

internal sealed record DataViewOptions(string Collection, int Limit, bool Json);
