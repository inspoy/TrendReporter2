using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using TrendReporter2.Core.Configuration;
using TrendReporter2.Core.Content;

namespace TrendReporter2.Core.Events;

public sealed class EventMatcher : IEventMatcher
{
    private const int RepresentativeTitleLimit = 3;
    private const int KeyTermLimit = 12;
    private const int EntityLimit = 8;
    private const int AliasLimit = 8;
    private const int StableAnchorLimit = 8;
    private const int MinCjkAnchorLength = 2;
    private const int MaxMixedCjkAnchorLength = 4;

    private static readonly Regex StableAnchorTokenRegex = new(@"\b[\p{L}\p{N}][\p{L}\p{N}'&.-]*\b", RegexOptions.Compiled);

    private readonly AppConfig _config;
    private readonly ILogger _logger;
    private readonly IEventRepository _repository;
    private readonly IEventCandidateService _candidateService;
    private readonly IClusterLlmClient _clusterLlmClient;

    public EventMatcher(
        AppConfig config,
        IEventRepository repository,
        IEventCandidateService candidateService,
        IClusterLlmClient clusterLlmClient,
        ILoggerFactory loggerFactory)
    {
        _config = config;
        _repository = repository;
        _candidateService = candidateService;
        _clusterLlmClient = clusterLlmClient;
        _logger = loggerFactory.CreateLogger("EventMatcher");
    }

    public async Task<EventMatchRunResult> MatchRunAsync(string runId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var items = await _repository.LoadUnmappedRunContentItemsAsync(runId, cancellationToken);
        await _repository.MarkStaleEventsAsync(now, _config.Analysis.Event.StaleHours, cancellationToken);
        var counters = new MatchRunCounters();
        var precomputedMatches = await PrecomputeMatchesAsync(runId, items, now, counters, cancellationToken);
        var created = 0;
        var merged = 0;
        var reactivated = 0;
        var mapped = 0;
        var skipped = 0;
        var hasCommittedEventChange = false;

        foreach (var precomputedMatch in precomputedMatches)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var commitMatch = hasCommittedEventChange && ShouldRevalidateBeforeCommit(precomputedMatch.Candidates, precomputedMatch.Match, precomputedMatch.UseLlm)
                    ? await RevalidateBeforeCommitAsync(runId, precomputedMatch, now, counters, cancellationToken)
                : precomputedMatch;
            var targetEvent = await ResolveTargetEventAsync(
                commitMatch.Item,
                commitMatch.Candidates,
                commitMatch.Match,
                commitMatch.UseLlm,
                now,
                cancellationToken);
            hasCommittedEventChange = true;
            if (targetEvent.CreatedNew)
            {
                created++;
                counters.IncrementCreated();
            }
            else
            {
                merged++;
                counters.IncrementMerged();
                if (targetEvent.Reactivated)
                {
                    reactivated++;
                    counters.IncrementReactivated();
                }
            }

            var mappedNow = await _repository.MapEventItemIfMissingAsync(new EventItem
            {
                Id = BuildEventItemId(targetEvent.Event.Id, commitMatch.Item.Id),
                EventId = targetEvent.Event.Id,
                ContentItemId = commitMatch.Item.Id,
                Confidence = targetEvent.Confidence,
                MatchedAt = now,
                MatchReason = targetEvent.Reason
            }, cancellationToken);

            if (mappedNow)
            {
                mapped++;
                counters.IncrementMapped();
            }
            else
            {
                skipped++;
                counters.IncrementSkipped();
            }

            LogMatchDecisionDebug(commitMatch, targetEvent, mappedNow);
        }

