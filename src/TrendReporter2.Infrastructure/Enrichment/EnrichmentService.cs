using Microsoft.Extensions.Logging;
using TrendReporter2.Core.Configuration;
using TrendReporter2.Core.Content;
using TrendReporter2.Core.Enrichment;
using TrendReporter2.Infrastructure.Persistence;

namespace TrendReporter2.Infrastructure.Enrichment;

public sealed class EnrichmentService : IEnrichmentService
{
    private readonly AppConfig _config;
    private readonly PostgresContentRepository _contentRepository;
    private readonly IEnrichmentClient _enrichmentClient;
    private readonly ILogger _logger;

    public EnrichmentService(
        AppConfig config,
        PostgresContentRepository contentRepository,
        IEnrichmentClient enrichmentClient,
        ILoggerFactory loggerFactory)
    {
        _config = config;
        _contentRepository = contentRepository;
        _enrichmentClient = enrichmentClient;
        _logger = loggerFactory.CreateLogger("Enrichment");
    }

    public async Task<EnrichmentRunResult> EnrichRunAsync(
        string runId,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken)
    {
        var candidates = await LoadCandidatesAsync(runId, startedAt, cancellationToken);
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
                await MarkSkippedAsync(item, "未配置富化网页提取 URL。", startedAt, cancellationToken);
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
                await MarkSkippedAsync(item, "已达到每次运行富化请求上限。", startedAt, cancellationToken);
                Interlocked.Increment(ref skipped);
                return;
            }

            await semaphore.WaitAsync(cancellationToken);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (string.IsNullOrWhiteSpace(item.Url))
                {
                    await MarkSkippedAsync(item, "内容条目没有 URL。", startedAt, cancellationToken);
                    Interlocked.Increment(ref skipped);
                    return;
                }

                if (Interlocked.Increment(ref attempted) > limit)
                {
                    await MarkSkippedAsync(item, "已达到每次运行富化请求上限。", startedAt, cancellationToken);
                    Interlocked.Increment(ref skipped);
                    return;
                }

                item.EnrichmentTriedAt = startedAt;

                try
                {
                    var result = await _enrichmentClient.EnrichAsync(item, cancellationToken);
                    if (result is null || string.IsNullOrWhiteSpace(result.Summary))
                    {
                        await ApplySummaryFallbackAsync(item, EnrichmentStatuses.Failed, startedAt, markTried: true, cancellationToken);
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
                    await SaveAsync(item, cancellationToken);
                    Interlocked.Increment(ref succeeded);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(
                        ex,
                        "富化处理失败，内容条目编号={ContentItemId}，运行编号={RunId}。",
                        item.Id,
                        runId);
                    await ApplySummaryFallbackAsync(item, EnrichmentStatuses.Failed, startedAt, markTried: true, CancellationToken.None);
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

    private async Task<IReadOnlyList<ContentItem>> LoadCandidatesAsync(string runId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var cooldownCutoff = now.AddHours(-Math.Max(0, _config.Enrichment.RetryCooldownHours));
        return await _contentRepository.LoadEnrichmentCandidatesAsync(runId, cooldownCutoff, cancellationToken);
    }

    private async Task MarkSkippedAsync(ContentItem item, string reason, DateTimeOffset now, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "跳过富化处理，内容条目编号={ContentItemId}。原因={Reason}",
            item.Id,
            reason);
        await ApplySummaryFallbackAsync(item, EnrichmentStatuses.Skipped, now, markTried: false, cancellationToken);
    }

    private async Task ApplySummaryFallbackAsync(ContentItem item, string status, DateTimeOffset now, bool markTried, CancellationToken cancellationToken)
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
        await SaveAsync(item, cancellationToken);
    }

    private async Task SaveAsync(ContentItem item, CancellationToken cancellationToken)
    {
        await _contentRepository.SaveAsync(item, cancellationToken);
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
