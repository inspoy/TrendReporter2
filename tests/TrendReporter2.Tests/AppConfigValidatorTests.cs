using TrendReporter2.Core.Configuration;
using TrendReporter2.Core.Sources;

namespace TrendReporter2.Tests;

public sealed class AppConfigValidatorTests
{
    [Fact]
    public void Validate_AllowsExactPostgresProviderAndDefaultsMigrateOnStartup()
    {
        var config = ValidConfig();

        AppConfigValidator.Validate(config);

        Assert.NotNull(config.Database);
        Assert.True(config.Database!.MigrateOnStartup);
    }

    [Theory]
    [InlineData("litedb")]
    [InlineData("postgresql")]
    [InlineData("sqlite")]
    [InlineData("POSTGRES")]
    [InlineData("")]
    public void Validate_RejectsNonExactDatabaseProviders(string provider)
    {
        var config = ValidConfig();
        config = WithDatabase(config, new DatabaseConfig { Provider = provider, ConnectionString = "Host=localhost;Database=trend;Username=trend;Password=secret" });

        var exception = Assert.Throws<AppConfigValidationException>(() => AppConfigValidator.Validate(config));

        Assert.Contains("database.provider 必须为 postgres。", exception.Errors);
    }

    [Fact]
    public void Validate_RejectsMissingDatabaseSection()
    {
        var exception = Assert.Throws<AppConfigValidationException>(() => AppConfigValidator.Validate(new AppConfig()));

        Assert.Contains("database 不能为空。", exception.Errors);
    }

    [Fact]
    public void Validate_RejectsMissingDatabaseConnectionString()
    {
        var config = WithDatabase(ValidConfig(), new DatabaseConfig { Provider = "postgres" });

        var exception = Assert.Throws<AppConfigValidationException>(() => AppConfigValidator.Validate(config));

        Assert.Contains("database.connectionString 不能为空。", exception.Errors);
    }

    [Fact]
    public void Validate_RejectsMissingEnabledSources()
    {
        var config = WithSources(ValidConfig(), new SourcesConfig());

        var exception = Assert.Throws<AppConfigValidationException>(() => AppConfigValidator.Validate(config));

        Assert.Contains("sources 必须至少包含一个启用的信源。", exception.Errors);
    }

    [Fact]
    public void Validate_StillRunsOtherRulesWhenDatabaseIsInvalid()
    {
        var config = new AppConfig
        {
            Database = new DatabaseConfig { Provider = "sqlite", ConnectionString = "" },
            Analysis = new AnalysisConfig
            {
                FetchInterval = 0,
                HistoryHours = 24,
                Push = new PushConfig { PushCount = 0, PushTime = ["25:61"] },
                Event = new EventAnalysisConfig
                {
                    SourceCount = 0,
                    NormalizedRankThreshold = 1.5,
                    TrendWindowHours = 0,
                    StaleHours = 0,
                    ArchiveRecallDays = 0,
                    CandidateLimit = 0,
                    MergeThreshold = -0.1,
                    StaleMergeThreshold = 1.1,
                    MinTrendSamples = 0,
                    MinTrendHeat = -1
                }
            },
            Llm = new LlmConfig
            {
                Cluster = new LlmEndpointConfig { Pricing = new LLmPricingConfig { CacheRead = -1 } },
                Judge = new LlmEndpointConfig { Pricing = new LLmPricingConfig { Input = -1 } },
                Tagging = new LlmEndpointConfig { Pricing = new LLmPricingConfig { Output = -1 } }
            },
            Enrichment = new EnrichmentConfig
            {
                MaxRequestsPerRun = -1,
                MinTitleLength = 0,
                RecallWeakScoreThreshold = 1.5,
                RetryCooldownHours = -1
            },
            System = new SystemConfig
            {
                MaxParallelFetch = 0,
                MaxParallelEnrichment = 0,
                MaxParallelLlm = 0,
                TimeZone = "Invalid/Zone"
            }
        };

        var exception = Assert.Throws<AppConfigValidationException>(() => AppConfigValidator.Validate(config));

        Assert.Contains("database.provider 必须为 postgres。", exception.Errors);
        Assert.Contains("analysis.fetchInterval 必须大于 0。", exception.Errors);
        Assert.Contains("analysis.push.pushCount 必须大于 0。", exception.Errors);
        Assert.Contains("analysis.push.pushTime 包含无效时间 '25:61'，期望格式为 HH:mm。", exception.Errors);
        Assert.Contains("analysis.event.normalizedRankThreshold 必须在 0 到 1 之间。", exception.Errors);
        Assert.Contains("llm.cluster.pricing.cacheRead 必须是有限且非负的数字。", exception.Errors);
        Assert.Contains("llm.judge.pricing.input 必须是有限且非负的数字。", exception.Errors);
        Assert.Contains("llm.tagging.pricing.output 必须是有限且非负的数字。", exception.Errors);
        Assert.Contains("enrichment.maxRequestsPerRun 不能为负数。", exception.Errors);
        Assert.Contains("system.maxParallelFetch 必须大于 0。", exception.Errors);
        Assert.Contains("system.timeZone 'Invalid/Zone' 在当前系统上未找到。", exception.Errors);
    }

