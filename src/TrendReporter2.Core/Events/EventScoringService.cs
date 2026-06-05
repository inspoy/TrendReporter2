using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using TrendReporter2.Core.Configuration;
using TrendReporter2.Core.Content;
using TrendReporter2.Core.Sources;

namespace TrendReporter2.Core.Events;

public sealed class EventScoringService : IEventScoringService
{
    private const double ReactivationBonusValue = 10;
    private static readonly EventId PushSkippedEventId = new(2001, "PushSkipped");
    private static readonly EventId PushAttemptEventId = new(2002, "PushAttempt");
    private static readonly EventId PushSucceededEventId = new(2003, "PushSucceeded");
    private static readonly EventId PushFailedEventId = new(2004, "PushFailed");
    private readonly AppConfig _config;
    private readonly IEventRepository _repository;
    private readonly IJudgeLlmClient _judgeLlmClient;
    private readonly IEnumerable<IPusher> _pushers;
    private readonly ILogger _logger;
    private readonly IReadOnlyDictionary<string, double> _sourceWeights;

    public EventScoringService(
        AppConfig config,
        IEventRepository repository,
        IJudgeLlmClient judgeLlmClient,
        IEnumerable<IPusher> pushers,
        ISourceRegistry sourceRegistry,
        ILoggerFactory loggerFactory)
    {
        _config = config;
        _repository = repository;
        _judgeLlmClient = judgeLlmClient;
        _pushers = pushers;
        _sourceWeights = sourceRegistry.GetSources()
            .ToDictionary(source => source.Id, source => source.Weight, StringComparer.OrdinalIgnoreCase);
        _logger = loggerFactory.CreateLogger("EventScoring");
    }