        var snapshot = counters.Snapshot();
        _logger.LogInformation(
            "事件匹配运行完成。RunId={RunId}，条目数={ItemCount}，无候选={NoCandidateCount}，规则跳过={RuleSkipCount}，Cluster调用={ClusterCalledCount}，Cluster未配置={ClusterUnconfiguredCount}，重校验={RevalidatedCount}，重校验复用={RevalidationReusedCount}，新建={CreatedCount}，合并={MergedCount}，重新激活={ReactivatedCount}，映射={MappedCount}，跳过={SkippedCount}。",
            runId,
            items.Count,
            snapshot.NoCandidateCount,
            snapshot.RuleSkipCount,
            snapshot.ClusterCalledCount,
            snapshot.ClusterUnconfiguredCount,
            snapshot.RevalidatedCount,
            snapshot.RevalidationReusedCount,
            snapshot.CreatedCount,
            snapshot.MergedCount,
            snapshot.ReactivatedCount,
            snapshot.MappedCount,
            snapshot.SkippedCount);

        return new EventMatchRunResult(items.Count, created, merged, reactivated, mapped, skipped);
    }

    private async Task<IReadOnlyList<PrecomputedEventMatch>> PrecomputeMatchesAsync(
        string runId,
        IReadOnlyList<ContentItem> items,
        DateTimeOffset now,
        MatchRunCounters counters,
        CancellationToken cancellationToken)
    {
        var maxParallel = Math.Max(1, _config.Llm.Cluster.MaxParallel);
        _logger.LogInformation(
            "需要计算{Total}次聚类分析，并发数={Parallel}",
            items.Count, maxParallel
        );
        using var semaphore = new SemaphoreSlim(maxParallel);
        var progress = 0;
        var tasks = items.Select(async item =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var match = await RecallAndMatchAsync(runId, item, now, counters, cancellationToken);
                var p = Interlocked.Increment(ref progress);
                if (p % 10 == 0)
                {
                    _logger.LogInformation("已处理 {ProcessedCount} 次聚类分析", p);
                }
                return match;
            }
            finally
            {
                semaphore.Release();
            }
        });

        var result =  await Task.WhenAll(tasks);
        _logger.LogInformation("聚类分析预计算已完成");
        return result;
    }

    private async Task<PrecomputedEventMatch> RecallAndMatchAsync(
        string runId,
        ContentItem item,
        DateTimeOffset now,
        MatchRunCounters counters,
        CancellationToken cancellationToken)
    {
        var candidates = await _candidateService.RecallAsync(item, now, cancellationToken);
        var candidateFingerprint = BuildCandidateFingerprint(candidates);
        var match = await MatchCandidatesAsync(runId, item, candidates, counters, cancellationToken);
        return new PrecomputedEventMatch(item, candidates, candidateFingerprint, match.Result, match.UseLlm);
    }

    private async Task<PrecomputedEventMatch> RevalidateBeforeCommitAsync(
        string runId,
        PrecomputedEventMatch precomputedMatch,
        DateTimeOffset now,
        MatchRunCounters counters,
        CancellationToken cancellationToken)
    {
        counters.IncrementRevalidated();
        var candidates = await _candidateService.RecallAsync(precomputedMatch.Item, now, cancellationToken);
        var candidateFingerprint = BuildCandidateFingerprint(candidates);
        if (string.Equals(candidateFingerprint, precomputedMatch.CandidateFingerprint, StringComparison.Ordinal))
        {
            counters.IncrementRevalidationReused();
            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug(
                    "复用预计算聚类结果，内容条目编号={ContentItemId}，候选指纹={CandidateFingerprint}，候选数={CandidateCount}，决策={Decision}，目标事件={EventId}。",
                    precomputedMatch.Item.Id,
                    candidateFingerprint,
                    candidates.Count,
                    precomputedMatch.Match.Decision,
                    precomputedMatch.Match.EventId);
            }

            return precomputedMatch with { Candidates = candidates };
        }

        var match = await MatchCandidatesAsync(runId, precomputedMatch.Item, candidates, counters, cancellationToken);
        return new PrecomputedEventMatch(precomputedMatch.Item, candidates, candidateFingerprint, match.Result, match.UseLlm);
    }

    private async Task<CandidateMatchResult> MatchCandidatesAsync(
        string runId,
        ContentItem item,
        IReadOnlyList<EventCandidate> candidates,
        MatchRunCounters counters,
        CancellationToken cancellationToken)
    {
        if (candidates.Count == 0)
        {
            counters.IncrementNoCandidate();
        }

        // 规则召回高置信度时跳过 LLM，直接使用 top candidate
        var topCandidate = candidates.FirstOrDefault();
        if (topCandidate is not null &&
            topCandidate.Score >= _config.Analysis.Event.RuleMergeThreshold &&
            topCandidate.MatchedFeatures.Contains("token_overlap") &&
            topCandidate.MatchedFeatures.Contains("char_ngram_jaccard"))
        {
            counters.IncrementRuleSkip();
            var ruleMatch = new ClusterMatchResult(
                ClusterDecisions.SameEvent,
                topCandidate.Event.Id,
                topCandidate.Event.CanonicalTitle,
                topCandidate.Event.Summary,
                topCandidate.Score,
                "规则召回高置信度匹配（跳过 LLM）");
            _logger.LogInformation(
                "跳过聚类 LLM（规则召回匹配），内容条目编号={ContentItemId}，目标事件={EventTitle}，规则得分={Score}",
                item.Id,
                topCandidate.Event.CanonicalTitle,
                topCandidate.Score);
            return new CandidateMatchResult(ruleMatch, false);
        }

        var useLlm = candidates.Count > 0 && _clusterLlmClient.IsConfigured;
        if (useLlm)
        {
            counters.IncrementClusterCalled();
        }
        else if (candidates.Count > 0)
        {
            counters.IncrementClusterUnconfigured();
        }

        var match = useLlm
            ? await _clusterLlmClient.MatchAsync(new ClusterMatchRequest(runId, item, candidates), cancellationToken)
            : ClusterMatchResult.CreateNew(candidates.Count == 0 ? "没有召回的候选事件" : "聚类 LLM 未配置");

        return new CandidateMatchResult(match, useLlm);
    }

    private bool ShouldRevalidateBeforeCommit(
        IReadOnlyList<EventCandidate> candidates,
        ClusterMatchResult match,
        bool useLlm)
        => !CanUseExistingTarget(candidates, match, useLlm);

    private bool CanUseExistingTarget(
        IReadOnlyList<EventCandidate> candidates,
        ClusterMatchResult match,
        bool useLlm)
    {
        var matchedCandidate = FindMatchedCandidate(candidates, match);
        return CanMergeSameEvent(match, matchedCandidate, useLlm) || CanMergeFollowUp(match, matchedCandidate, useLlm);
    }

    private bool CanMergeSameEvent(ClusterMatchResult match, EventCandidate? matchedCandidate, bool useLlm)
        => match.Decision == ClusterDecisions.SameEvent &&
            match.Confidence >= (useLlm ? _config.Analysis.Event.MergeThreshold : _config.Analysis.Event.RuleMergeThreshold) &&
            matchedCandidate is not null;

    private bool CanMergeFollowUp(ClusterMatchResult match, EventCandidate? matchedCandidate, bool useLlm)
        => match.Decision == ClusterDecisions.FollowUp &&
            match.Confidence >= _config.Analysis.Event.StaleMergeThreshold &&
            matchedCandidate is not null;

    private async Task<EventMatchOutcome> ResolveTargetEventAsync(
        ContentItem item,
        IReadOnlyList<EventCandidate> candidates,
        ClusterMatchResult match,
        bool useLlm,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var matchedCandidate = FindMatchedCandidate(candidates, match);
        var canMergeSameEvent = CanMergeSameEvent(match, matchedCandidate, useLlm);
        var canMergeFollowUp = CanMergeFollowUp(match, matchedCandidate, useLlm);

        if (!canMergeSameEvent && !canMergeFollowUp)
        {
            var newEvent = CreateEvent(item, match, now);
            await _repository.UpsertEventAsync(newEvent, cancellationToken);
            return new EventMatchOutcome(newEvent, true, false, 1, match.Reason ?? "创建新事件");
        }

        var eventAggregate = await _repository.GetEventAsync(matchedCandidate!.Event.Id, cancellationToken) ?? matchedCandidate.Event;
        var reactivated = eventAggregate.LastSeenAt < now.AddHours(-Math.Max(1, _config.Analysis.Event.StaleHours));
        UpdateExistingEvent(eventAggregate, item, match, now, reactivated);
        await _repository.UpsertEventAsync(eventAggregate, cancellationToken);
        return new EventMatchOutcome(eventAggregate, false, reactivated, match.Confidence, match.Reason ?? match.Decision);
    }

    private static EventCandidate? FindMatchedCandidate(IReadOnlyList<EventCandidate> candidates, ClusterMatchResult match)
        => string.IsNullOrWhiteSpace(match.EventId)
            ? null
            : candidates.FirstOrDefault(candidate => candidate.Event.Id == match.EventId);

    private void LogMatchDecisionDebug(PrecomputedEventMatch commitMatch, EventMatchOutcome targetEvent, bool mappedNow)
    {
        if (!_logger.IsEnabled(LogLevel.Debug))
        {
            return;
        }

        _logger.LogDebug(
            "事件匹配决策。内容条目编号={ContentItemId}，候选数={CandidateCount}，候选事件={CandidateEventIds}，候选指纹={CandidateFingerprint}，使用LLM={UseLlm}，决策={Decision}，决策事件={DecisionEventId}，置信度={Confidence:F4}，结果事件={TargetEventId}，新建={CreatedNew}，重新激活={Reactivated}，已映射={MappedNow}，原因={Reason}。",
            commitMatch.Item.Id,
            commitMatch.Candidates.Count,
            string.Join(',', commitMatch.Candidates.Select(candidate => candidate.Event.Id)),
            commitMatch.CandidateFingerprint,
            commitMatch.UseLlm,
            commitMatch.Match.Decision,
            commitMatch.Match.EventId,
            commitMatch.Match.Confidence,
            targetEvent.Event.Id,
            targetEvent.CreatedNew,
            targetEvent.Reactivated,
            mappedNow,
            targetEvent.Reason);
    }

    private static EventAggregate CreateEvent(ContentItem item, ClusterMatchResult match, DateTimeOffset now)
    {
        var title = FirstNonEmpty(match.CanonicalTitle, item.Title, item.Summary) ?? "未命名事件";
        var summary = FirstNonEmpty(match.Summary, item.Summary, item.Title) ?? title;
        var entities = ExtractStableAnchors(title, summary, item.Title, item.Summary);
        var aliases = ExtractStableAnchors(item.Title, match.CanonicalTitle, title);
        return new EventAggregate
        {
            Id = BuildEventId(item),
            Type = EventType.NewsEvent,
            CanonicalTitle = title,
            Summary = summary,
            Entities = entities,
            Aliases = aliases,
            KeyTerms = ExtractKeyTerms(title, summary),
            RepresentativeTitles = string.IsNullOrWhiteSpace(item.Title) ? [] : [item.Title.Trim()],
            Status = EventStatus.Active,
            FirstSeenAt = now,
            LastSeenAt = now,
            LastActivatedAt = now,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static void UpdateExistingEvent(
        EventAggregate eventAggregate,
        ContentItem item,
        ClusterMatchResult match,
        DateTimeOffset now,
        bool reactivated)
    {
        if (!string.IsNullOrWhiteSpace(match.CanonicalTitle))
        {
            eventAggregate.CanonicalTitle = match.CanonicalTitle.Trim();
        }

        if (!string.IsNullOrWhiteSpace(match.Summary))
        {
            eventAggregate.Summary = match.Summary.Trim();
        }

        eventAggregate.LastSeenAt = now;
        eventAggregate.UpdatedAt = now;
        if (reactivated)
        {
            eventAggregate.Status = EventStatus.Active;
            eventAggregate.LastActivatedAt = now;
        }

        AddUnique(eventAggregate.RepresentativeTitles, item.Title, RepresentativeTitleLimit);
        var terms = ExtractKeyTerms(item.Title, item.Summary);
        foreach (var term in terms)
        {
            AddUnique(eventAggregate.KeyTerms, term, KeyTermLimit);
        }

        foreach (var entity in ExtractStableAnchors(item.Title, item.Summary))
        {
            AddUnique(eventAggregate.Entities, entity, EntityLimit);
        }

        foreach (var alias in ExtractStableAnchors(item.Title, match.CanonicalTitle, match.Summary, eventAggregate.CanonicalTitle))
        {
            AddUnique(eventAggregate.Aliases, alias, AliasLimit);
        }
    }

    private static void AddUnique(List<string> values, string? value, int limit)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var normalized = value.Trim();
        if (values.Any(existing => string.Equals(existing, normalized, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        values.Insert(0, normalized);
        if (values.Count > limit)
        {
            values.RemoveRange(limit, values.Count - limit);
        }
    }

    private static List<string> ExtractKeyTerms(params string?[] values)
        => EventCandidateService.Tokenize(string.Join(' ', values.Where(value => !string.IsNullOrWhiteSpace(value))))
            .OrderByDescending(token => token.Length)
            .ThenBy(token => token, StringComparer.OrdinalIgnoreCase)
            .Take(KeyTermLimit)
            .ToList();

    private static List<string> ExtractStableAnchors(params string?[] values)
    {
        var anchors = new List<string>();

        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            foreach (var anchor in ExtractStableAnchorsFromText(value))
            {
                AddUnique(anchors, anchor, StableAnchorLimit);
            }
        }

        return anchors;
    }

    private static IEnumerable<string> ExtractStableAnchorsFromText(string text)
    {
        var tokens = StableAnchorTokenRegex.Matches(text)
            .Select(match => match.Value.Trim())
            .Where(token => token.Length > 0)
            .ToList();

        var run = new List<string>();
        foreach (var token in tokens)
        {
            if (IsStableAnchorToken(token))
            {
                run.Add(token);
                yield return token;
                continue;
            }

            foreach (var phrase in BuildStableAnchorPhrases(run))
            {
                yield return phrase;
            }

            run.Clear();
        }

        foreach (var phrase in BuildStableAnchorPhrases(run))
        {
            yield return phrase;
        }
    }

    private static IEnumerable<string> BuildStableAnchorPhrases(IReadOnlyList<string> run)
    {
        if (run.Count < 2)
        {
            yield break;
        }

        yield return string.Join(' ', run);
    }

    private static bool IsStableAnchorToken(string token)
    {
        if (token.Length < 2)
        {
            return false;
        }

        var lower = token.ToLowerInvariant();
        if (StableAnchorStopWords.Contains(lower))
        {
            return false;
        }

        if (ContainsCjkCharacter(token))
        {
            var cjkCount = CountCjkCharacters(token);
            if (cjkCount >= MinCjkAnchorLength)
            {
                return true;
            }

            return cjkCount == 1 && token.Length <= MaxMixedCjkAnchorLength && ContainsAsciiLetterOrDigit(token);
        }

        if (token.Any(char.IsDigit))
        {
            return true;
        }

        var upperCount = token.Count(char.IsUpper);
        if (upperCount >= 2)
        {
            return true;
        }

        var hasUpper = token.Any(char.IsUpper);
        var hasLower = token.Any(char.IsLower);
        if (hasUpper && hasLower)
        {
            return true;
        }

        return token.Length >= 3 && char.IsUpper(token[0]) && token.Skip(1).All(character => char.IsLower(character));
    }

    private static bool ContainsCjkCharacter(string token)
        => token.Any(IsCjkCharacter);

    private static int CountCjkCharacters(string token)
        => token.Count(IsCjkCharacter);

    private static bool ContainsAsciiLetterOrDigit(string token)
        => token.Any(character =>
            character is >= '0' and <= '9' ||
            character is >= 'A' and <= 'Z' ||
            character is >= 'a' and <= 'z');

    private static bool IsCjkCharacter(char character)
        => (character >= '\u4E00' && character <= '\u9FFF') ||
            (character >= '\u3400' && character <= '\u4DBF') ||
            (character >= '\u3040' && character <= '\u30FF') ||
            (character >= '\uAC00' && character <= '\uD7AF');

    private static readonly HashSet<string> StableAnchorStopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "with", "from", "this", "that", "into", "after", "about", "news", "update", "over", "under", "into", "between"
    };

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static string BuildEventId(ContentItem item)
        => $"evt:{ShortHash($"{item.Source}|{item.SourceItemId}|{item.Title}")}";

    private static string BuildEventItemId(string eventId, string contentItemId)
        => $"ei:{ShortHash($"{eventId}|{contentItemId}")}";

    private static string BuildCandidateFingerprint(IReadOnlyList<EventCandidate> candidates)
    {
        if (candidates.Count == 0)
        {
            return ShortHash("empty");
        }

        var builder = new StringBuilder();
        foreach (var candidate in candidates)
        {
            builder.Append(candidate.Event.Id)
                .Append('|')
                .Append(candidate.Score.ToString("R", System.Globalization.CultureInfo.InvariantCulture))
                .Append('|')
                .AppendJoin(',', candidate.MatchedFeatures.Order(StringComparer.OrdinalIgnoreCase))
                .Append(';');
        }

        return ShortHash(builder.ToString());
    }

    private static string ShortHash(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value.Trim()));
        return Convert.ToHexString(hash)[..20].ToLowerInvariant();
    }

    private sealed record PrecomputedEventMatch(
        ContentItem Item,
        IReadOnlyList<EventCandidate> Candidates,
        string CandidateFingerprint,
        ClusterMatchResult Match,
        bool UseLlm);

    private sealed record CandidateMatchResult(ClusterMatchResult Result, bool UseLlm);

    private sealed record EventMatchOutcome(
        EventAggregate Event,
        bool CreatedNew,
        bool Reactivated,
        double Confidence,
        string Reason);

    private sealed class MatchRunCounters
    {
        private int _noCandidateCount;
        private int _ruleSkipCount;
        private int _clusterCalledCount;
        private int _clusterUnconfiguredCount;
        private int _revalidatedCount;
        private int _revalidationReusedCount;
        private int _createdCount;
        private int _mergedCount;
        private int _reactivatedCount;
        private int _mappedCount;
        private int _skippedCount;

        public void IncrementNoCandidate() => Interlocked.Increment(ref _noCandidateCount);
        public void IncrementRuleSkip() => Interlocked.Increment(ref _ruleSkipCount);
        public void IncrementClusterCalled() => Interlocked.Increment(ref _clusterCalledCount);
        public void IncrementClusterUnconfigured() => Interlocked.Increment(ref _clusterUnconfiguredCount);
        public void IncrementRevalidated() => Interlocked.Increment(ref _revalidatedCount);
        public void IncrementRevalidationReused() => Interlocked.Increment(ref _revalidationReusedCount);
        public void IncrementCreated() => Interlocked.Increment(ref _createdCount);
        public void IncrementMerged() => Interlocked.Increment(ref _mergedCount);
        public void IncrementReactivated() => Interlocked.Increment(ref _reactivatedCount);
        public void IncrementMapped() => Interlocked.Increment(ref _mappedCount);
        public void IncrementSkipped() => Interlocked.Increment(ref _skippedCount);

        public MatchRunCounterSnapshot Snapshot()
            => new(
                Volatile.Read(ref _noCandidateCount),
                Volatile.Read(ref _ruleSkipCount),
                Volatile.Read(ref _clusterCalledCount),
                Volatile.Read(ref _clusterUnconfiguredCount),
                Volatile.Read(ref _revalidatedCount),
                Volatile.Read(ref _revalidationReusedCount),
                Volatile.Read(ref _createdCount),
                Volatile.Read(ref _mergedCount),
                Volatile.Read(ref _reactivatedCount),
                Volatile.Read(ref _mappedCount),
                Volatile.Read(ref _skippedCount));
    }

    private sealed record MatchRunCounterSnapshot(
        int NoCandidateCount,
        int RuleSkipCount,
        int ClusterCalledCount,
        int ClusterUnconfiguredCount,
        int RevalidatedCount,
        int RevalidationReusedCount,
        int CreatedCount,
        int MergedCount,
        int ReactivatedCount,
        int MappedCount,
        int SkippedCount);
}
