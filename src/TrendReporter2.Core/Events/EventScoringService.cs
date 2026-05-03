using System.Security.Cryptography;
using System.Text;
using TrendReporter2.Core.Configuration;

namespace TrendReporter2.Core.Events;

public sealed class EventScoringService : IEventScoringService
{
    private const double ReactivationBonusValue = 10;
    private readonly AppConfig _config;
    private readonly IEventRepository _repository;
    private readonly IJudgeLlmClient _judgeLlmClient;
    private readonly IEnumerable<IPusher> _pushers;

    public EventScoringService(
        AppConfig config,
        IEventRepository repository,
        IJudgeLlmClient judgeLlmClient,
        IEnumerable<IPusher> pushers)
    {
        _config = config;
        _repository = repository;
        _judgeLlmClient = judgeLlmClient;
        _pushers = pushers;
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

        var eventIds = inputs.Select(input => input.Event.Id).Distinct(StringComparer.Ordinal).ToList();
        var trendSince = now.AddHours(-Math.Max(1, _config.Analysis.Event.TrendWindowHours));
        var recentSnapshots = await _repository.LoadRecentScoreSnapshotsAsync(eventIds, trendSince, cancellationToken);
        var recentByEvent = recentSnapshots
            .GroupBy(snapshot => snapshot.EventId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderBy(snapshot => snapshot.CalculatedAt).ToList(), StringComparer.Ordinal);

        var eligibleCount = 0;
        var pushedCount = 0;

        foreach (var input in inputs)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var priorSnapshots = recentByEvent.GetValueOrDefault(input.Event.Id) ?? [];
            ApplyBlacklist(input.Event);
            var score = BuildBaseScore(runId, input, priorSnapshots, runStartedAt, now);
            var eligibleBeforeJudge = IsEligible(score, input.Event, runStartedAt, now);

            var judge = eligibleBeforeJudge || IsNearEligibility(score)
                ? await _judgeLlmClient.JudgeAsync(new JudgeRequest(input.Event, score, input.Evidence, score.TriggerReasons), cancellationToken)
                : JudgeResult.Neutral("event did not reach judge threshold");

            ApplyJudge(score, judge);
            var eligible = IsEligible(score, input.Event, runStartedAt, now);
            if (eligible)
            {
                eligibleCount++;
            }

            var progress = BuildProgress(input, score, priorSnapshots, judge, runStartedAt, now);
            ApplyProgress(input.Event, progress, judge, now);
            score.CurrentStage = input.Event.CurrentStage;

            if (eligible && ShouldPush(input.Event, score))
            {
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
                        pushedCount++;
                    }
                }
            }

            var snapshot = ToSnapshot(score);
            await _repository.InsertEventScoreSnapshotAsync(snapshot, cancellationToken);

            input.Event.UpdatedAt = now;
            await _repository.UpdateEventsAsync([input.Event], cancellationToken);
        }

        return new EventScoringRunResult(inputs.Count, eligibleCount, pushedCount);
    }

    private EventScore BuildBaseScore(
        string runId,
        RunEventScoringInput input,
        IReadOnlyList<EventScoreSnapshot> priorSnapshots,
        DateTimeOffset runStartedAt,
        DateTimeOffset now)
    {
        var snapshots = input.Evidence.Select(evidence => evidence.Snapshot).ToList();
        var uniqueSourceCount = snapshots.Select(snapshot => snapshot.Source).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var avgRank = snapshots.Count == 0 ? 0 : snapshots.Average(snapshot => snapshot.Rank);
        var avgNormalizedRank = snapshots.Count == 0 ? 0 : snapshots.Average(snapshot => snapshot.NormalizedRankScore);
        var heatValue = snapshots.Sum(snapshot => snapshot.NormalizedRankScore);
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
            TrendScore = trendScore,
            PersistenceScore = Clamp01((now - input.Event.FirstSeenAt).TotalHours / Math.Max(1, _config.Analysis.HistoryHours)),
            ReactivationBonus = reactivated ? ReactivationBonusValue : 0,
            UniqueSourceCount = uniqueSourceCount,
            AvgRank = avgRank,
            AvgNormalizedRank = avgNormalizedRank,
            HeatValue = heatValue,
            SmoothedHeatValue = smoothedHeat,
            TrendEvidenceCount = heatSeries.Count
        };

        if (uniqueSourceCount >= _config.Analysis.Event.SourceCount && avgNormalizedRank >= _config.Analysis.Event.NormalizedRankThreshold)
        {
            score.TriggerReasons.Add(TriggerReasons.CoverageRank);
        }

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
             score.TriggerReasons.Contains(TriggerReasons.JudgeHighImportance) ||
             IsReactivated(eventAggregate, runStartedAt, now));

    private bool IsNearEligibility(EventScore score)
        => score.UniqueSourceCount >= Math.Max(1, _config.Analysis.Event.SourceCount - 1) ||
            score.HeatValue >= _config.Analysis.Event.MinTrendHeat;

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
            ? $"First seen at {input.Event.FirstSeenAt:yyyy-MM-dd HH:mm}, now covered by {score.UniqueSourceCount} source(s) including {string.Join(", ", sources)}; latest development: {latestTitle}."
            : judge.ProgressSummary.Trim();

        var milestones = new List<EventMilestone>
        {
            new()
            {
                Time = input.Event.FirstSeenAt,
                Kind = "first_seen",
                Label = "First detected",
                Source = sources.FirstOrDefault(),
                Summary = $"Event entered monitoring as {input.Event.CanonicalTitle}."
            }
        };

        if (score.UniqueSourceCount > 1)
        {
            milestones.Add(new EventMilestone
            {
                Time = now,
                Kind = "source_expansion",
                Label = "Source coverage expanded",
                Source = sources.FirstOrDefault(),
                Summary = $"Coverage reached {score.UniqueSourceCount} sources with average normalized rank {score.AvgNormalizedRank:F2}."
            });
        }

        if (reactivated)
        {
            milestones.Add(new EventMilestone
            {
                Time = input.Event.LastActivatedAt,
                Kind = "reactivation",
                Label = "Follow-up detected",
                Source = sources.FirstOrDefault(),
                Summary = "A previously stale event became active again in this fetch run."
            });
        }

        if (milestones.Count < 3 && score.TrendScore > 0)
        {
            milestones.Add(new EventMilestone
            {
                Time = now,
                Kind = "heat_rising",
                Label = "Heat rising",
                Source = sources.FirstOrDefault(),
                Summary = $"Heat reached {score.HeatValue:F2} and trend score is {score.TrendScore:F2}."
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

    private bool ShouldPush(EventAggregate eventAggregate, EventScore score)
    {
        if (eventAggregate.IsBlacklisted)
        {
            return false;
        }

        if (eventAggregate.PushCount == 0 || eventAggregate.LastPushedAt is null)
        {
            score.TriggerReasons.Add(TriggerReasons.FirstPush);
            return true;
        }

        if (eventAggregate.LastPushSourceCount is null || score.UniqueSourceCount - eventAggregate.LastPushSourceCount.Value >= _config.Analysis.RepeatPush.SourceAddThreshold)
        {
            score.TriggerReasons.Add(TriggerReasons.SourceIncrease);
            return true;
        }

        if (eventAggregate.LastPushRankScore is null || score.RankScore - eventAggregate.LastPushRankScore.Value >= _config.Analysis.RepeatPush.RankScoreImproveThreshold)
        {
            score.TriggerReasons.Add(TriggerReasons.RankImprovement);
            return true;
        }

        if (eventAggregate.LastPushScore is null || score.TotalScore - eventAggregate.LastPushScore.Value >= _config.Analysis.RepeatPush.ScoreImproveThreshold)
        {
            score.TriggerReasons.Add(TriggerReasons.ScoreImprovement);
            return true;
        }

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
            Payload = BuildUnsentPayload(message),
            DedupKey = message.DedupKey,
            Success = false,
            Error = "pending"
        };
        var inserted = await _repository.InsertPushLogIfMissingAsync(pushLog, cancellationToken);
        if (!inserted)
        {
            return new PushAttemptResult(false, false);
        }

        var pusher = _pushers.FirstOrDefault(pusher => pusher.IsConfigured);
        var result = pusher is null
            ? PushResult.Skipped("no configured pusher", pushLog.Payload)
            : await pusher.PushAsync(message, cancellationToken);
        pushLog.Payload = result.Payload;
        pushLog.Success = result.Success;
        pushLog.Error = result.Error;
        await _repository.UpdatePushLogAsync(pushLog, cancellationToken);

        return new PushAttemptResult(result.Success, true);
    }

    private PushMessage BuildPushMessage(string runId, RunEventScoringInput input, EventScore score)
    {
        var reason = score.TriggerReasons.LastOrDefault() ?? score.TriggerReasons.FirstOrDefault() ?? "eligible";
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
        return new PushMessage
        {
            Title = input.Event.CanonicalTitle,
            Message = $"[{stage}] {summary} Why now: {FormatReason(reason)}. Sources: {string.Join(", ", sources)}. Score {score.TotalScore:F1}, heat {score.HeatValue:F2}.",
            Link = link,
            EventId = input.Event.Id,
            PushType = PushTypes.Instant,
            DedupKey = $"instant:{input.Event.Id}:{runId}:{reason}"
        };
    }

    private void ApplyBlacklist(EventAggregate eventAggregate)
    {
        var text = string.Join(' ', eventAggregate.CanonicalTitle, eventAggregate.Summary);
        var keyword = _config.Filters.BlacklistKeywords
            .FirstOrDefault(keyword => !string.IsNullOrWhiteSpace(keyword) && text.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        if (keyword is null)
        {
            return;
        }

        eventAggregate.IsBlacklisted = true;
        eventAggregate.BlacklistReason = $"Matched blacklist keyword: {keyword}";
    }

    private bool HasRisingTrend(EventScore score, double trendHeat)
        => score.TrendEvidenceCount >= _config.Analysis.Event.MinTrendSamples &&
            trendHeat >= _config.Analysis.Event.MinTrendHeat &&
            score.TrendScore > 0;

    private bool IsReactivated(EventAggregate eventAggregate, DateTimeOffset runStartedAt, DateTimeOffset now)
        => eventAggregate.FirstSeenAt < runStartedAt &&
            eventAggregate.LastActivatedAt >= runStartedAt &&
            eventAggregate.LastActivatedAt <= now;

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
            0.10 * score.LlmBoostScore) + score.ReactivationBonus;

    private static EventScoreSnapshot ToSnapshot(EventScore score)
        => new()
        {
            Id = $"ess:{score.RunId}:{score.EventId}",
            EventId = score.EventId,
            RunId = score.RunId,
            CalculatedAt = score.CalculatedAt,
            CoverageScore = score.CoverageScore,
            RankScore = score.RankScore,
            TrendScore = score.TrendScore,
            PersistenceScore = score.PersistenceScore,
            LlmBoostScore = score.LlmBoostScore,
            ReactivationBonus = score.ReactivationBonus,
            TotalScore = score.TotalScore,
            UniqueSourceCount = score.UniqueSourceCount,
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

    private static string FormatReason(string reason)
        => reason.Replace('_', ' ');

    private static string BuildUnsentPayload(PushMessage message)
        => $"{{\"cate\":\"default\",\"title\":\"{EscapeJson(message.Title)}\",\"msg\":\"{EscapeJson(message.Message)}\",\"link\":\"{EscapeJson(message.Link)}\"}}";

    private static string EscapeJson(string? value)
        => (value ?? string.Empty)
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal);

    private static double Clamp01(double value) => Math.Clamp(value, 0, 1);

    private sealed record ProgressFallback(string Stage, string Summary, IReadOnlyList<EventMilestone> Milestones);

    private sealed record PushAttemptResult(bool Success, bool Recorded);
}
