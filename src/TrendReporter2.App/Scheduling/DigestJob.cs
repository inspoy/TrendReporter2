using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using TrendReporter2.Core.Configuration;
using TrendReporter2.Core.Events;
using TrendReporter2.Core.Jobs;
using TrendReporter2.Core.Reports;

namespace TrendReporter2.App.Scheduling;

public sealed class DigestJob : IDigestJob
{
    private static readonly EventId DigestSkippedEventId = new(3001, "DigestSkipped");
    private static readonly EventId DigestAttemptEventId = new(3002, "DigestAttempt");
    private static readonly EventId DigestSucceededEventId = new(3003, "DigestSucceeded");
    private static readonly EventId DigestFailedEventId = new(3004, "DigestFailed");
    private readonly AppConfig _config;
    private readonly IEventRepository _eventRepository;
    private readonly IAppStateRepository _stateRepository;
    private readonly IReportReadModelQuery? _reportReadModelQuery;
    private readonly IStaticHtmlReportRenderer? _reportRenderer;
    private readonly IReportSnapshotRepository? _reportSnapshotRepository;
    private readonly IEnumerable<IPusher> _pushers;
    private readonly ILogger _logger;

    public DigestJob(
        AppConfig config,
        IEventRepository eventRepository,
        IAppStateRepository stateRepository,
        IEnumerable<IPusher> pushers,
        ILoggerFactory loggerFactory)
        : this(config, eventRepository, stateRepository, pushers, null, null, null, loggerFactory)
    {
    }

    public DigestJob(
        AppConfig config,
        IEventRepository eventRepository,
        IAppStateRepository stateRepository,
        IEnumerable<IPusher> pushers,
        IReportReadModelQuery? reportReadModelQuery,
        IStaticHtmlReportRenderer? reportRenderer,
        IReportSnapshotRepository? reportSnapshotRepository,
        ILoggerFactory loggerFactory)
    {
        _config = config;
        _eventRepository = eventRepository;
        _stateRepository = stateRepository;
        _reportReadModelQuery = reportReadModelQuery;
        _reportRenderer = reportRenderer;
        _reportSnapshotRepository = reportSnapshotRepository;
        _pushers = pushers;
        _logger = loggerFactory.CreateLogger("DigestJob");
    }

    public async Task RunAsync(DateOnly localDate, string slotTime, DateTimeOffset localNow, CancellationToken cancellationToken)
    {
        var dateText = localDate.ToString("yyyy-MM-dd");
        var dedupKey = $"digest:{dateText}:{slotTime}";
        var stateKey = $"digest:processed:{dateText}:{slotTime}";
        var scheduledSlot = BuildScheduledSlot(localDate, slotTime, localNow);
        var now = DateTimeOffset.UtcNow;
        var existingState = await _stateRepository.GetAsync(stateKey, cancellationToken);
        if (existingState is not null)
        {
            _logger.LogInformation(DigestSkippedEventId, "跳过摘要任务，状态已记录。日期={LocalDate}，时段={SlotTime}。", localDate, slotTime);
            return;
        }

        var since = now.AddHours(-Math.Max(1, _config.Analysis.HistoryHours));
        var candidates = await _eventRepository.LoadDigestCandidatesAsync(since, Math.Max(1, _config.Analysis.Push.PushCount), cancellationToken);
        var loadedCandidateCount = candidates.Count;
        var newlyBlacklisted = candidates
            .Select(candidate => candidate.Event)
            .Where(eventAggregate => EventBlacklistPolicy.Apply(eventAggregate, _config.Filters))
            .ToList();
        if (newlyBlacklisted.Count > 0)
        {
            foreach (var eventAggregate in newlyBlacklisted)
            {
                eventAggregate.UpdatedAt = now;
            }

            await _eventRepository.UpdateEventsAsync(newlyBlacklisted, cancellationToken);
        }

        candidates = candidates
            .Where(candidate => !candidate.Event.IsBlacklisted)
            .ToList();

        _logger.LogInformation(
            "摘要候选过滤完成。日期={LocalDate}，时段={SlotTime}，加载候选数={LoadedCandidateCount}，新增黑名单数={NewlyBlacklistedCount}，选中候选数={SelectedCandidateCount}。",
            localDate,
            slotTime,
            loadedCandidateCount,
            newlyBlacklisted.Count,
            candidates.Count);
        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug(
                "摘要候选明细。日期={LocalDate}，时段={SlotTime}，选中事件={SelectedEventIds}，新增黑名单事件={NewlyBlacklistedEventIds}。",
                localDate,
                slotTime,
                string.Join(',', candidates.Select(candidate => candidate.Event.Id)),
                string.Join(',', newlyBlacklisted.Select(eventAggregate => eventAggregate.Id)));
        }

