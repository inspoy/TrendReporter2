namespace TrendReporter2.Core.Configuration;

public sealed class AppConfig
{
    public NewsNowConfig NewsNow { get; init; } = new();

    public DatabaseConfig Database { get; init; } = new();

    public AnalysisConfig Analysis { get; init; } = new();

    public LlmConfig Llm { get; init; } = new();

    public EnrichmentConfig Enrichment { get; init; } = new();

    public FilterConfig Filters { get; init; } = new();

    public List<PusherConfig> Pushers { get; init; } = [];

    public SystemConfig System { get; init; } = new();
}

public sealed class NewsNowConfig
{
    public string BaseUrl { get; init; } = string.Empty;

    public Dictionary<string, List<string>> Sources { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class DatabaseConfig
{
    public string Path { get; init; } = "./data/trend.db";
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

    public double StaleMergeThreshold { get; init; } = 0.88;

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

    public LlmEndpointConfig Writer { get; init; } = new();
}

public sealed class LlmEndpointConfig
{
    public string BaseUrl { get; init; } = string.Empty;

    public string ApiKey { get; init; } = string.Empty;

    public string Model { get; init; } = string.Empty;

    public int MaxTokens { get; init; } = 2048;

    public LLmPricingConfig Pricing { get; init; } = new();
}

public sealed class LLmPricingConfig
{
    public float CacheRead { get; init; } = 0;

    public float Input { get; init; } = 0;

    public float Output { get; init; } = 0;
}

public sealed class EnrichmentConfig
{
    public string WebExtractUrl { get; init; } = string.Empty;

    public List<string> EnabledSources { get; init; } = [];

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

    public int MaxParallelLlm { get; init; } = 2;
}
