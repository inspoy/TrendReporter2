using LiteDB;
using Microsoft.Extensions.Logging;
using TrendReporter2.Core.Configuration;
using TrendReporter2.Core.Content;
using TrendReporter2.Core.Enrichment;
using TrendReporter2.Core.Persistence;
using TrendReporter2.Infrastructure.Persistence;

namespace TrendReporter2.Infrastructure.Enrichment;

public sealed class EnrichmentService : IEnrichmentService
{
    private readonly AppConfig _config;
    private readonly LiteDbConnectionFactory _connectionFactory;
    private readonly IEnrichmentClient _enrichmentClient;
    private readonly ILogger _logger;

    public EnrichmentService(
        AppConfig config,
        LiteDbConnectionFactory connectionFactory,
        IEnrichmentClient enrichmentClient,
        ILoggerFactory loggerFactory)
    {
        _config = config;
        _connectionFactory = connectionFactory;
        _enrichmentClient = enrichmentClient;
        _logger = loggerFactory.CreateLogger("Enrichment");
    }

    public async Task<EnrichmentRunResult> EnrichRunAsync(
        string runId,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken)
    {
        var candidates = LoadCandidates(runId, startedAt);
        var limit = Math.Max(0, _config.Enrichment.MaxRequestsPerRun);
        var attempted = 0;
        var succeeded = 0;
        var failed = 0;
        var skipped = 0;

        if (candidates.Count == 0)
        {
            _logger.LogInformation("运行 {RunId} 没有需要富化的内容条目。", runId);
            return new EnrichmentRunResult(0, 0, 0, 0, 0);
        }

        if (string.IsNullOrWhiteSpace(_config.Enrichment.WebExtractUrl))
        {
            foreach (var item in candidates)
            {
                MarkSkipped(item, "未配置富化网页提取 URL。", startedAt);
                skipped++;
            }

            _logger.LogWarning(
                "跳过富化处理，运行编号={RunId}；未配置网页提取 URL。候选数={CandidateCount}。",
                runId,
                candidates.Count);
            return new EnrichmentRunResult(candidates.Count, 0, 0, 0, skipped);
        }

        using var semaphore = new SemaphoreSlim(_config.System.MaxParallelEnrichment);
        var tasks = candidates.Select(async item =>
        {
            if (Volatile.Read(ref attempted) >= limit)
            {
                MarkSkipped(item, "已达到每次运行富化请求上限。", startedAt);
                Interlocked.Increment(ref skipped);
                return;
            }

            await semaphore.WaitAsync(cancellationToken);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (string.IsNullOrWhiteSpace(item.Url))
                {
                    MarkSkipped(item, "内容条目没有 URL。", startedAt);
                    Interlocked.Increment(ref skipped);
                    return;
                }

                if (Interlocked.Increment(ref attempted) > limit)
                {
                    MarkSkipped(item, "已达到每次运行富化请求上限。", startedAt);
                    Interlocked.Increment(ref skipped);
                    return;
                }

                item.EnrichmentTriedAt = startedAt;

                try
                {
                    var result = await _enrichmentClient.EnrichAsync(item, cancellationToken);
                    if (result is null || string.IsNullOrWhiteSpace(result.Summary))
                    {
                        ApplySummaryFallback(item, EnrichmentStatuses.Failed, startedAt, markTried: true);
                        Interlocked.Increment(ref failed);
                        return;
                    }

                    item.Title = string.IsNullOrWhiteSpace(result.Title) ? item.Title : result.Title.Trim();
                    item.Url = string.IsNullOrWhiteSpace(result.Url) ? item.Url : result.Url.Trim();
                    var summary = BuildPreferredSummary(item, result.Summary);
                    item.Summary = summary.Value;
                    item.SummarySource = summary.Source;
                    item.EnrichmentStatus = EnrichmentStatuses.Succeeded;
                    item.UpdatedAt = startedAt;
                    Save(item);
                    Interlocked.Increment(ref succeeded);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(
                        ex,
                        "富化处理失败，内容条目编号={ContentItemId}，运行编号={RunId}。",
                        item.Id,
                        runId);
                    ApplySummaryFallback(item, EnrichmentStatuses.Failed, startedAt, markTried: true);
                    Interlocked.Increment(ref failed);
                }
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);

        _logger.LogInformation(
            "富化处理完成，运行编号={RunId}。候选={CandidateCount}，已尝试={AttemptedCount}，成功={SucceededCount}，失败={FailedCount}，跳过={SkippedCount}。",
            runId,
            candidates.Count,
            attempted,
            succeeded,
            failed,
            skipped);

        return new EnrichmentRunResult(candidates.Count, attempted, succeeded, failed, skipped);
    }

    private List<ContentItem> LoadCandidates(string runId, DateTimeOffset now)
    {
        var cooldownCutoff = now.AddHours(-Math.Max(0, _config.Enrichment.RetryCooldownHours));

        using var database = _connectionFactory.Open();
        var collection = database.GetCollection<ContentItem>(TrendCollectionNames.ContentItem);
        return collection
            .Find(item =>
                item.LastSeenRunId == runId &&
                item.NeedEnrichment &&
                item.EnrichmentStatus != EnrichmentStatuses.Succeeded &&
                (item.EnrichmentTriedAt == null || item.EnrichmentTriedAt <= cooldownCutoff))
            .OrderBy(item => item.LastSeenRank)
            .ThenBy(item => item.Source)
            .ToList();
    }

    private void MarkSkipped(ContentItem item, string reason, DateTimeOffset now)
    {
        _logger.LogInformation(
            "跳过富化处理，内容条目编号={ContentItemId}。原因={Reason}",
            item.Id,
            reason);
        ApplySummaryFallback(item, EnrichmentStatuses.Skipped, now, markTried: false);
    }

    private void ApplySummaryFallback(ContentItem item, string status, DateTimeOffset now, bool markTried)
    {
        var summary = BuildPreferredSummary(item, enrichmentSummary: null);
        item.Summary = summary.Value;
        item.SummarySource = summary.Source;
        item.EnrichmentStatus = status;
        if (markTried)
        {
            item.EnrichmentTriedAt = now;
        }

        item.UpdatedAt = now;
        Save(item);
    }

    private void Save(ContentItem item)
    {
        using var database = _connectionFactory.Open();
        database.GetCollection<ContentItem>(TrendCollectionNames.ContentItem).Update(item);
    }

    private static (string Value, string Source) BuildPreferredSummary(ContentItem item, string? enrichmentSummary)
    {
        if (!string.IsNullOrWhiteSpace(item.HoverText))
        {
            return (item.HoverText.Trim(), SummarySources.HoverText);
        }

        return string.IsNullOrWhiteSpace(enrichmentSummary)
            ? (item.Title.Trim(), SummarySources.TitleOnly)
            : (enrichmentSummary.Trim(), SummarySources.Enrichment);
    }
}
