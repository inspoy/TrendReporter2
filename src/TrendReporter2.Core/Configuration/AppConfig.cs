namespace TrendReporter2.Core.Configuration;

public sealed class AppConfig
{
    public SourcesConfig Sources { get; init; } = new();

    public DatabaseConfig? Database { get; init; }

    public AnalysisConfig Analysis { get; init; } = new();

    public LlmConfig Llm { get; init; } = new();

    public EnrichmentConfig Enrichment { get; init; } = new();

    public FilterConfig Filters { get; init; } = new();

    public ReportConfig Report { get; init; } = new();

    public List<PusherConfig> Pushers { get; init; } = [];

    public SystemConfig System { get; init; } = new();
}

public sealed class SourcesConfig
{
    public SourceProviderConfig NewsNow { get; init; } = new();

    public SourceProviderConfig DailyHotApi { get; init; } = new();
}

public sealed class SourceProviderConfig
{
    public string BaseUrl { get; init; } = string.Empty;

    public List<SourceItemConfig> Items { get; init; } = [];
}

public sealed class SourceItemConfig
{
    public string Id { get; init; } = string.Empty;

    public string ExternalId { get; init; } = string.Empty;

    public string Param { get; init; } = string.Empty;

    public string Category { get; init; } = string.Empty;

    public string DisplayName { get; init; } = string.Empty;

    public string ContentKind { get; init; } = string.Empty;

    public bool Enabled { get; init; } = true;

    public double Weight { get; init; } = 1.0;
}

public sealed class DatabaseConfig
{
    public string Provider { get; init; } = string.Empty;

    public string ConnectionString { get; init; } = string.Empty;

    public bool MigrateOnStartup { get; init; } = true;
}

public sealed class AnalysisConfig
{
    public int FetchInterval { get; init; } = 3600;

    public int HistoryHours { get; init; } = 24;

    public PushConfig Push { get; init; } = new();

    public EventAnalysisConfig Event { get; init; } = new();

    public RepeatPushConfig RepeatPush { get; init; } = new();
}

public sealed class PushConfig
{
    public List<string> PushTime { get; init; } = ["09:20", "18:20"];

    public int PushCount { get; init; } = 5;
}

public sealed class EventAnalysisConfig
{
    public int SourceCount { get; init; } = 3;

    public double NormalizedRankThreshold { get; init; } = 0.75;

    public int TrendWindowHours { get; init; } = 6;

    public int StaleHours { get; init; } = 24;

    public int ArchiveRecallDays { get; init; } = 30;

    public int CandidateLimit { get; init; } = 20;

    public double MergeThreshold { get; init; } = 0.82;

    public double RuleMergeThreshold { get; init; } = 0.55;

    public double StaleMergeThreshold { get; init; } = 0.88;

    public double MergeSimilarityThreshold { get; init; } = 0.7;

    public int MergeCandidateLimit { get; init; } = 15;

    public double MergeLlmConfidenceThreshold { get; init; } = 0.6;

    public int MinTrendSamples { get; init; } = 3;

    public double MinTrendHeat { get; init; } = 1.5;
}

public sealed class RepeatPushConfig
{
    public int SourceAddThreshold { get; init; } = 2;

    public double RankScoreImproveThreshold { get; init; } = 0.15;

    public double ScoreImproveThreshold { get; init; } = 12;
}

public sealed class LlmConfig
{
    public LlmEndpointConfig Cluster { get; init; } = new();

    public LlmEndpointConfig Judge { get; init; } = new();

    public LlmEndpointConfig Tagging { get; init; } = new();

    public EmbeddingLlmConfig Embedding { get; init; } = new();
}

public sealed class LlmEndpointConfig
{
    public string BaseUrl { get; init; } = string.Empty;

    public string ApiKey { get; init; } = string.Empty;

    public string Model { get; init; } = string.Empty;

    public int MaxTokens { get; init; } = 2048;

    public int MaxParallel { get; init; } = 2;

    public string ReasoningEffort { get; init; } = string.Empty;

    public LLmPricingConfig Pricing { get; init; } = new();
}

public sealed class LLmPricingConfig
{
    public float CacheRead { get; init; } = 0;

    public float Input { get; init; } = 0;

    public float Output { get; init; } = 0;
}

public sealed class EmbeddingLlmConfig
{
    public string BaseUrl { get; init; } = string.Empty;

    public string ApiKey { get; init; } = string.Empty;

    public string Model { get; init; } = string.Empty;

    public int MaxTokens { get; init; } = 8192;

    public int MaxParallel { get; init; } = 2;

    public LLmPricingConfig Pricing { get; init; } = new();

    public string Version { get; init; } = "v1";

    public int Dimensions { get; init; } = 768;

    public int MaxRequestsPerRun { get; init; } = 50;

    public double VectorSimilarityThreshold { get; init; } = 0.78;

    public int VectorCandidateLimit { get; init; } = 10;
}

public sealed class EnrichmentConfig
{
    public string WebExtractUrl { get; init; } = string.Empty;

    public List<string> EnabledSources { get; init; } = [];

    public List<string> DisabledSources { get; init; } = [];

    public int MaxRequestsPerRun { get; init; } = 5;

    public int MinTitleLength { get; init; } = 14;

    public bool OnlyWhenRecallWeak { get; init; } = true;

    public double RecallWeakScoreThreshold { get; init; } = 0.35;

    public int RetryCooldownHours { get; init; } = 12;
}

public sealed class FilterConfig
{
    public List<string> BlacklistKeywords { get; init; } = [];
}

public sealed class ReportConfig
{
    public bool Enabled { get; init; }

    public string OutputDirectory { get; init; } = "./data/reports";

    public string PublicBaseUrl { get; init; } = string.Empty;

    public bool IncludeInDigestPush { get; init; } = true;
}

public sealed class PusherConfig
{
    public string Type { get; init; } = string.Empty;

    public string Url { get; init; } = string.Empty;

    public string Secret { get; init; } = string.Empty;

    public string Cate { get; init; } = "default";

    public string Channels { get; init; } = string.Empty;
}

public sealed class SystemConfig
{
    public string TimeZone { get; init; } = "Asia/Shanghai";

    public int MaxParallelFetch { get; init; } = 4;

    public int MaxParallelEnrichment { get; init; } = 3;

}
