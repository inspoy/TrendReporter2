using System.Globalization;
using TrendReporter2.Core.Sources;

namespace TrendReporter2.Core.Configuration;

public static class AppConfigValidator
{
    public static void Validate(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var errors = new List<string>();

        ValidateSourceProvider("sources.newsNow", config.Sources.NewsNow);
        ValidateSourceProvider("sources.dailyHotApi", config.Sources.DailyHotApi);
        Require(HasEnabledSource(config.Sources), "sources 必须至少包含一个启用的信源。");

        if (config.Database is null)
        {
            errors.Add("database 不能为空。");
        }
        else
        {
            Require(config.Database.Provider == "postgres", "database.provider 必须为 postgres。");
            Require(!string.IsNullOrWhiteSpace(config.Database.ConnectionString), "database.connectionString 不能为空。");
        }

        Require(config.Analysis.FetchInterval > 0, "analysis.fetchInterval 必须大于 0。");
        Require(config.Analysis.HistoryHours > 0, "analysis.historyHours 必须大于 0。");
        Require(config.Analysis.Push.PushCount > 0, "analysis.push.pushCount 必须大于 0。");
        Require(config.Analysis.Push.PushTime.Count > 0, "analysis.push.pushTime 必须至少包含一个时间。");

        foreach (var pushTime in config.Analysis.Push.PushTime)
        {
            Require(
                TimeOnly.TryParseExact(pushTime, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
                $"analysis.push.pushTime 包含无效时间 '{pushTime}'，期望格式为 HH:mm。");
        }

        Require(config.Analysis.Event.SourceCount > 0, "analysis.event.sourceCount 必须大于 0。");
        Require(IsRatio(config.Analysis.Event.NormalizedRankThreshold), "analysis.event.normalizedRankThreshold 必须在 0 到 1 之间。");
        Require(config.Analysis.Event.TrendWindowHours > 0, "analysis.event.trendWindowHours 必须大于 0。");
        Require(config.Analysis.Event.StaleHours > 0, "analysis.event.staleHours 必须大于 0。");
        Require(config.Analysis.Event.ArchiveRecallDays > 0, "analysis.event.archiveRecallDays 必须大于 0。");
        Require(config.Analysis.Event.CandidateLimit > 0, "analysis.event.candidateLimit 必须大于 0。");
        Require(IsRatio(config.Analysis.Event.MergeThreshold), "analysis.event.mergeThreshold 必须在 0 到 1 之间。");
        Require(IsRatio(config.Analysis.Event.RuleMergeThreshold), "analysis.event.ruleMergeThreshold 必须在 0 到 1 之间。");
        Require(IsRatio(config.Analysis.Event.StaleMergeThreshold), "analysis.event.staleMergeThreshold 必须在 0 到 1 之间。");
        Require(config.Analysis.Event.MinTrendSamples > 0, "analysis.event.minTrendSamples 必须大于 0。");
        Require(config.Analysis.Event.MinTrendHeat >= 0, "analysis.event.minTrendHeat 不能为负数。");

        Require(config.Enrichment.MaxRequestsPerRun >= 0, "enrichment.maxRequestsPerRun 不能为负数。");
        Require(config.Enrichment.MinTitleLength > 0, "enrichment.minTitleLength 必须大于 0。");
        Require(IsRatio(config.Enrichment.RecallWeakScoreThreshold), "enrichment.recallWeakScoreThreshold 必须在 0 到 1 之间。");
        Require(config.Enrichment.RetryCooldownHours >= 0, "enrichment.retryCooldownHours 不能为负数。");

        if (config.Report.Enabled)
        {
            Require(!string.IsNullOrWhiteSpace(config.Report.OutputDirectory), "report.outputDirectory 不能为空。");
            if (config.Report.IncludeInDigestPush)
            {
                Require(IsAbsoluteHttpUrl(config.Report.PublicBaseUrl), "report.publicBaseUrl 必须是 http 或 https 绝对 URL，才能在摘要推送中包含报告链接。");
            }
        }

        ValidateLlmPricing("llm.cluster.pricing", config.Llm.Cluster.Pricing);
        ValidateLlmPricing("llm.judge.pricing", config.Llm.Judge.Pricing);
        ValidateLlmPricing("llm.tagging.pricing", config.Llm.Tagging.Pricing);
        ValidateLlmPricing("llm.embedding.pricing", config.Llm.Embedding.Pricing);
        if (!string.IsNullOrWhiteSpace(config.Llm.Embedding.BaseUrl) || !string.IsNullOrWhiteSpace(config.Llm.Embedding.ApiKey))
        {
            Require(!string.IsNullOrWhiteSpace(config.Llm.Embedding.Model), "llm.embedding.model 不能为空。");
        }

        Require(config.Llm.Embedding.Dimensions == 1536, "llm.embedding.dimensions 当前必须为 1536。");
        Require(!string.IsNullOrWhiteSpace(config.Llm.Embedding.Version), "llm.embedding.version 不能为空。");
        Require(config.Llm.Embedding.MaxTokens > 0, "llm.embedding.maxTokens 必须大于 0。");
        Require(config.Llm.Embedding.MaxRequestsPerRun >= 0, "llm.embedding.maxRequestsPerRun 不能为负数。");
        Require(IsRatio(config.Llm.Embedding.VectorSimilarityThreshold), "llm.embedding.vectorSimilarityThreshold 必须在 0 到 1 之间。");
        Require(config.Llm.Embedding.VectorCandidateLimit > 0, "llm.embedding.vectorCandidateLimit 必须大于 0。");

        Require(config.System.MaxParallelFetch > 0, "system.maxParallelFetch 必须大于 0。");
        Require(config.System.MaxParallelEnrichment > 0, "system.maxParallelEnrichment 必须大于 0。");
        Require(config.System.MaxParallelLlm > 0, "system.maxParallelLlm 必须大于 0。");

        try
        {
            _ = TimeZoneResolver.Find(config.System.TimeZone);
        }
        catch (TimeZoneNotFoundException)
        {
            errors.Add($"system.timeZone '{config.System.TimeZone}' 在当前系统上未找到。");
        }
        catch (InvalidTimeZoneException)
        {
            errors.Add($"system.timeZone '{config.System.TimeZone}' 在当前系统上无效。");
        }

        if (errors.Count > 0)
        {
            throw new AppConfigValidationException(errors);
        }

        void Require(bool condition, string message)
        {
            if (!condition)
            {
                errors.Add(message);
            }
        }

        void ValidateLlmPricing(string path, LLmPricingConfig pricing)
        {
            Require(IsNonNegativeFinite(pricing.CacheRead), $"{path}.cacheRead 必须是有限且非负的数字。");
            Require(IsNonNegativeFinite(pricing.Input), $"{path}.input 必须是有限且非负的数字。");
            Require(IsNonNegativeFinite(pricing.Output), $"{path}.output 必须是有限且非负的数字。");
        }

        void ValidateSourceProvider(string path, SourceProviderConfig providerConfig)
        {
            if (providerConfig.Items.Any(item => item.Enabled))
            {
                Require(!string.IsNullOrWhiteSpace(providerConfig.BaseUrl), $"{path}.baseUrl 不能为空。");
            }

            for (var index = 0; index < providerConfig.Items.Count; index++)
            {
                var item = providerConfig.Items[index];
                var itemPath = $"{path}.items[{index}]";

                Require(!string.IsNullOrWhiteSpace(item.ExternalId), $"{itemPath}.externalId 不能为空。");
                Require(!string.IsNullOrWhiteSpace(item.Category), $"{itemPath}.category 不能为空。");
                Require(!string.IsNullOrWhiteSpace(item.ContentKind), $"{itemPath}.contentKind 不能为空。");
                Require(ContentKind.IsDefined(item.ContentKind), $"{itemPath}.contentKind 必须是 ranked_news、flash_feed 或 topic。");
                Require(double.IsFinite(item.Weight) && item.Weight > 0, $"{itemPath}.weight 必须是有限且大于 0 的数字。");
            }
        }

        static bool HasEnabledSource(SourcesConfig sources)
            => sources.NewsNow.Items.Any(item => item.Enabled) ||
                sources.DailyHotApi.Items.Any(item => item.Enabled);
    }

    private static bool IsRatio(double value) => value is >= 0 and <= 1;

    private static bool IsNonNegativeFinite(float value) => float.IsFinite(value) && value >= 0;

    private static bool IsAbsoluteHttpUrl(string value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
}