    public async Task<EventScoringRunResult> ScoreAndPushRunAsync(
        string runId,
        DateTimeOffset runStartedAt,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var inputs = await _repository.LoadRunEventScoringInputsAsync(runId, cancellationToken);
        if (inputs.Count == 0)
        {
            return new EventScoringRunResult(0, 0, 0);
        }

        _logger.LogInformation("开始执行事件评分，输入事件数={InputCount}。", inputs.Count);
        var eventIds = inputs.Select(input => input.Event.Id).Distinct(StringComparer.Ordinal).ToList();
        var trendSince = now.AddHours(-Math.Max(1, _config.Analysis.Event.TrendWindowHours));
        var recentSnapshots = await _repository.LoadRecentScoreSnapshotsAsync(eventIds, trendSince, cancellationToken);
        var recentByEvent = recentSnapshots
            .GroupBy(snapshot => snapshot.EventId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderBy(snapshot => snapshot.CalculatedAt).ToList(), StringComparer.Ordinal);

        var counters = new ScoringRunCounters();
        var maxParallelLlm = Math.Max(1, _config.System.MaxParallelLlm);
        using var semaphore = new SemaphoreSlim(maxParallelLlm);
        var tasks = inputs.Select(async input =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var priorSnapshots = recentByEvent.GetValueOrDefault(input.Event.Id) ?? [];
                EventBlacklistPolicy.Apply(input.Event, _config.Filters);
                if (input.Event.IsBlacklisted)
                {
                    counters.IncrementBlacklisted();
                }

                var score = BuildBaseScore(runId, input, priorSnapshots, runStartedAt, now);
                var eligibleBeforeJudge = IsEligible(score, input.Event, runStartedAt, now);
                var reactivated = IsReactivated(input.Event, runStartedAt, now);
                var hasEnoughSources = score.UniqueSourceCount >= _config.Analysis.Event.SourceCount;
                var shouldCallJudge = reactivated || (hasEnoughSources && (eligibleBeforeJudge || IsNearEligibility(score)));
                var judgeGateReason = shouldCallJudge
                    ? reactivated ? "reactivated" : eligibleBeforeJudge ? "eligible_before_judge" : "near_eligibility"
                    : hasEnoughSources ? "not_near_eligibility" : "source_count_below_threshold";

                if (shouldCallJudge)
                {
                    counters.IncrementJudgeCalled();
                }

                var judge = shouldCallJudge
                    ? await _judgeLlmClient.JudgeAsync(new JudgeRequest(runId, input.Event, score, input.Evidence, score.TriggerReasons), cancellationToken)
                    : JudgeResult.Neutral("事件未达到评判阈值");
                if (IsJudgePromoted(judge))
                {
                    counters.IncrementJudgePromoted();
                }

                ApplyJudge(score, judge);
                var eligible = IsEligible(score, input.Event, runStartedAt, now);
                if (eligible)
                {
                    counters.IncrementEligible();
                }
                else
                {
                    counters.IncrementRejection(BuildEligibilityRejectReason(input.Event));
                }

                var progress = BuildProgress(input, score, priorSnapshots, judge, runStartedAt, now);
                ApplyProgress(input.Event, progress, judge, now);
                score.CurrentStage = input.Event.CurrentStage;
                string? pushRejectReason = null;

                if (eligible)
                {
                    var shouldPush = ShouldPush(input.Event, score, out var dontPushReason);
                    if (shouldPush)
                    {
                        counters.IncrementPushAttempt();
                        var message = BuildPushMessage(runId, input, score);
                        var pushAttempt = await PushAndLogAsync(message, now, cancellationToken);
                        if (pushAttempt.Recorded)
                        {
                            input.Event.LastPushedAt = now;
                            input.Event.PushCount++;
                            input.Event.LastPushScore = score.TotalScore;
                            input.Event.LastPushRankScore = score.RankScore;
                            input.Event.LastPushSourceCount = score.UniqueSourceCount;
                            if (pushAttempt.Success)
                            {
                                counters.IncrementPushSuccess();
                            }
                            else
                            {
                                pushRejectReason = pushAttempt.Error ?? "push_failed_or_skipped";
                                counters.IncrementRejection(pushAttempt.Error ?? "push_failed_or_skipped");
                            }
                        }
                        else
                        {
                            pushRejectReason = pushAttempt.Error ?? "push_log_exists";
                            counters.IncrementRejection(pushAttempt.Error ?? "push_log_exists");
                        }
                    }
                    else
                    {
                        pushRejectReason = dontPushReason;
                        counters.IncrementRejection(dontPushReason);
                        _logger.LogInformation(
                            PushSkippedEventId,
                            "合格事件未推送，事件编号={EventId}，原因={Reason}。",
                            input.Event.Id,
                            dontPushReason);
                    }
                }

                var snapshot = ToSnapshot(score);
                await _repository.InsertEventScoreSnapshotAsync(snapshot, cancellationToken);

                input.Event.UpdatedAt = now;
                await _repository.UpdateEventsAsync([input.Event], cancellationToken);
                LogScoringDecisionDebug(input.Event, score, judge, judgeGateReason, eligible, eligible ? pushRejectReason : BuildEligibilityRejectReason(input.Event));
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);

        var snapshot = counters.Snapshot();
        _logger.LogInformation(
            "事件评分推送运行完成。RunId={RunId}，评分事件数={ScoredCount}，黑名单事件数={BlacklistedCount}，合格事件数={EligibleCount}，Judge调用数={JudgeCalledCount}，Judge提升数={JudgePromotedCount}，推送尝试数={PushAttemptCount}，推送成功数={PushSuccessCount}，拒绝分桶={RejectionBuckets}。",
            runId,
            inputs.Count,
            snapshot.BlacklistedCount,
            snapshot.EligibleCount,
            snapshot.JudgeCalledCount,
            snapshot.JudgePromotedCount,
            snapshot.PushAttemptCount,
            snapshot.PushSuccessCount,
            FormatBuckets(snapshot.RejectionBuckets));

        return new EventScoringRunResult(inputs.Count, snapshot.EligibleCount, snapshot.PushSuccessCount);
    }

