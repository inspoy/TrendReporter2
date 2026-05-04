using Microsoft.Extensions.Logging;
using TrendReporter2.Core.Configuration;
using TrendReporter2.Core.Content;
using TrendReporter2.Core.Enrichment;
using TrendReporter2.Core.Events;
using TrendReporter2.Core.Fetch;
using TrendReporter2.Core.Jobs;
using TrendReporter2.Core.News;

namespace TrendReporter2.App.Scheduling;

public sealed class FetchJob : IFetchJob
{
    private readonly AppConfig _config;
    private readonly INewsSourceClient _newsSourceClient;
    private readonly IContentIngestService _contentIngestService;
    private readonly IEnrichmentService _enrichmentService;
    private readonly IEventMatcher _eventMatcher;
    private readonly IEventScoringService _eventScoringService;
    private readonly IFetchRunRepository _fetchRunRepository;
    private readonly ILogger _logger;

    public FetchJob(
        AppConfig config,
        INewsSourceClient newsSourceClient,
        IContentIngestService contentIngestService,
        IEnrichmentService enrichmentService,
        IEventMatcher eventMatcher,
        IEventScoringService eventScoringService,
        IFetchRunRepository fetchRunRepository,
        ILoggerFactory loggerFactory)
    {
        _config = config;
        _newsSourceClient = newsSourceClient;
        _contentIngestService = contentIngestService;
        _enrichmentService = enrichmentService;
        _eventMatcher = eventMatcher;
        _eventScoringService = eventScoringService;
        _fetchRunRepository = fetchRunRepository;
        _logger = loggerFactory.CreateLogger("FetchJob");
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var sources = GetConfiguredSources();
        var startedAt = DateTimeOffset.UtcNow;
        var fetchRun = await _fetchRunRepository.CreateAsync(sources.Count, startedAt, cancellationToken);

        _logger.LogInformation(
            "抓取运行 {RunId} 已开始。来源数={SourceCount}。",
            fetchRun.Id,
            sources.Count);

        try
        {
            var sourceResults = await FetchSourcesAsync(sources, cancellationToken);
            var allItems = sourceResults.Where(result => result.Success).SelectMany(result => result.Items).ToList();
            var ingestResult = await _contentIngestService.IngestAsync(
                fetchRun.Id,
                allItems,
                DateTimeOffset.UtcNow,
                cancellationToken);
            var enrichmentResult = await EnrichRunAsync(fetchRun.Id, startedAt, cancellationToken);
            var eventMatchResult = await MatchEventsAsync(fetchRun.Id, DateTimeOffset.UtcNow, cancellationToken);
            var scoringResult = await ScoreAndPushRunAsync(fetchRun.Id, startedAt, DateTimeOffset.UtcNow, cancellationToken);

            fetchRun.SuccessSourceCount = sourceResults.Count(result => result.Success);
            fetchRun.FailureSourceCount = sourceResults.Count(result => !result.Success);
            fetchRun.FetchedItemCount = ingestResult.TotalCount;
            fetchRun.EnrichedItemCount = enrichmentResult.SucceededCount;
            fetchRun.MatchedEventCount = eventMatchResult.MappedItemCount;
            fetchRun.PushedEventCount = scoringResult.PushedEventCount;
            fetchRun.Errors = sourceResults
                .Where(result => !result.Success && !string.IsNullOrWhiteSpace(result.Error))
                .Select(result => $"{result.Category}/{result.Source}: {result.Error}")
                .ToList();
            fetchRun.Status = DetermineStatus(fetchRun);
            fetchRun.FinishedAt = DateTimeOffset.UtcNow;

            await _fetchRunRepository.CompleteAsync(fetchRun, cancellationToken);

            var duration = (fetchRun.FinishedAt.Value - fetchRun.StartedAt).TotalSeconds;
            _logger.LogInformation(
                "抓取运行 {RunId} 已完成，耗时 {Cost:F1}秒。状态={Status}，成功来源={SuccessSourceCount}，失败来源={FailureSourceCount}，条目={FetchedItemCount}，新增={InsertedCount}，更新={UpdatedCount}，快照={SnapshotCount}，富化候选={EnrichmentCandidateCount}，已富化={EnrichedItemCount}，富化失败={EnrichmentFailedCount}，富化跳过={EnrichmentSkippedCount}，匹配事件={MatchedEventCount}，新建事件={CreatedEventCount}，合并事件={MergedEventCount}，重新激活={ReactivatedEventCount}，评分事件={ScoredEventCount}，合格事件={EligibleEventCount}，已推送={PushedEventCount}。",
                fetchRun.Id,
                duration,
                fetchRun.Status,
                fetchRun.SuccessSourceCount,
                fetchRun.FailureSourceCount,
                fetchRun.FetchedItemCount,
                ingestResult.InsertedCount,
                ingestResult.UpdatedCount,
                ingestResult.SnapshotCount,
                enrichmentResult.CandidateCount,
                enrichmentResult.SucceededCount,
                enrichmentResult.FailedCount,
                enrichmentResult.SkippedCount,
                eventMatchResult.MappedItemCount,
                eventMatchResult.CreatedEventCount,
                eventMatchResult.MergedEventCount,
                eventMatchResult.ReactivatedEventCount,
                scoringResult.ScoredEventCount,
                scoringResult.EligibleEventCount,
                scoringResult.PushedEventCount);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            fetchRun.Status = FetchRunStatuses.Failed;
            fetchRun.FinishedAt = DateTimeOffset.UtcNow;
            fetchRun.Errors.Add(ex.Message);
            await _fetchRunRepository.CompleteAsync(fetchRun, CancellationToken.None);

            var duration = (fetchRun.FinishedAt.Value - fetchRun.StartedAt).TotalSeconds;
            _logger.LogError(ex, "抓取运行 {RunId} 失败，耗时 {Cost:F1}秒。", fetchRun.Id, duration);
        }
    }