        var message = candidates.Count == 0
            ? BuildSkippedMessage(dateText, slotTime, dedupKey)
            : BuildDigestMessage(dateText, slotTime, candidates, dedupKey, reportLink: null);

        var pushLog = new PushLog
        {
            Id = BuildPushLogId(dedupKey),
            EventId = null,
            PushType = PushTypes.Digest,
            PushedAt = now,
            Title = message.Title,
            Reason = message.Reason,
            Content = message.Content,
            Payload = "{}",
            DedupKey = dedupKey,
            Success = false,
            Error = "待处理"
        };

        var inserted = await _eventRepository.InsertPushLogIfMissingAsync(pushLog, cancellationToken);
        if (!inserted)
        {
            _logger.LogInformation(DigestSkippedEventId, "跳过摘要推送，推送日志已存在。DedupKey={DedupKey}。", dedupKey);
            await MarkProcessedAsync(stateKey, "push_log_exists", now, cancellationToken);
            return;
        }

        if (candidates.Count > 0)
        {
            var reportLink = await TryGenerateReportAsync(since, now, slotTime, scheduledSlot, cancellationToken);
            if (!string.IsNullOrWhiteSpace(reportLink))
            {
                message = BuildDigestMessage(dateText, slotTime, candidates, dedupKey, reportLink);
                pushLog.Content = message.Content;
            }
        }

        PushResult result;
        if (candidates.Count == 0)
        {
            result = PushResult.Skipped("没有摘要候选事件", "{}");
        }
        else
        {
            _logger.LogInformation(DigestAttemptEventId, "尝试发送摘要推送。日期={LocalDate}，时段={SlotTime}，候选数={CandidateCount}。", localDate, slotTime, candidates.Count);
            var pusher = _pushers.FirstOrDefault(pusher => pusher.IsConfigured);
            result = pusher is null
                ? PushResult.Skipped("没有已配置的推送器", "{}")
                : await pusher.PushAsync(message, cancellationToken);
        }

        pushLog.Payload = result.Payload;
        pushLog.Success = result.Success;
        pushLog.Error = result.Error;
        await _eventRepository.UpdatePushLogAsync(pushLog, cancellationToken);
        await MarkProcessedAsync(stateKey, result.Success ? "success" : $"skipped_or_failed:{result.Error}", now, cancellationToken);

