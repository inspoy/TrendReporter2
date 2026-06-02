using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using TrendReporter2.Core.Configuration;
using TrendReporter2.Core.Content;
using TrendReporter2.Core.Embeddings;
using TrendReporter2.Core.Observability;
using TrendReporter2.Core.Tags;

namespace TrendReporter2.Core.Events;

public sealed class SecondaryMergeService : ISecondaryMergeService
{
    private const int RepresentativeTitleLimit = 3;
    private const int KeyTermLimit = 12;
    private const int EntityLimit = 8;
    private const int AliasLimit = 8;
    private const int PlaceLimit = 8;

    private readonly AppConfig _config;
    private readonly IEventRepository _eventRepository;
    private readonly IEmbeddingRepository _embeddingRepository;
    private readonly IEventMergeRepository _eventMergeRepository;
    private readonly IClusterLlmClient _clusterLlmClient;
    private readonly IEventScoringService _eventScoringService;
    private readonly ITagRepository _tagRepository;
    private readonly IEmbeddingService _embeddingService;
    private readonly ILogger _logger;

    public SecondaryMergeService(
        AppConfig config,
        IEventRepository eventRepository,
        IEmbeddingRepository embeddingRepository,
        IEventMergeRepository eventMergeRepository,
        IClusterLlmClient clusterLlmClient,
        IEventScoringService eventScoringService,
        ITagRepository tagRepository,
        IEmbeddingService embeddingService,
        ILoggerFactory loggerFactory)
    {
        _config = config;
        _eventRepository = eventRepository;
        _embeddingRepository = embeddingRepository;
        _eventMergeRepository = eventMergeRepository;
        _clusterLlmClient = clusterLlmClient;
        _eventScoringService = eventScoringService;
        _tagRepository = tagRepository;
        _embeddingService = embeddingService;
        _logger = loggerFactory.CreateLogger("SecondaryMerge");
    }