    private async Task<EnrichmentRunResult> EnrichRunAsync(
        string runId,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _enrichmentService.EnrichRunAsync(runId, startedAt, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "富化处理失败，运行编号={RunId}；抓取流程将继续。", runId);
            return new EnrichmentRunResult(0, 0, 0, 1, 0);
        }
    }

    private async Task<EventMatchRunResult> MatchEventsAsync(
        string runId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _eventMatcher.MatchRunAsync(runId, now, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "事件匹配失败，运行编号={RunId}；抓取流程将继续。", runId);
            return new EventMatchRunResult(0, 0, 0, 0, 0, 0);
        }
    }

    private async Task<EventScoringRunResult> ScoreAndPushRunAsync(
        string runId,
        DateTimeOffset startedAt,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _eventScoringService.ScoreAndPushRunAsync(runId, startedAt, now, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "事件评分和即时推送失败，运行编号={RunId}；抓取流程将继续。", runId);
            return new EventScoringRunResult(0, 0, 0);
        }
    }

    private async Task<List<SourceFetchResult>> FetchSourcesAsync(
        IReadOnlyList<ConfiguredSource> sources,
        CancellationToken cancellationToken)
    {
        using var semaphore = new SemaphoreSlim(_config.System.MaxParallelFetch);
        var tasks = sources.Select(source => FetchSourceAsync(source, semaphore, cancellationToken));
        var results = await Task.WhenAll(tasks);
        return results.ToList();
    }

    private async Task<SourceFetchResult> FetchSourceAsync(
        ConfiguredSource source,
        SemaphoreSlim semaphore,
        CancellationToken cancellationToken)
    {
        await semaphore.WaitAsync(cancellationToken);
        try
        {
            var items = await _newsSourceClient.FetchAsync(source.Category, source.Source, cancellationToken);
            return SourceFetchResult.Succeeded(source.Category, source.Source, items);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "抓取 NewsNow 来源失败，来源={Source}，分类={Category}。",
                source.Source,
                source.Category);
            return SourceFetchResult.Failed(source.Category, source.Source, ex);
        }
        finally
        {
            semaphore.Release();
        }
    }

    private List<ConfiguredSource> GetConfiguredSources()
    {
        return _config.NewsNow.Sources
            .SelectMany(category => category.Value.Select(source => new ConfiguredSource(category.Key, source)))
            .Where(source => !string.IsNullOrWhiteSpace(source.Source))
            .ToList();
    }

    private static string DetermineStatus(FetchRun fetchRun)
    {
        if (fetchRun.SourceCount > 0 && fetchRun.SuccessSourceCount == fetchRun.SourceCount)
        {
            return FetchRunStatuses.Succeeded;
        }

        return fetchRun.SuccessSourceCount > 0 ? FetchRunStatuses.Partial : FetchRunStatuses.Failed;
    }

    private sealed record ConfiguredSource(string Category, string Source);
}