        if (result.Success)
        {
            _logger.LogInformation(DigestSucceededEventId, "摘要推送成功。日期={LocalDate}，时段={SlotTime}，候选数={CandidateCount}。", localDate, slotTime, candidates.Count);
        }
        else
        {
            _logger.LogWarning(DigestFailedEventId, "摘要推送失败或跳过。日期={LocalDate}，时段={SlotTime}，错误={Error}。", localDate, slotTime, result.Error);
        }
    }

    private async Task MarkProcessedAsync(string stateKey, string value, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await _stateRepository.UpsertAsync(new AppState
        {
            Key = stateKey,
            Value = value,
            UpdatedAt = now
        }, cancellationToken);
    }

    private async Task<string?> TryGenerateReportAsync(
        DateTimeOffset since,
        DateTimeOffset now,
        string slotTime,
        DateTimeOffset scheduledSlot,
        CancellationToken cancellationToken)
    {
        if (!_config.Report.Enabled || _reportReadModelQuery is null || _reportRenderer is null || _reportSnapshotRepository is null)
        {
            return null;
        }

        try
        {
            var payload = await _reportReadModelQuery.BuildDigestReportAsync(since, now, slotTime, Math.Max(1, _config.Analysis.Push.PushCount), cancellationToken);
            var rendered = await _reportRenderer.RenderAsync(payload, cancellationToken);
            var snapshot = new ReportSnapshot
            {
                Id = BuildReportSnapshotId(ReportTypes.DigestHtml, scheduledSlot),
                ReportType = ReportTypes.DigestHtml,
                SlotTime = scheduledSlot,
                GeneratedAt = now,
                FilePath = rendered.FilePath,
                PublicUrl = rendered.PublicUrl,
                EventCount = payload.Events.Count,
                PayloadJson = JsonSerializer.Serialize(payload)
            };
            await _reportSnapshotRepository.UpsertAsync(snapshot, cancellationToken);
            return _config.Report.IncludeInDigestPush ? rendered.PublicUrl : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "静态摘要报告生成失败；摘要推送将继续。时段={SlotTime}。", slotTime);
            return null;
        }
    }

    private static PushMessage BuildSkippedMessage(string dateText, string slotTime, string dedupKey)
    {
        var content = $"{dateText} {slotTime} 摘要未发送：统计窗口内没有可推送的非黑名单事件。";
        return new PushMessage
        {
            Title = $"舆情摘要 {dateText} {slotTime}",
            Message = content,
            Reason = "scheduled_digest_empty",
            Content = content,
            PushType = PushTypes.Digest,
            DedupKey = dedupKey
        };
    }

    private static PushMessage BuildDigestMessage(string dateText, string slotTime, IReadOnlyList<DigestCandidate> candidates, string dedupKey, string? reportLink)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"{dateText} {slotTime} 舆情摘要，共 {candidates.Count} 个事件：");
        if (!string.IsNullOrWhiteSpace(reportLink))
        {
            builder.AppendLine($"静态报告：{reportLink}");
        }

        for (var i = 0; i < candidates.Count; i++)
        {
            var candidate = candidates[i];
            builder.AppendLine();
            builder.AppendLine($"{i + 1}. {candidate.Event.CanonicalTitle}");
            builder.AppendLine($"摘要：{OneLine(candidate.Event.Summary)}");
            builder.AppendLine($"阶段：{Stage(candidate.Event, candidate.Score)}");
            builder.AppendLine($"进程：{Progress(candidate.Event)}");
            builder.AppendLine($"热度：总分 {candidate.Score.TotalScore:F1}，Heat {candidate.Score.HeatValue:F2}，来源数 {candidate.Score.UniqueSourceCount}，平均排名 {candidate.Score.AvgRank:F1}。");
            builder.AppendLine($"来源/术语：{SourcesAndTerms(candidate.Event)}");
        }

        var content = builder.ToString().TrimEnd();
        return new PushMessage
        {
            Title = $"舆情摘要 {dateText} {slotTime}",
            Message = content,
            Reason = "scheduled_digest",
            Content = content,
            PushType = PushTypes.Digest,
            DedupKey = dedupKey
        };
    }

    private static string Stage(EventAggregate eventAggregate, EventScoreSnapshot score)
        => string.IsNullOrWhiteSpace(eventAggregate.CurrentStage) ? score.CurrentStage ?? EventProgressStages.Initial : eventAggregate.CurrentStage;

    private static string Progress(EventAggregate eventAggregate)
    {
        if (!string.IsNullOrWhiteSpace(eventAggregate.ProgressSummary))
        {
            return OneLine(eventAggregate.ProgressSummary);
        }

        var milestones = eventAggregate.Milestones
            .OrderByDescending(milestone => milestone.Time)
            .Take(2)
            .Select(milestone => $"{milestone.Label}: {OneLine(milestone.Summary)}")
            .ToList();
        return milestones.Count == 0 ? "暂无进程摘要" : string.Join("；", milestones);
    }

    private static string SourcesAndTerms(EventAggregate eventAggregate)
    {
        var sources = eventAggregate.Milestones
            .Select(milestone => milestone.Source)
            .Where(source => !string.IsNullOrWhiteSpace(source))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(3)
            .ToList();
        var terms = eventAggregate.KeyTerms
            .Concat(eventAggregate.Entities)
            .Where(term => !string.IsNullOrWhiteSpace(term))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(5)
            .ToList();
        var sourceText = sources.Count == 0 ? "未记录来源" : string.Join(", ", sources);
        var termText = terms.Count == 0 ? "未记录术语" : string.Join(", ", terms);
        return $"{sourceText}；{termText}";
    }

    private static string OneLine(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "暂无摘要";
        }

        return value.ReplaceLineEndings(" ").Trim();
    }

    private static string BuildPushLogId(string dedupKey)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(dedupKey));
        return $"pl:{Convert.ToHexString(hash)[..24].ToLowerInvariant()}";
    }

    private static DateTimeOffset BuildScheduledSlot(DateOnly localDate, string slotTime, DateTimeOffset localNow)
    {
        return TimeOnly.TryParseExact(slotTime, "HH:mm", out var time)
            ? new DateTimeOffset(localDate.ToDateTime(time), localNow.Offset)
            : localNow;
    }

    private static string BuildReportSnapshotId(string reportType, DateTimeOffset slotTime)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{reportType}|{slotTime.UtcDateTime:O}"));
        return $"report:{reportType}:{Convert.ToHexString(hash)[..24].ToLowerInvariant()}";
    }
}
