using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using TrendReporter2.Core.Configuration;
using TrendReporter2.Core.Content;
using TrendReporter2.Core.Enrichment;
using TrendReporter2.Core.Events;
using TrendReporter2.Core.Fetch;
using TrendReporter2.Core.Jobs;
using TrendReporter2.Core.News;
using TrendReporter2.Core.Observability;

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
    private readonly IRunTelemetryRecorder _telemetryRecorder;
    private readonly ILogger _logger;

    public FetchJob(
        AppConfig config,
        INewsSourceClient newsSourceClient,
        IContentIngestService contentIngestService,
        IEnrichmentService enrichmentService,
        IEventMatcher eventMatcher,
        IEventScoringService eventScoringService,
        IFetchRunRepository fetchRunRepository,
        IRunTelemetryRecorder telemetryRecorder,
        ILoggerFactory loggerFactory)
    {
        _config = config;
        _newsSourceClient = newsSourceClient;
        _contentIngestService = contentIngestService;
        _enrichmentService = enrichmentService;
        _eventMatcher = eventMatcher;
        _eventScoringService = eventScoringService;
        _fetchRunRepository = fetchRunRepository;
        _telemetryRecorder = telemetryRecorder;
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
            var sourceResults = await RecordStageAsync(
                fetchRun.Id,
                RunStageNames.Fetch,
                () => FetchSourcesAsync(fetchRun.Id, sources, cancellationToken),
                cancellationToken);
            foreach (var sourceResult in sourceResults.Where(result => result.Success))
            {
                _logger.LogInformation(
                    "NewsNow 来源抓取成功，来源={Source}，分类={Category}，条目={ItemCount}。",
                    sourceResult.Source,
                    sourceResult.Category,
                    sourceResult.Items.Count);
            }

            var allItems = sourceResults.Where(result => result.Success).SelectMany(result => result.Items).ToList();
            var ingestResult = await RecordStageAsync(
                fetchRun.Id,
                RunStageNames.Ingest,
                () => _contentIngestService.IngestAsync(fetchRun.Id, allItems, DateTimeOffset.UtcNow, cancellationToken),
                cancellationToken);
            EnrichmentRunResult enrichmentResult;
            try
            {
                enrichmentResult = await RecordStageAsync(
                    fetchRun.Id,
                    RunStageNames.Enrich,
                    () => EnrichRunAsync(fetchRun.Id, startedAt, cancellationToken),
                    cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "富化处理失败，运行编号={RunId}；抓取流程将继续。", fetchRun.Id);
                enrichmentResult = new EnrichmentRunResult(0, 0, 0, 1, 0);
            }

            EventMatchRunResult eventMatchResult;
            try
            {
                eventMatchResult = await RecordStageAsync(
                    fetchRun.Id,
                    RunStageNames.Match,
                    () => MatchEventsAsync(fetchRun.Id, DateTimeOffset.UtcNow, cancellationToken),
                    cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "事件匹配失败，运行编号={RunId}；抓取流程将继续。", fetchRun.Id);
                eventMatchResult = new EventMatchRunResult(0, 0, 0, 0, 0, 0);
            }

            EventScoringRunResult scoringResult;
            try
            {
                scoringResult = await RecordStageAsync(
                    fetchRun.Id,
                    RunStageNames.Score,
                    () => ScoreAndPushRunAsync(fetchRun.Id, startedAt, DateTimeOffset.UtcNow, cancellationToken),
                    cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "事件评分和即时推送失败，运行编号={RunId}；抓取流程将继续。", fetchRun.Id);
                scoringResult = new EventScoringRunResult(0, 0, 0);
            }
            await RecordSkippedStageAsync(fetchRun.Id, RunStageNames.Push, "即时推送当前包含在 score 阶段执行。", cancellationToken);

            fetchRun.SuccessSourceCount = sourceResults.Count(result => result.Success);
            fetchRun.FailureSourceCount = sourceResults.Count(result => !result.Success);
            fetchRun.FetchedItemCount = ingestResult.TotalCount;
            fetchRun.EnrichedItemCount = enrichmentResult.SucceededCount;
            fetchRun.MatchedEventCount = eventMatchResult.MappedItemCount;
            fetchRun.PushedEventCount = scoringResult.PushedEventCount;
            var llmUsageSummary = await _telemetryRecorder.GetLlmUsageSummaryAsync(fetchRun.Id, cancellationToken);
            fetchRun.EstimatedLlmCost = llmUsageSummary.EstimatedCost;
            fetchRun.Errors = sourceResults
                .Where(result => !result.Success && !string.IsNullOrWhiteSpace(result.Error))
                .Select(result => $"{result.Category}/{result.Source}: {result.Error}")
                .ToList();
            fetchRun.Status = DetermineStatus(fetchRun);
            fetchRun.FinishedAt = DateTimeOffset.UtcNow;

            await _fetchRunRepository.CompleteAsync(fetchRun, cancellationToken);

            var duration = (fetchRun.FinishedAt.Value - fetchRun.StartedAt).TotalSeconds;
            _logger.LogInformation(
                "抓取运行 {RunId} 已完成，耗时 {Cost:F1}秒。状态={Status}，成功来源={SuccessSourceCount}，失败来源={FailureSourceCount}，条目={FetchedItemCount}，新增={InsertedCount}，更新={UpdatedCount}，快照={SnapshotCount}，富化候选={EnrichmentCandidateCount}，已富化={EnrichedItemCount}，富化失败={EnrichmentFailedCount}，富化跳过={EnrichmentSkippedCount}，匹配事件={MatchedEventCount}，新建事件={CreatedEventCount}，合并事件={MergedEventCount}，重新激活={ReactivatedEventCount}，评分事件={ScoredEventCount}，合格事件={EligibleEventCount}，已推送={PushedEventCount}，LLM调用={LlmCallCount}，估算成本={EstimatedLlmCost:F8}。",
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
                scoringResult.PushedEventCount,
                llmUsageSummary.CallCount,
                llmUsageSummary.EstimatedCost);
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

    private Task<EnrichmentRunResult> EnrichRunAsync(
        string runId,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken)
        => _enrichmentService.EnrichRunAsync(runId, startedAt, cancellationToken);

    private Task<EventMatchRunResult> MatchEventsAsync(
        string runId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
        => _eventMatcher.MatchRunAsync(runId, now, cancellationToken);

    private Task<EventScoringRunResult> ScoreAndPushRunAsync(
        string runId,
        DateTimeOffset startedAt,
        DateTimeOffset now,
        CancellationToken cancellationToken)
        => _eventScoringService.ScoreAndPushRunAsync(runId, startedAt, now, cancellationToken);

    private async Task<List<SourceFetchResult>> FetchSourcesAsync(
        string runId,
        IReadOnlyList<ConfiguredSource> sources,
        CancellationToken cancellationToken)
    {
        using var semaphore = new SemaphoreSlim(_config.System.MaxParallelFetch);
        var tasks = sources.Select(source => FetchSourceAsync(runId, source, semaphore, cancellationToken));
        var results = await Task.WhenAll(tasks);
        return results.ToList();
    }

    private async Task<SourceFetchResult> FetchSourceAsync(
        string runId,
        ConfiguredSource source,
        SemaphoreSlim semaphore,
        CancellationToken cancellationToken)
    {
        await semaphore.WaitAsync(cancellationToken);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var items = await _newsSourceClient.FetchAsync(source.Category, source.Source, cancellationToken);
            var result = SourceFetchResult.Succeeded(source.Category, source.Source, items);
            await RecordSourceAsync(runId, result, stopwatch.ElapsedMilliseconds, cancellationToken);
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "抓取 NewsNow 来源失败，来源={Source}，分类={Category}。",
                source.Source,
                source.Category);
            var result = SourceFetchResult.Failed(source.Category, source.Source, ex);
            await RecordSourceAsync(runId, result, stopwatch.ElapsedMilliseconds, cancellationToken);
            return result;
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

    private async Task<T> RecordStageAsync<T>(
        string runId,
        string stage,
        Func<Task<T>> action,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var result = await action();
            await _telemetryRecorder.RecordStageAsync(new RunStageTelemetry(
                BuildStageId(runId, stage, startedAt),
                runId,
                stage,
                startedAt,
                DateTimeOffset.UtcNow,
                ToDurationMs(stopwatch.ElapsedMilliseconds),
                RunTelemetryStatuses.Succeeded,
                null), cancellationToken);
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await _telemetryRecorder.RecordStageAsync(new RunStageTelemetry(
                BuildStageId(runId, stage, startedAt),
                runId,
                stage,
                startedAt,
                DateTimeOffset.UtcNow,
                ToDurationMs(stopwatch.ElapsedMilliseconds),
                RunTelemetryStatuses.Failed,
                ex.Message), CancellationToken.None);
            throw;
        }
    }

    private Task RecordSkippedStageAsync(string runId, string stage, string reason, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        return _telemetryRecorder.RecordStageAsync(new RunStageTelemetry(
            BuildStageId(runId, stage, now),
            runId,
            stage,
            now,
            now,
            0,
            RunTelemetryStatuses.Skipped,
            reason), cancellationToken);
    }

    private Task RecordSourceAsync(
        string runId,
        SourceFetchResult result,
        long durationMs,
        CancellationToken cancellationToken)
        => _telemetryRecorder.RecordSourceAsync(new RunSourceTelemetry(
            runId,
            BuildSourceId(result.Category, result.Source),
            result.Category,
            result.Source,
            result.Success ? RunTelemetryStatuses.Succeeded : RunTelemetryStatuses.Failed,
            ToDurationMs(durationMs),
            result.Items.Count,
            result.Error,
            DateTimeOffset.UtcNow), cancellationToken);

    private static string BuildSourceId(string category, string source)
        => $"{category}/{source}";

    private static string BuildStageId(string runId, string stage, DateTimeOffset startedAt)
        => $"frs:{ShortHash($"{runId}|{stage}|{startedAt.UtcTicks}")}";

    private static int ToDurationMs(long elapsedMs)
        => elapsedMs > int.MaxValue ? int.MaxValue : Math.Max(0, (int)elapsedMs);

    private static string ShortHash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..24].ToLowerInvariant();
}
