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
    private readonly ITavilyClient _tavilyClient;
    private readonly ILogger<EnrichmentService> _logger;

    public EnrichmentService(
        AppConfig config,
        LiteDbConnectionFactory connectionFactory,
        ITavilyClient tavilyClient,
        ILogger<EnrichmentService> logger)
    {
        _config = config;
        _connectionFactory = connectionFactory;
        _tavilyClient = tavilyClient;
        _logger = logger;
    }

    public async Task<EnrichmentRunResult> EnrichRunAsync(
        string runId,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken)
    {
        var candidates = LoadCandidates(runId, startedAt);
        var limit = Math.Max(0, _config.Tavily.MaxRequestsPerRun);
        var attempted = 0;
        var succeeded = 0;
        var failed = 0;
        var skipped = 0;

        if (candidates.Count == 0)
        {
            _logger.LogInformation("No content items need enrichment for run={RunId}.", runId);
            return new EnrichmentRunResult(0, 0, 0, 0, 0);
        }

        if (string.IsNullOrWhiteSpace(_config.Tavily.ApiKey))
        {
            skipped = MarkSkipped(candidates, "Tavily API key is not configured.", startedAt);
            _logger.LogWarning(
                "Skipped Tavily enrichment for run={RunId}; API key is not configured. CandidateCount={CandidateCount}.",
                runId,
                candidates.Count);
            return new EnrichmentRunResult(candidates.Count, 0, 0, 0, skipped);
        }

        foreach (var item in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (attempted >= limit)
            {
                skipped += MarkSkipped(item, "Per-run Tavily request limit reached.", startedAt);
                continue;
            }

            if (string.IsNullOrWhiteSpace(item.Url))
            {
                skipped += MarkSkipped(item, "Content item has no URL.", startedAt);
                continue;
            }

            attempted++;
            item.EnrichmentTriedAt = startedAt;

            try
            {
                var result = await _tavilyClient.EnrichAsync(item, cancellationToken);
                if (result is null || string.IsNullOrWhiteSpace(result.Summary))
                {
                    ApplyTitleOnlyFallback(item, EnrichmentStatuses.Failed, startedAt, markTried: true);
                    failed++;
                    continue;
                }

                item.Title = string.IsNullOrWhiteSpace(result.Title) ? item.Title : result.Title.Trim();
                item.Url = string.IsNullOrWhiteSpace(result.Url) ? item.Url : result.Url.Trim();
                item.Summary = result.Summary.Trim();
                item.SummarySource = SummarySources.Tavily;
                item.EnrichmentStatus = EnrichmentStatuses.Succeeded;
                item.UpdatedAt = startedAt;
                Save(item);
                succeeded++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(
                    ex,
                    "Tavily enrichment failed for contentItemId={ContentItemId}, run={RunId}.",
                    item.Id,
                    runId);
                ApplyTitleOnlyFallback(item, EnrichmentStatuses.Failed, startedAt, markTried: true);
                failed++;
            }
        }

        _logger.LogInformation(
            "Tavily enrichment finished for run={RunId}. Candidates={CandidateCount}, Attempted={AttemptedCount}, Succeeded={SucceededCount}, Failed={FailedCount}, Skipped={SkippedCount}.",
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
        var cooldownCutoff = now.AddHours(-Math.Max(0, _config.Tavily.RetryCooldownHours));

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

    private int MarkSkipped(IEnumerable<ContentItem> items, string reason, DateTimeOffset now)
    {
        var count = 0;
        foreach (var item in items)
        {
            count += MarkSkipped(item, reason, now);
        }

        return count;
    }

    private int MarkSkipped(ContentItem item, string reason, DateTimeOffset now)
    {
        _logger.LogInformation(
            "Skipped enrichment for contentItemId={ContentItemId}. Reason={Reason}",
            item.Id,
            reason);
        ApplyTitleOnlyFallback(item, EnrichmentStatuses.Skipped, now, markTried: false);
        return 1;
    }

    private void ApplyTitleOnlyFallback(ContentItem item, string status, DateTimeOffset now, bool markTried)
    {
        item.Summary = BuildTitleOnlySummary(item);
        item.SummarySource = SummarySources.TitleOnly;
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

    private static string BuildTitleOnlySummary(ContentItem item)
        => string.IsNullOrWhiteSpace(item.HoverText)
            ? item.Title.Trim()
            : $"{item.Title.Trim()} {item.HoverText.Trim()}";
}