    private void LogScoringDecisionDebug(EventAggregate eventAggregate, EventScore score, JudgeResult judge, string judgeGateReason, bool eligible, string? rejectReason)
    {
        if (!_logger.IsEnabled(LogLevel.Debug))
        {
            return;
        }

        _logger.LogDebug(
            "事件评分决策。事件编号={EventId}，总分={TotalScore:F2}，覆盖={CoverageScore:F4}，排名={RankScore:F4}，快讯={FlashScore:F4}，新鲜度={FreshnessScore:F4}，趋势={TrendScore:F4}，持续={PersistenceScore:F4}，LLM加成={LlmBoostScore:F4}，重新激活加成={ReactivationBonus:F2}，Heat={HeatValue:F4}，来源数={UniqueSourceCount}，排名来源数={RankedSourceCount}，快讯来源数={FlashSourceCount}，触发原因={TriggerReasons}，JudgeGate={JudgeGateReason}，JudgeImportance={JudgeImportance}，JudgeBoost={JudgeBoost:F4}，合格={Eligible}，拒绝原因={RejectReason}，阶段={CurrentStage}。",
            eventAggregate.Id,
            score.TotalScore,
            score.CoverageScore,
            score.RankScore,
            score.FlashScore,
            score.FreshnessScore,
            score.TrendScore,
            score.PersistenceScore,
            score.LlmBoostScore,
            score.ReactivationBonus,
            score.HeatValue,
            score.UniqueSourceCount,
            score.RankedSourceCount,
            score.FlashSourceCount,
            string.Join(',', score.TriggerReasons),
            judgeGateReason,
            judge.Importance,
            judge.BoostScore,
            eligible,
            rejectReason,
            score.CurrentStage);
    }

    private static string BuildEligibilityRejectReason(EventAggregate eventAggregate)
        => eventAggregate.IsBlacklisted ? "blacklisted" : "not_eligible";

    private EventScore BuildBaseScore(
        string runId,
        RunEventScoringInput input,
        IReadOnlyList<EventScoreSnapshot> priorSnapshots,
        DateTimeOffset runStartedAt,
        DateTimeOffset now)
    {
        var snapshots = input.Evidence.Select(evidence => evidence.Snapshot).ToList();
        var pushableSnapshots = snapshots.Where(snapshot => !IsTopicSnapshot(snapshot)).ToList();
        var uniqueSourceCount = pushableSnapshots.Select(snapshot => snapshot.Source).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var rankedSnapshots = pushableSnapshots.Where(IsRankedSnapshot).ToList();
        var flashSnapshots = pushableSnapshots.Where(IsFlashSnapshot).ToList();
        var rankedSourceCount = rankedSnapshots.Select(snapshot => snapshot.Source).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var flashSourceCount = flashSnapshots.Select(snapshot => snapshot.Source).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var avgRank = rankedSnapshots.Count == 0 ? 0 : rankedSnapshots.Average(snapshot => snapshot.Rank!.Value);
        var avgNormalizedRank = rankedSnapshots.Count == 0 ? 0 : rankedSnapshots.Average(snapshot => snapshot.NormalizedRankScore!.Value);
        var freshnessScore = flashSnapshots.Count == 0 ? 0 : flashSnapshots.Average(snapshot => Clamp01(snapshot.FreshnessScore));
        var flashRepeatScore = flashSnapshots.Count <= 1 ? 0 : Clamp01((double)(flashSnapshots.Count - 1) / Math.Max(1, _config.Analysis.Event.SourceCount - 1));
        var flashCoverageScore = Clamp01((double)flashSourceCount / Math.Max(1, _config.Analysis.Event.SourceCount));
        var flashScore = flashSnapshots.Count == 0 ? 0 : Clamp01(0.55 * freshnessScore + 0.30 * flashCoverageScore + 0.15 * flashRepeatScore);
        var rankedHeat = rankedSnapshots.Sum(snapshot => snapshot.NormalizedRankScore!.Value);
        var flashHeat = flashSnapshots.Sum(snapshot => Clamp01(snapshot.FreshnessScore));
        var heatValue = rankedHeat + flashHeat;
        var heatSeries = priorSnapshots.Select(snapshot => snapshot.HeatValue).Append(heatValue).ToList();
        var smoothedHeat = CalculateEwma(heatSeries);
        var trendScore = CalculateTrendScore(heatSeries);
        var reactivated = IsReactivated(input.Event, runStartedAt, now);
        var score = new EventScore
        {
            EventId = input.Event.Id,
            RunId = runId,
            CalculatedAt = now,
            CoverageScore = Clamp01((double)uniqueSourceCount / Math.Max(1, _config.Analysis.Event.SourceCount)),
            RankScore = Clamp01(avgNormalizedRank),
            FlashScore = flashScore,
            FreshnessScore = freshnessScore,
            TrendScore = trendScore,
            PersistenceScore = Clamp01((now - input.Event.FirstSeenAt).TotalHours / Math.Max(1, _config.Analysis.HistoryHours)),
            ReactivationBonus = reactivated ? ReactivationBonusValue : 0,
            UniqueSourceCount = uniqueSourceCount,
            RankedSourceCount = rankedSourceCount,
            FlashSourceCount = flashSourceCount,
            AvgRank = avgRank,
            AvgNormalizedRank = avgNormalizedRank,
            HeatValue = heatValue,
            SmoothedHeatValue = smoothedHeat,
            TrendEvidenceCount = heatSeries.Count
        };

        if (rankedSourceCount >= _config.Analysis.Event.SourceCount && avgNormalizedRank >= _config.Analysis.Event.NormalizedRankThreshold)
        {
            score.TriggerReasons.Add(TriggerReasons.CoverageRank);
        }

        AddFlashTriggerReasons(score, flashSnapshots, reactivated);

        if (HasRisingTrend(score, heatSeries.Sum()))
        {
            score.TriggerReasons.Add(TriggerReasons.RisingTrend);
        }

        if (reactivated)
        {
            score.TriggerReasons.Add(TriggerReasons.Reactivation);
        }

        score.TotalScore = CalculateTotalScore(score);
        return score;
    }