    [Fact]
    public void Validate_AllowsEnabledDailyHotApiSourcesWithBaseUrl()
    {
        var config = WithSources(
            ValidConfig(),
            new SourcesConfig
            {
                DailyHotApi = new SourceProviderConfig
                {
                    BaseUrl = "https://dailyhot.local",
                    Items =
                    [
                        new SourceItemConfig
                        {
                            Id = "dailyhot:weibo",
                            ExternalId = "weibo",
                            Category = "social",
                            DisplayName = "微博热搜",
                            ContentKind = ContentKind.RankedNews,
                            Enabled = true,
                            Weight = 1.2
                        }
                    ]
                }
            });

        AppConfigValidator.Validate(config);
    }

    [Fact]
    public void Validate_RejectsEnabledProviderGroupWithoutBaseUrl()
    {
        var config = WithSources(
            ValidConfig(),
            new SourcesConfig
            {
                DailyHotApi = new SourceProviderConfig
                {
                    Items =
                    [
                        new SourceItemConfig
                        {
                            Id = "dailyhot:weibo",
                            ExternalId = "weibo",
                            Category = "social",
                            DisplayName = "微博热搜",
                            ContentKind = ContentKind.RankedNews,
                            Enabled = true,
                            Weight = 1.0
                        }
                    ]
                }
            });

        var exception = Assert.Throws<AppConfigValidationException>(() => AppConfigValidator.Validate(config));

        Assert.Contains("sources.dailyHotApi.baseUrl 不能为空。", exception.Errors);
    }

    [Fact]
    public void Validate_RejectsInvalidSourceContentKind()
    {
        var config = WithSources(
            ValidConfig(),
            new SourcesConfig
            {
                NewsNow = new SourceProviderConfig
                {
                    BaseUrl = "https://news.local",
                    Items =
                    [
                        new SourceItemConfig
                        {
                            Id = "newsnow:china:ifeng",
                            ExternalId = "ifeng",
                            Category = "china",
                            DisplayName = "凤凰网",
                            ContentKind = "video",
                            Enabled = true,
                            Weight = 1.0
                        }
                    ]
                }
            });

        var exception = Assert.Throws<AppConfigValidationException>(() => AppConfigValidator.Validate(config));

        Assert.Contains("sources.newsNow.items[0].contentKind 必须是 ranked_news、flash_feed 或 topic。", exception.Errors);
    }

    private static AppConfig ValidConfig()
        => new()
        {
            Database = new DatabaseConfig
            {
                Provider = "postgres",
                ConnectionString = "Host=localhost;Database=trend;Username=trend;Password=secret"
            },
            Sources = new SourcesConfig
            {
                NewsNow = new SourceProviderConfig
                {
                    BaseUrl = "https://news.local",
                    Items =
                    [
                        new SourceItemConfig
                        {
                            Id = "newsnow:china:ifeng",
                            ExternalId = "ifeng",
                            Category = "china",
                            DisplayName = "凤凰网",
                            ContentKind = ContentKind.RankedNews,
                            Enabled = true,
                            Weight = 1.0
                        }
                    ]
                }
            },
            Analysis = new AnalysisConfig
            {
                FetchInterval = 3600,
                HistoryHours = 24,
                Push = new PushConfig { PushCount = 5, PushTime = ["09:20", "18:20"] },
                Event = new EventAnalysisConfig
                {
                    SourceCount = 3,
                    NormalizedRankThreshold = 0.75,
                    TrendWindowHours = 6,
                    StaleHours = 24,
                    ArchiveRecallDays = 30,
                    CandidateLimit = 20,
                    MergeThreshold = 0.82,
                    StaleMergeThreshold = 0.88,
                    MinTrendSamples = 3,
                    MinTrendHeat = 1.5
                }
            },
            Llm = new LlmConfig
            {
                Cluster = new LlmEndpointConfig { Pricing = new LLmPricingConfig { CacheRead = 0, Input = 0, Output = 0 } },
                Judge = new LlmEndpointConfig { Pricing = new LLmPricingConfig { CacheRead = 0, Input = 0, Output = 0 } },
                Tagging = new LlmEndpointConfig { Pricing = new LLmPricingConfig { CacheRead = 0, Input = 0, Output = 0 } }
            },
            Enrichment = new EnrichmentConfig
            {
                MaxRequestsPerRun = 5,
                MinTitleLength = 14,
                RecallWeakScoreThreshold = 0.35,
                RetryCooldownHours = 12
            },
            Filters = new FilterConfig { BlacklistKeywords = [] },
            System = new SystemConfig { TimeZone = "Asia/Shanghai", MaxParallelFetch = 4, MaxParallelEnrichment = 3, MaxParallelLlm = 2 }
        };

    private static AppConfig WithDatabase(AppConfig config, DatabaseConfig database)
        => new()
        {
            Sources = config.Sources,
            Database = database,
            Analysis = config.Analysis,
            Llm = config.Llm,
            Enrichment = config.Enrichment,
            Filters = config.Filters,
            Pushers = config.Pushers,
            System = config.System
        };

    private static AppConfig WithSources(AppConfig config, SourcesConfig sources)
        => new()
        {
            Sources = sources,
            Database = config.Database,
            Analysis = config.Analysis,
            Llm = config.Llm,
            Enrichment = config.Enrichment,
            Filters = config.Filters,
            Pushers = config.Pushers,
            System = config.System
        };
}