    public async Task<SecondaryMergeRunResult> MergeRunAsync(string runId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_config.Llm.Embedding.Model))
        {
            _logger.LogInformation("二次归并跳过，embedding 未配置，运行编号={RunId}。", runId);
            return new SecondaryMergeRunResult(0, 0, 0, 0);
        }

        var events = await _eventRepository.LoadMergeCandidateEventsAsync(
            now,
            _config.Analysis.HistoryHours,
            _config.Analysis.Event.ArchiveRecallDays,
            cancellationToken);
        if (events.Count < 2)
        {
            return new SecondaryMergeRunResult(0, 0, 0, 0);
        }

        var candidates = await BuildCandidatePairsAsync(events, now, cancellationToken);
        var hardFilterExcluded = 0;
        var llmDecided = 0;
        var merged = 0;

        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (SecondaryMergeHardFilters.ShouldExclude(candidate, out var hardFilterReason))
            {
                hardFilterExcluded++;
                _logger.LogInformation(
                    "二次归并硬过滤排除，来源事件={SourceEventId}，目标事件={TargetEventId}，原因={Reason}。",
                    candidate.SourceEvent.Id,
                    candidate.TargetEvent.Id,
                    hardFilterReason);
                continue;
            }

            if (!_clusterLlmClient.IsConfigured)
            {
                continue;
            }

            EventMergeDecision decision;
            try
            {
                decision = await DecideWithLlmAsync(runId, candidate, now, cancellationToken);
                llmDecided++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(
                    ex,
                    "二次归并 LLM 判定失败，来源事件={SourceEventId}，目标事件={TargetEventId}；跳过该候选对。",
                    candidate.SourceEvent.Id,
                    candidate.TargetEvent.Id);
                continue;
            }

            if (!decision.ShouldMerge || decision.Confidence < _config.Analysis.Event.MergeLlmConfidenceThreshold)
            {
                continue;
            }

            await MergeEventsAsync(runId, candidate.SourceEvent, candidate.TargetEvent, candidate, decision, now, cancellationToken);
            merged++;
        }

        return new SecondaryMergeRunResult(candidates.Count, hardFilterExcluded, llmDecided, merged);
    }

    private async Task<IReadOnlyList<EventMergeCandidate>> BuildCandidatePairsAsync(IReadOnlyList<EventAggregate> events, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var pairs = new List<EventMergeCandidate>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var eventsById = events.ToDictionary(eventAggregate => eventAggregate.Id, StringComparer.Ordinal);
        foreach (var left in events)
        {
            var embedding = await _embeddingRepository.GetEventEmbeddingAsync(
                left.Id,
                _config.Llm.Embedding.Model,
                _config.Llm.Embedding.Version,
                _config.Llm.Embedding.Dimensions,
                cancellationToken);
            if (embedding is null)
            {
                continue;
            }

            var similarEvents = await _embeddingRepository.QuerySimilarEventsAsync(
                embedding.Embedding,
                _config.Llm.Embedding.Model,
                _config.Llm.Embedding.Version,
                _config.Llm.Embedding.Dimensions,
                now,
                _config.Analysis.HistoryHours,
                _config.Analysis.Event.ArchiveRecallDays,
                _config.Analysis.Event.MergeSimilarityThreshold,
                _config.Analysis.Event.MergeCandidateLimit,
                cancellationToken);
            foreach (var similarEvent in similarEvents)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!eventsById.TryGetValue(similarEvent.Event.Id, out var right) || left.Id == right.Id || left.MergedIntoEventId is not null || right.MergedIntoEventId is not null)
                {
                    continue;
                }

                var pairKey = BuildPairKey(left.Id, right.Id);
                if (!seen.Add(pairKey) || await _eventMergeRepository.HasBeenProcessedAsync(left.Id, right.Id, cancellationToken))
                {
                    continue;
                }

                var source = left.FirstSeenAt <= right.FirstSeenAt ? right : left;
                var target = ReferenceEquals(source, left) ? right : left;
                pairs.Add(new EventMergeCandidate(source, target, similarEvent.Similarity, [similarEvent.Reason]));
            }
        }

        return pairs
            .OrderByDescending(pair => pair.Similarity)
            .ThenByDescending(pair => pair.TargetEvent.LastSeenAt)
            .Take(Math.Max(1, _config.Analysis.Event.MergeCandidateLimit))
            .ToList();
    }

    private async Task<EventMergeDecision> DecideWithLlmAsync(string runId, EventMergeCandidate candidate, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var item = new ContentItem
        {
            Id = $"secondary-merge:{candidate.SourceEvent.Id}",
            DedupKey = candidate.SourceEvent.Id,
            Source = "event",
            Category = RunStageNames.SecondaryMerge,
            Type = "Event",
            ContentKind = "event",
            SourceItemId = candidate.SourceEvent.Id,
            Title = candidate.SourceEvent.CanonicalTitle,
            Summary = JoinNonEmpty(candidate.SourceEvent.CanonicalTitle, candidate.SourceEvent.Summary),
            Url = string.Empty,
            CreatedAt = now,
            UpdatedAt = now
        };
        var result = await _clusterLlmClient.MatchAsync(new ClusterMatchRequest(
            runId,
            item,
            [new EventCandidate(candidate.TargetEvent, candidate.Similarity, candidate.MatchedReasons)]), cancellationToken);
        return result.Decision == ClusterDecisions.SameEvent && result.EventId == candidate.TargetEvent.Id
            ? EventMergeDecision.SameEvent(result.Confidence, result.Reason ?? "二次归并 LLM 判定为同一事件")
            : EventMergeDecision.RelatedButDistinct(result.Confidence, result.Reason ?? result.Decision);
    }

    private async Task MergeEventsAsync(
        string runId,
        EventAggregate sourceEvent,
        EventAggregate targetEvent,
        EventMergeCandidate candidate,
        EventMergeDecision decision,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var mergeHistory = new EventMergeHistory
        {
            Id = BuildMergeHistoryId(sourceEvent.Id, targetEvent.Id, now),
            SourceEventId = sourceEvent.Id,
            TargetEventId = targetEvent.Id,
            Confidence = decision.Confidence,
            Reason = decision.Reason,
            DecidedBy = MergeDecidedBy.Llm,
            EvidenceSnapshot = JsonConvert.SerializeObject(new
            {
                sourceEventId = sourceEvent.Id,
                targetEventId = targetEvent.Id,
                sourceTitle = sourceEvent.CanonicalTitle,
                targetTitle = targetEvent.CanonicalTitle,
                candidate.Similarity,
                candidate.MatchedReasons
            }),
            CreatedAt = now
        };

        await _eventMergeRepository.InsertMergeHistoryAsync(mergeHistory, cancellationToken);
        await _eventRepository.BatchSetEventMergedStatusAsync([sourceEvent.Id], targetEvent.Id, cancellationToken);
        await _eventMergeRepository.MigrateEventItemsAsync(sourceEvent.Id, targetEvent.Id, mergeHistory.Id, now, cancellationToken);
        await _eventMergeRepository.DeactivateEventItemsAsync(sourceEvent.Id, cancellationToken);

        MergeEventEvidence(targetEvent, sourceEvent, now);
        await MergeTagsAsync(sourceEvent.Id, targetEvent.Id, now, cancellationToken);
        await _eventRepository.UpsertEventAsync(targetEvent, cancellationToken);
        await _embeddingService.GenerateEventEmbeddingsAsync(runId, now, 1, cancellationToken);
        await WriteSecondaryMergeScoreSnapshotAsync(runId, targetEvent.Id, now, cancellationToken);

        _logger.LogInformation(
            "二次归并已合并事件，来源事件={SourceEventId}，目标事件={TargetEventId}，置信度={Confidence:F2}，原因={Reason}。",
            sourceEvent.Id,
            targetEvent.Id,
            decision.Confidence,
            decision.Reason);
    }

    private async Task MergeTagsAsync(string sourceEventId, string targetEventId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var tags = await _tagRepository.LoadEventTagsAsync([sourceEventId, targetEventId], cancellationToken);
        var assignments = tags
            .Where(tag => tag.EventId == sourceEventId)
            .Select(tag => new TagAssignment(tag.Tag.Name, tag.Tag.DisplayName, tag.Tag.Category, tag.Source, tag.Confidence))
            .ToList();
        await _tagRepository.UpsertEventTagsAsync(targetEventId, assignments, now, cancellationToken);
    }

    private async Task WriteSecondaryMergeScoreSnapshotAsync(string runId, string eventId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var recent = await _eventRepository.LoadRecentScoreSnapshotsAsync([eventId], now.AddHours(-Math.Max(1, _config.Analysis.Event.TrendWindowHours)), cancellationToken);
        var latest = recent.OrderByDescending(snapshot => snapshot.CalculatedAt).FirstOrDefault();
        if (latest is null)
        {
            return;
        }

        var triggerReasons = latest.TriggerReasons.ToList();
        if (!triggerReasons.Contains(RunStageNames.SecondaryMerge, StringComparer.OrdinalIgnoreCase))
        {
            triggerReasons.Add(RunStageNames.SecondaryMerge);
        }

        await _eventRepository.InsertEventScoreSnapshotAsync(new EventScoreSnapshot
        {
            Id = $"ess:{runId}:{eventId}:secondary_merge",
            EventId = eventId,
            RunId = runId,
            CalculatedAt = now,
            CoverageScore = latest.CoverageScore,
            RankScore = latest.RankScore,
            FlashScore = latest.FlashScore,
            FreshnessScore = latest.FreshnessScore,
            TrendScore = latest.TrendScore,
            PersistenceScore = latest.PersistenceScore,
            LlmBoostScore = latest.LlmBoostScore,
            ReactivationBonus = latest.ReactivationBonus,
            TotalScore = latest.TotalScore,
            UniqueSourceCount = latest.UniqueSourceCount,
            RankedSourceCount = latest.RankedSourceCount,
            FlashSourceCount = latest.FlashSourceCount,
            AvgRank = latest.AvgRank,
            AvgNormalizedRank = latest.AvgNormalizedRank,
            HeatValue = latest.HeatValue,
            SmoothedHeatValue = latest.SmoothedHeatValue,
            TrendEvidenceCount = latest.TrendEvidenceCount,
            CurrentStage = latest.CurrentStage,
            TriggerReasons = triggerReasons
        }, cancellationToken);
    }

    private static void MergeEventEvidence(EventAggregate targetEvent, EventAggregate sourceEvent, DateTimeOffset now)
    {
        if ((sourceEvent.CanonicalTitle?.Length ?? 0) > (targetEvent.CanonicalTitle?.Length ?? 0))
        {
            targetEvent.CanonicalTitle = sourceEvent.CanonicalTitle ?? string.Empty;
        }

        targetEvent.Summary = JoinNonEmpty(targetEvent.Summary, sourceEvent.Summary);
        AddUnique(targetEvent.Aliases, sourceEvent.Aliases, AliasLimit);
        AddUnique(targetEvent.Entities, sourceEvent.Entities, EntityLimit);
        AddUnique(targetEvent.Places, sourceEvent.Places, PlaceLimit);
        AddUnique(targetEvent.KeyTerms, sourceEvent.KeyTerms, KeyTermLimit);
        AddUnique(targetEvent.RepresentativeTitles, sourceEvent.RepresentativeTitles, RepresentativeTitleLimit);
        AddUnique(targetEvent.RepresentativeTitles, sourceEvent.CanonicalTitle, RepresentativeTitleLimit);
        targetEvent.FirstSeenAt = Min(targetEvent.FirstSeenAt, sourceEvent.FirstSeenAt);
        targetEvent.LastSeenAt = Max(targetEvent.LastSeenAt, sourceEvent.LastSeenAt);
        targetEvent.LastActivatedAt = Max(targetEvent.LastActivatedAt, sourceEvent.LastActivatedAt);
        targetEvent.Status = sourceEvent.Status == EventStatus.Active ? EventStatus.Active : targetEvent.Status;
        targetEvent.UpdatedAt = now;
    }

    private static void AddUnique(List<string> target, IEnumerable<string> values, int limit)
    {
        foreach (var value in values)
        {
            AddUnique(target, value, limit);
        }
    }

    private static void AddUnique(List<string> target, string? value, int limit)
    {
        if (string.IsNullOrWhiteSpace(value) || target.Any(existing => string.Equals(existing, value.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        target.Add(value.Trim());
        if (target.Count > limit)
        {
            target.RemoveRange(limit, target.Count - limit);
        }
    }

    private static DateTimeOffset Min(DateTimeOffset left, DateTimeOffset right)
        => left <= right ? left : right;

    private static DateTimeOffset Max(DateTimeOffset left, DateTimeOffset right)
        => left >= right ? left : right;

    private static string BuildPairKey(string left, string right)
        => string.CompareOrdinal(left, right) <= 0 ? $"{left}|{right}" : $"{right}|{left}";

    private static string BuildMergeHistoryId(string sourceEventId, string targetEventId, DateTimeOffset now)
        => $"emh:{ShortHash($"{sourceEventId}|{targetEventId}|{now:O}")}";

    private static string ShortHash(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value.Trim()));
        return Convert.ToHexString(hash)[..24].ToLowerInvariant();
    }

    private static string JoinNonEmpty(params string?[] values)
        => string.Join('\n', values.Where(value => !string.IsNullOrWhiteSpace(value)).Select(value => value!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase));
}