    private bool IsEligible(EventScore score, EventAggregate eventAggregate, DateTimeOffset runStartedAt, DateTimeOffset now)
        => !eventAggregate.IsBlacklisted &&
            (score.TriggerReasons.Contains(TriggerReasons.CoverageRank) ||
             score.TriggerReasons.Contains(TriggerReasons.RisingTrend) ||
             HasFlashTriggerReason(score) ||
             score.TriggerReasons.Contains(TriggerReasons.JudgeHighImportance) ||
             IsReactivated(eventAggregate, runStartedAt, now));

    private bool IsNearEligibility(EventScore score)
        => score.UniqueSourceCount >= Math.Max(1, _config.Analysis.Event.SourceCount - 1) ||
            score.HeatValue >= _config.Analysis.Event.MinTrendHeat ||
            score.FlashScore >= _config.Analysis.Event.NormalizedRankThreshold;

    private void AddFlashTriggerReasons(EventScore score, IReadOnlyList<ContentSnapshot> flashSnapshots, bool reactivated)
    {
        if (score.FlashScore < _config.Analysis.Event.NormalizedRankThreshold)
        {
            return;
        }

        if (score.FlashSourceCount >= _config.Analysis.Event.SourceCount)
        {
            score.TriggerReasons.Add(TriggerReasons.FlashMultiSource);
        }

        if (flashSnapshots.Count >= _config.Analysis.Event.SourceCount)
        {
            score.TriggerReasons.Add(TriggerReasons.FlashRepeated);
        }

        if (reactivated && score.FlashSourceCount > 0)
        {
            score.TriggerReasons.Add(TriggerReasons.FlashFollowUp);
        }

        if (flashSnapshots.Any(snapshot => HasTrustedWeight(snapshot)))
        {
            score.TriggerReasons.Add(TriggerReasons.FlashTrustedSource);
        }
    }

    private bool HasTrustedWeight(ContentSnapshot snapshot)
    {
        var sourceId = snapshot.SourceId ?? snapshot.Source;
        return !string.IsNullOrWhiteSpace(sourceId) &&
            _sourceWeights.TryGetValue(sourceId, out var weight) &&
            weight > 1.0;
    }

    private static bool HasFlashTriggerReason(EventScore score)
        => score.TriggerReasons.Contains(TriggerReasons.FlashMultiSource) ||
            score.TriggerReasons.Contains(TriggerReasons.FlashRepeated) ||
            score.TriggerReasons.Contains(TriggerReasons.FlashFollowUp) ||
            score.TriggerReasons.Contains(TriggerReasons.FlashTrustedSource);

    private void ApplyJudge(EventScore score, JudgeResult judge)
    {
        score.LlmBoostScore = Clamp01(judge.BoostScore);
        score.TotalScore = CalculateTotalScore(score);
        if (IsJudgePromoted(judge))
        {
            score.TriggerReasons.Add(TriggerReasons.JudgeHighImportance);
        }
    }

