using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TrendReporter2.Core.Configuration;
using TrendReporter2.Core.Enrichment;
using TrendReporter2.Core.Events;
using TrendReporter2.Core.Jobs;
using TrendReporter2.Core.Sources;
using TrendReporter2.App.Scheduling;
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

if (options.ValidateOnly)
{
    Console.WriteLine("配置验证成功。");
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
builder.Services.AddHttpClient<NewsNowClient>();
builder.Services.AddSingleton<IContentSourceClient>(static serviceProvider => serviceProvider.GetRequiredService<NewsNowClient>());
builder.Services.AddHttpClient<DailyHotApiClient>();
builder.Services.AddSingleton<IContentSourceClient>(static serviceProvider => serviceProvider.GetRequiredService<DailyHotApiClient>());
builder.Services.AddHttpClient<IEnrichmentClient, WebExtractEnrichmentClient>();
builder.Services.AddHttpClient<IClusterLlmClient, ClusterLlmClient>();
builder.Services.AddHttpClient<IJudgeLlmClient, JudgeLlmClient>();
builder.Services.AddHttpClient<IPusher, UnipushPusher>();
builder.Services.AddSingleton<IFetchJob, FetchJob>();
builder.Services.AddSingleton<IDigestJob, DigestJob>();
if (options.Background)
{
    builder.Services.AddHostedService<FetchSchedulerService>();
    builder.Services.AddHostedService<DigestSchedulerService>();
}

using var host = builder.Build();
var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("TrendReporter2.App");

LogConfigSummary(logger, config, options);
if (!await TryRunStartupMigrationsAsync(host.Services, config, logger, Console.Error, CancellationToken.None))
{
    Environment.ExitCode = 1;
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

static async Task<bool> TryRunStartupMigrationsAsync(
    IServiceProvider services,
    AppConfig config,
    ILogger logger,
    TextWriter errorWriter,
    CancellationToken cancellationToken)
{
    try
    {
        await StartupMigration.RunIfEnabledAsync(services, config, logger, cancellationToken);
        return true;
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        logger.LogCritical(ex, "PostgreSQL 启动迁移失败，程序将退出。");
        errorWriter.WriteLine($"PostgreSQL 启动迁移失败：{ex.Message}");
        return false;
    }
}

static void LogConfigSummary(ILogger logger, AppConfig config, CliOptions options)
{
    var registry = new SourceRegistry(config);
    var sourceCount = registry.GetSources().Count;
    var enabledCount = registry.GetEnabledSources().Count;
    logger.LogInformation(
        "配置已从 {ConfigPath} 加载。配置信源数={SourceCount}，启用的信源数={EnabledCount}，抓取间隔秒={FetchInterval}。",
        options.ConfigPath,
        sourceCount,
        enabledCount,
        config.Analysis.FetchInterval);
}

internal sealed record CliOptions(string ConfigPath, CliMode Mode)
{
    public bool ValidateOnly => Mode == CliMode.Validate;

    public bool FetchOnce => Mode == CliMode.FetchOnce;

    public bool DigestOnce => Mode == CliMode.DigestOnce;

    public bool Background => Mode == CliMode.Background;

    public static CliOptions Parse(string[] args)
    {
        var configPath = "config.yaml";
        var mode = CliMode.Background;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            if (arg.Equals("validate", StringComparison.OrdinalIgnoreCase))
            {
                if (mode is not CliMode.Background and not CliMode.Validate)
                {
                    throw new ArgumentException("请只选择一种模式: validate、fetch-once 或 digest-once。");
                }

                mode = CliMode.Validate;
                continue;
            }

            if (arg.Equals("fetch-once", StringComparison.OrdinalIgnoreCase))
            {
                if (mode is not CliMode.Background and not CliMode.FetchOnce)
                {
                    throw new ArgumentException("请只选择一种模式: validate、fetch-once 或 digest-once。");
                }

                mode = CliMode.FetchOnce;
                continue;
            }

            if (arg.Equals("digest-once", StringComparison.OrdinalIgnoreCase))
            {
                if (mode is not CliMode.Background and not CliMode.DigestOnce)
                {
                    throw new ArgumentException("请只选择一种模式: validate、fetch-once 或 digest-once。");
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

            throw new ArgumentException($"未知参数 '{arg}'。用法: TrendReporter2.App [validate | fetch-once | digest-once] [--config <path>]。");
        }

        return new CliOptions(Path.GetFullPath(configPath), mode);
    }
}

internal enum CliMode
{
    Background,
    Validate,
    FetchOnce,
    DigestOnce
}

internal static class StartupMigration
{
    private static readonly Func<IServiceProvider, CancellationToken, Task<SqlMigrationRunResult>> DefaultRunMigrationAsync =
        static (services, cancellationToken) => services.GetRequiredService<SqlMigrationRunner>().RunAsync(cancellationToken);

    public static Func<IServiceProvider, CancellationToken, Task<SqlMigrationRunResult>> RunMigrationAsync { get; set; } = DefaultRunMigrationAsync;

    public static async Task RunIfEnabledAsync(
        IServiceProvider services,
        AppConfig config,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        if (config.Database?.MigrateOnStartup != true)
        {
            logger.LogInformation("PostgreSQL 启动迁移已按配置跳过。");
            return;
        }

        logger.LogInformation("PostgreSQL 启动迁移开始。");
        var result = await RunMigrationAsync(services, cancellationToken);
        logger.LogInformation(
            "PostgreSQL 启动迁移已完成：应用 {AppliedCount} 个，跳过 {SkippedCount} 个。",
            result.AppliedCount,
            result.SkippedCount);
    }

    public static void ResetForTests()
        => RunMigrationAsync = DefaultRunMigrationAsync;
}