    private static bool IsJudgePromoted(JudgeResult judge)
        => judge.BoostScore >= 0.5 ||
            string.Equals(judge.Importance, "high", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(judge.Importance, "critical", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(judge.Importance, "breaking", StringComparison.OrdinalIgnoreCase);

    private ProgressFallback BuildProgress(
        RunEventScoringInput input,
        EventScore score,
        IReadOnlyList<EventScoreSnapshot> priorSnapshots,
        JudgeResult judge,
        DateTimeOffset runStartedAt,
        DateTimeOffset now)
    {
        var reactivated = IsReactivated(input.Event, runStartedAt, now);
        var previousHeat = priorSnapshots.LastOrDefault()?.HeatValue ?? 0;
        var stage = judge.Stage;
        if (string.IsNullOrWhiteSpace(stage))
        {
            stage = reactivated ? EventProgressStages.FollowUp :
                score.TrendScore >= 0.35 && score.RankScore >= _config.Analysis.Event.NormalizedRankThreshold ? EventProgressStages.Escalating :
                score.UniqueSourceCount >= _config.Analysis.Event.SourceCount || score.TrendScore > 0 ? EventProgressStages.Expanding :
                previousHeat > score.HeatValue && score.TrendEvidenceCount >= _config.Analysis.Event.MinTrendSamples ? EventProgressStages.Cooling :
                EventProgressStages.Initial;
        }

        var sources = input.Evidence
            .Select(evidence => evidence.Snapshot.Source)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToList();
        var latestTitle = input.Evidence
            .OrderByDescending(evidence => evidence.MatchedAt)
            .Select(evidence => evidence.ContentItem.Title)
            .FirstOrDefault(title => !string.IsNullOrWhiteSpace(title)) ?? input.Event.CanonicalTitle;
        var summary = string.IsNullOrWhiteSpace(judge.ProgressSummary)
            ? $"首次发现于 {input.Event.FirstSeenAt:yyyy-MM-dd HH:mm}，当前由 {score.UniqueSourceCount} 个来源覆盖，包括 {string.Join(", ", sources)}；最新进展: {latestTitle}。"
            : judge.ProgressSummary.Trim();

        var milestones = new List<EventMilestone>
        {
            new()
            {
                Time = input.Event.FirstSeenAt,
                Kind = "first_seen",
                Label = "首次发现",
                Source = sources.FirstOrDefault(),
                Summary = $"事件以 {input.Event.CanonicalTitle} 进入监控。"
            }
        };

        if (score.UniqueSourceCount > 1)
        {
            milestones.Add(new EventMilestone
            {
                Time = now,
                Kind = "source_expansion",
                Label = "来源覆盖扩展",
                Source = sources.FirstOrDefault(),
                Summary = $"覆盖已扩展到 {score.UniqueSourceCount} 个来源，平均归一化排名 {score.AvgNormalizedRank:F2}。"
            });
        }

        if (reactivated)
        {
            milestones.Add(new EventMilestone
            {
                Time = input.Event.LastActivatedAt,
                Kind = "reactivation",
                Label = "发现后续进展",
                Source = sources.FirstOrDefault(),
                Summary = "一个之前已冷却的事件在本次抓取运行中重新活跃。"
            });
        }

        if (milestones.Count < 3 && score.TrendScore > 0)
        {
            milestones.Add(new EventMilestone
            {
                Time = now,
                Kind = "heat_rising",
                Label = "热度上升",
                Source = sources.FirstOrDefault(),
                Summary = $"热度达到 {score.HeatValue:F2}，趋势分数为 {score.TrendScore:F2}。"
            });
        }

        return new ProgressFallback(stage, summary, milestones.Take(3).ToList());
    }

    private static void ApplyProgress(EventAggregate eventAggregate, ProgressFallback progress, JudgeResult judge, DateTimeOffset now)
    {
        if (!string.IsNullOrWhiteSpace(judge.Summary))
        {
            eventAggregate.Summary = judge.Summary.Trim();
        }

        eventAggregate.CurrentStage = progress.Stage;
        eventAggregate.ProgressSummary = progress.Summary;
        eventAggregate.Milestones = progress.Milestones.ToList();
        eventAggregate.ProgressUpdatedAt = now;
    }

    private bool ShouldPush(EventAggregate eventAggregate, EventScore score, out string dontPushReason)
    {
        dontPushReason = string.Empty;
        if (eventAggregate.IsBlacklisted)
        {
            dontPushReason = "事件在黑名单中";
            return false;
        }

        if (eventAggregate.PushCount == 0 || eventAggregate.LastPushedAt is null)
        {
            if (score.UniqueSourceCount < _config.Analysis.Event.SourceCount && !HasFlashTriggerReason(score))
            {
                dontPushReason = $"信源数量不足({score.UniqueSourceCount})";
                return false;
            }

            score.TriggerReasons.Add(TriggerReasons.FirstPush);
            return true;
        }

        if (eventAggregate.LastPushSourceCount is null || score.UniqueSourceCount - eventAggregate.LastPushSourceCount.Value >= _config.Analysis.RepeatPush.SourceAddThreshold)
        {
            score.TriggerReasons.Add(TriggerReasons.SourceIncrease);
            return true;
        }

        if (score.RankedSourceCount > 0 && (eventAggregate.LastPushRankScore is null || score.RankScore - eventAggregate.LastPushRankScore.Value >= _config.Analysis.RepeatPush.RankScoreImproveThreshold))
        {
            score.TriggerReasons.Add(TriggerReasons.RankImprovement);
            return true;
        }

        if (eventAggregate.LastPushScore is null || score.TotalScore - eventAggregate.LastPushScore.Value >= _config.Analysis.RepeatPush.ScoreImproveThreshold)
        {
            score.TriggerReasons.Add(TriggerReasons.ScoreImprovement);
            return true;
        }

        dontPushReason = "非首次推送的事件，评分和排名增量均没有超过阈值";
        return false;
    }

    private async Task<PushAttemptResult> PushAndLogAsync(PushMessage message, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var pushLog = new PushLog
        {
            Id = BuildPushLogId(message.DedupKey),
            EventId = message.EventId,
            PushType = message.PushType,
            PushedAt = now,
            Title = message.Title,
            Reason = message.Reason,
            Content = message.Content,
            Payload = BuildUnsentPayload(message),
            DedupKey = message.DedupKey,
            Success = false,
            Error = "待处理"
        };
        var inserted = await _repository.InsertPushLogIfMissingAsync(pushLog, cancellationToken);
        if (!inserted)
        {
            _logger.LogInformation(
                PushSkippedEventId,
                "跳过推送插入，事件编号={EventId}。原因={Reason}，标题={Title}，内容={Content}",
                message.EventId,
                message.Reason,
                message.Title,
                Truncate(message.Content, 500));
            return new PushAttemptResult(false, false, "push_log_exists");
        }

        _logger.LogInformation(
            PushAttemptEventId,
            "尝试推送，事件编号={EventId}。原因={Reason}，标题={Title}，内容={Content}",
            message.EventId,
            message.Reason,
            message.Title,
            Truncate(message.Content, 500));

        var pusher = _pushers.FirstOrDefault(pusher => pusher.IsConfigured);
        var result = pusher is null
            ? PushResult.Skipped("没有已配置的推送器", pushLog.Payload)
            : await pusher.PushAsync(message, cancellationToken);
        pushLog.Payload = result.Payload;
        pushLog.Success = result.Success;
        pushLog.Error = result.Error;
        await _repository.UpdatePushLogAsync(pushLog, cancellationToken);

        if (result.Success)
        {
            _logger.LogInformation(
                PushSucceededEventId,
                "推送成功，事件编号={EventId}。原因={Reason}，标题={Title}，内容={Content}",
                message.EventId,
                message.Reason,
                message.Title,
                Truncate(message.Content, 500));
        }
        else
        {
            _logger.LogWarning(
                PushFailedEventId,
                "推送失败或跳过，事件编号={EventId}。原因={Reason}，标题={Title}，内容={Content}，错误={Error}",
                message.EventId,
                message.Reason,
                message.Title,
                Truncate(message.Content, 500),
                result.Error);
        }

        return new PushAttemptResult(result.Success, true, result.Error);
    }

    private PushMessage BuildPushMessage(string runId, RunEventScoringInput input, EventScore score)
    {
        var reason = SelectPushReason(score);
        var link = input.Evidence
            .Select(evidence => evidence.ContentItem.MobileUrl ?? evidence.ContentItem.Url)
            .FirstOrDefault(url => !string.IsNullOrWhiteSpace(url)) ?? string.Empty;
        var stage = string.IsNullOrWhiteSpace(input.Event.CurrentStage) ? EventProgressStages.Initial : input.Event.CurrentStage;
        var summary = string.IsNullOrWhiteSpace(input.Event.ProgressSummary) ? input.Event.Summary : input.Event.ProgressSummary;
        var sources = input.Evidence
            .Select(evidence => evidence.Snapshot.Source)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToList();
        var message = $"[{stage}] {summary} WhyNow={FormatReason(reason)},Source={string.Join(", ", sources)},Score={score.TotalScore:F1},Heat={score.HeatValue:F2}。";
        return new PushMessage
        {
            Title = input.Event.CanonicalTitle,
            Message = message,
            Reason = reason,
            Content = message,
            Link = link,
            EventId = input.Event.Id,
            PushType = PushTypes.Instant,
            DedupKey = $"instant:{input.Event.Id}:{runId}:{reason}"
        };
    }

    private bool HasRisingTrend(EventScore score, double trendHeat)
        => score.TrendEvidenceCount >= _config.Analysis.Event.MinTrendSamples &&
            trendHeat >= _config.Analysis.Event.MinTrendHeat &&
            score.TrendScore > 0;

    private bool IsReactivated(EventAggregate eventAggregate, DateTimeOffset runStartedAt, DateTimeOffset now)
        => eventAggregate.FirstSeenAt < runStartedAt &&
            eventAggregate.LastActivatedAt >= runStartedAt &&
            eventAggregate.LastActivatedAt <= now;

    private static bool IsRankedSnapshot(TrendReporter2.Core.Content.ContentSnapshot snapshot)
        => string.Equals(snapshot.ContentKind, ContentKind.RankedNews, StringComparison.OrdinalIgnoreCase) &&
            snapshot.Rank is not null &&
            snapshot.NormalizedRankScore is not null;

    private static bool IsFlashSnapshot(TrendReporter2.Core.Content.ContentSnapshot snapshot)
        => string.Equals(snapshot.ContentKind, ContentKind.FlashFeed, StringComparison.OrdinalIgnoreCase);

    private static bool IsTopicSnapshot(TrendReporter2.Core.Content.ContentSnapshot snapshot)
        => string.Equals(snapshot.ContentKind, ContentKind.Topic, StringComparison.OrdinalIgnoreCase);

    private static double CalculateTrendScore(IReadOnlyList<double> heatSeries)
    {
        if (heatSeries.Count < 3)
        {
            return 0;
        }

        var smoothed = new List<double>();
        var current = heatSeries[0];
        foreach (var heat in heatSeries)
        {
            current = 0.5 * heat + 0.5 * current;
            smoothed.Add(current);
        }

        var midpoint = smoothed.Count / 2;
        var past = smoothed.Take(midpoint).DefaultIfEmpty(0).Average();
        var recent = smoothed.Skip(midpoint).DefaultIfEmpty(0).Average();
        return Clamp01((recent - past) / Math.Max(past, 0.2));
    }

    private static double CalculateEwma(IReadOnlyList<double> values)
    {
        if (values.Count == 0)
        {
            return 0;
        }

        var current = values[0];
        foreach (var value in values.Skip(1))
        {
            current = 0.5 * value + 0.5 * current;
        }

        return current;
    }

    private static double CalculateTotalScore(EventScore score)
        => 100 * (
            0.35 * score.CoverageScore +
            0.25 * score.RankScore +
            0.20 * score.TrendScore +
            0.10 * score.PersistenceScore +
            0.10 * score.LlmBoostScore) +
            15 * score.FlashScore +
            score.ReactivationBonus;

    private static EventScoreSnapshot ToSnapshot(EventScore score)
        => new()
        {
            Id = $"ess:{score.RunId}:{score.EventId}",
            EventId = score.EventId,
            RunId = score.RunId,
            CalculatedAt = score.CalculatedAt,
            CoverageScore = score.CoverageScore,
            RankScore = score.RankScore,
            FlashScore = score.FlashScore,
            FreshnessScore = score.FreshnessScore,
            TrendScore = score.TrendScore,
            PersistenceScore = score.PersistenceScore,
            LlmBoostScore = score.LlmBoostScore,
            ReactivationBonus = score.ReactivationBonus,
            TotalScore = score.TotalScore,
            UniqueSourceCount = score.UniqueSourceCount,
            RankedSourceCount = score.RankedSourceCount,
            FlashSourceCount = score.FlashSourceCount,
            AvgRank = score.AvgRank,
            AvgNormalizedRank = score.AvgNormalizedRank,
            HeatValue = score.HeatValue,
            SmoothedHeatValue = score.SmoothedHeatValue,
            TrendEvidenceCount = score.TrendEvidenceCount,
            CurrentStage = score.CurrentStage,
            TriggerReasons = score.TriggerReasons.Distinct(StringComparer.OrdinalIgnoreCase).ToList()
        };

    private static string BuildPushLogId(string dedupKey)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(dedupKey));
        return $"pl:{Convert.ToHexString(hash)[..24].ToLowerInvariant()}";
    }

    private static string SelectPushReason(EventScore score)
    {
        var priority = new[]
        {
            TriggerReasons.FlashMultiSource,
            TriggerReasons.FlashRepeated,
            TriggerReasons.FlashFollowUp,
            TriggerReasons.FlashTrustedSource,
            TriggerReasons.CoverageRank,
            TriggerReasons.RisingTrend,
            TriggerReasons.JudgeHighImportance,
            TriggerReasons.Reactivation,
            TriggerReasons.SourceIncrease,
            TriggerReasons.RankImprovement,
            TriggerReasons.ScoreImprovement,
            TriggerReasons.FirstPush
        };
        return priority.FirstOrDefault(score.TriggerReasons.Contains) ?? score.TriggerReasons.LastOrDefault() ?? "符合条件";
    }

    private static string FormatReason(string reason)
        => reason.Replace('_', ' ');

    private static string BuildUnsentPayload(PushMessage message)
        => JsonConvert.SerializeObject(new { cate = "default", title = message.Title, msg = message.Message, link = message.Link });

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];

    private static string FormatBuckets(IReadOnlyDictionary<string, int> buckets)
        => buckets.Count == 0
            ? "none"
            : string.Join(',', buckets.OrderBy(bucket => bucket.Key, StringComparer.Ordinal).Select(bucket => $"{bucket.Key}:{bucket.Value}"));

    private static double Clamp01(double value) => Math.Clamp(value, 0, 1);

    private sealed record ProgressFallback(string Stage, string Summary, IReadOnlyList<EventMilestone> Milestones);

    private sealed record PushAttemptResult(bool Success, bool Recorded, string? Error);

    private sealed class ScoringRunCounters
    {
        private int _blacklistedCount;
        private int _eligibleCount;
        private int _judgeCalledCount;
        private int _judgePromotedCount;
        private int _pushAttemptCount;
        private int _pushSuccessCount;
        private readonly ConcurrentDictionary<string, int> _rejectionBuckets = new(StringComparer.Ordinal);

        public void IncrementBlacklisted() => Interlocked.Increment(ref _blacklistedCount);
        public void IncrementEligible() => Interlocked.Increment(ref _eligibleCount);
        public void IncrementJudgeCalled() => Interlocked.Increment(ref _judgeCalledCount);
        public void IncrementJudgePromoted() => Interlocked.Increment(ref _judgePromotedCount);
        public void IncrementPushAttempt() => Interlocked.Increment(ref _pushAttemptCount);
        public void IncrementPushSuccess() => Interlocked.Increment(ref _pushSuccessCount);
        public void IncrementRejection(string reason) => _rejectionBuckets.AddOrUpdate(NormalizeReason(reason), 1, (_, count) => count + 1);

        public ScoringRunCounterSnapshot Snapshot()
            => new(
                Volatile.Read(ref _blacklistedCount),
                Volatile.Read(ref _eligibleCount),
                Volatile.Read(ref _judgeCalledCount),
                Volatile.Read(ref _judgePromotedCount),
                Volatile.Read(ref _pushAttemptCount),
                Volatile.Read(ref _pushSuccessCount),
                _rejectionBuckets.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal));

        private static string NormalizeReason(string reason)
            => string.IsNullOrWhiteSpace(reason) ? "unknown" : reason.Trim();
    }

    private sealed record ScoringRunCounterSnapshot(
        int BlacklistedCount,
        int EligibleCount,
        int JudgeCalledCount,
        int JudgePromotedCount,
        int PushAttemptCount,
        int PushSuccessCount,
        IReadOnlyDictionary<string, int> RejectionBuckets);
}
