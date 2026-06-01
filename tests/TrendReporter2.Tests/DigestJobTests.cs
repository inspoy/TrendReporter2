using Microsoft.Extensions.Logging.Abstractions;
using TrendReporter2.App.Scheduling;
using TrendReporter2.Core.Configuration;
using TrendReporter2.Core.Content;
using TrendReporter2.Core.Events;
using TrendReporter2.Core.Reports;

namespace TrendReporter2.Tests;

public sealed class DigestJobTests
{
    [Fact]
    public async Task RunAsync_RecordsDigestStateAndSkipsRepeatedSlot()
    {
        var now = DateTimeOffset.Parse("2026-05-05T08:20:00Z");
        var eventRepository = new DigestRepository([
            new DigestCandidate(new EventAggregate
            {
                Id = "event-ai",
                CanonicalTitle = "OpenAI starts GPT-4o voice rollout",
                Summary = "OpenAI said GPT-4o voice access is expanding to paid users.",
                CurrentStage = EventProgressStages.Expanding,
                ProgressSummary = "Rollout expanded from limited testing to paid users.",
                Milestones = [new EventMilestone { Time = now, Label = "扩展", Summary = "功能开始扩大开放。", Source = "Reuters" }],
                LastSeenAt = now,
                Status = EventStatus.Active
            }, new EventScoreSnapshot
            {
                EventId = "event-ai",
                TotalScore = 88,
                HeatValue = 2.7,
                UniqueSourceCount = 3,
                AvgRank = 2,
                CalculatedAt = now,
                CurrentStage = EventProgressStages.Expanding
            })
        ]);
        var stateRepository = new StateRepository();
        var pusher = new DigestPusher();
        var job = new DigestJob(new AppConfig { Analysis = new AnalysisConfig { Push = new PushConfig { PushCount = 3 } } }, eventRepository, stateRepository, [pusher], NullLoggerFactory.Instance);

        await job.RunAsync(new DateOnly(2026, 5, 5), "08:20", now, CancellationToken.None);
        await job.RunAsync(new DateOnly(2026, 5, 5), "08:20", now, CancellationToken.None);

        Assert.Single(pusher.Messages);
        Assert.Single(eventRepository.PushLogs);
        Assert.Equal("digest:2026-05-05:08:20", eventRepository.PushLogs.Single().DedupKey);
        Assert.True(eventRepository.PushLogs.Single().Success);
        Assert.Equal("success", stateRepository.States.Single().Value.Value);
    }

    [Fact]
    public async Task RunAsync_WhenReportEnabled_GeneratesSnapshotAndAddsReportLink()
    {
        var now = DateTimeOffset.Parse("2026-05-05T08:20:00Z");
        var eventRepository = new DigestRepository([
            new DigestCandidate(new EventAggregate
            {
                Id = "event-ai",
                CanonicalTitle = "OpenAI starts GPT-4o voice rollout",
                Summary = "OpenAI said GPT-4o voice access is expanding to paid users.",
                LastSeenAt = now,
                Status = EventStatus.Active
            }, new EventScoreSnapshot
            {
                EventId = "event-ai",
                TotalScore = 88,
                HeatValue = 2.7,
                UniqueSourceCount = 3,
                AvgRank = 2,
                CalculatedAt = now
            })
        ]);
        var stateRepository = new StateRepository();
        var pusher = new DigestPusher();
        var reportQuery = new ReportQuery();
        var reportRenderer = new ReportRenderer("/tmp/report.html", "https://reports.local/digest.html");
        var reportSnapshots = new ReportSnapshotRepository();
        var job = new DigestJob(
            new AppConfig { Analysis = new AnalysisConfig { Push = new PushConfig { PushCount = 3 } }, Report = new ReportConfig { Enabled = true, IncludeInDigestPush = true } },
            eventRepository,
            stateRepository,
            [pusher],
            reportQuery,
            reportRenderer,
            reportSnapshots,
            NullLoggerFactory.Instance);

        await job.RunAsync(new DateOnly(2026, 5, 5), "08:20", now, CancellationToken.None);

        Assert.Single(reportQuery.Payloads);
        Assert.Single(reportSnapshots.Snapshots);
        Assert.Contains("https://reports.local/digest.html", pusher.Messages.Single().Content);
        Assert.Equal(1, reportSnapshots.Snapshots.Single().EventCount);
    }

    [Fact]
    public async Task RunAsync_WhenPushLogAlreadyExists_DoesNotGenerateReport()
    {
        var now = DateTimeOffset.Parse("2026-05-05T08:20:00Z");
        var eventRepository = new DigestRepository([
            new DigestCandidate(new EventAggregate
            {
                Id = "event-ai",
                CanonicalTitle = "OpenAI starts GPT-4o voice rollout",
                Summary = "OpenAI said GPT-4o voice access is expanding to paid users.",
                LastSeenAt = now,
                Status = EventStatus.Active
            }, new EventScoreSnapshot
            {
                EventId = "event-ai",
                TotalScore = 88,
                HeatValue = 2.7,
                UniqueSourceCount = 3,
                AvgRank = 2,
                CalculatedAt = now
            })
        ]);
        eventRepository.PushLogs.Add(new PushLog { Id = "existing", DedupKey = "digest:2026-05-05:08:20" });
        var stateRepository = new StateRepository();
        var pusher = new DigestPusher();
        var reportQuery = new ReportQuery();
        var reportSnapshots = new ReportSnapshotRepository();
        var job = new DigestJob(
            new AppConfig { Analysis = new AnalysisConfig { Push = new PushConfig { PushCount = 3 } }, Report = new ReportConfig { Enabled = true, IncludeInDigestPush = true } },
            eventRepository,
            stateRepository,
            [pusher],
            reportQuery,
            new ReportRenderer("/tmp/report.html", "https://reports.local/digest.html"),
            reportSnapshots,
            NullLoggerFactory.Instance);

        await job.RunAsync(new DateOnly(2026, 5, 5), "08:20", now, CancellationToken.None);

        Assert.Empty(reportQuery.Payloads);
        Assert.Empty(reportSnapshots.Snapshots);
        Assert.Empty(pusher.Messages);
        Assert.Equal("push_log_exists", stateRepository.States.Single().Value.Value);
    }

    private sealed class DigestRepository : IEventRepository
    {
        private readonly IReadOnlyList<DigestCandidate> _candidates;
        public DigestRepository(IReadOnlyList<DigestCandidate> candidates) => _candidates = candidates;
        public List<PushLog> PushLogs { get; } = [];

        public Task<IReadOnlyList<DigestCandidate>> LoadDigestCandidatesAsync(DateTimeOffset since, int limit, CancellationToken cancellationToken) => Task.FromResult(_candidates.Take(limit).ToList() as IReadOnlyList<DigestCandidate>);
        public Task<bool> InsertPushLogIfMissingAsync(PushLog pushLog, CancellationToken cancellationToken)
        {
            if (PushLogs.Any(log => log.DedupKey == pushLog.DedupKey))
            {
                return Task.FromResult(false);
            }

            PushLogs.Add(pushLog);
            return Task.FromResult(true);
        }

        public Task UpdatePushLogAsync(PushLog pushLog, CancellationToken cancellationToken)
        {
            var index = PushLogs.FindIndex(log => log.DedupKey == pushLog.DedupKey);
            PushLogs[index] = pushLog;
            return Task.CompletedTask;
        }

        public Task UpdateEventsAsync(IReadOnlyList<EventAggregate> events, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<ContentItem>> LoadUnmappedRunContentItemsAsync(string runId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ContentItem>>([]);
        public Task<IReadOnlyList<EventAggregate>> LoadRecallCandidatesAsync(DateTimeOffset now, int historyHours, int staleHours, int archiveRecallDays, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<EventAggregate>>([]);
        public Task MarkStaleEventsAsync(DateTimeOffset now, int staleHours, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<EventAggregate?> GetEventAsync(string eventId, CancellationToken cancellationToken) => Task.FromResult<EventAggregate?>(null);
        public Task UpsertEventAsync(EventAggregate eventAggregate, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<bool> MapEventItemIfMissingAsync(EventItem eventItem, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task<IReadOnlyList<RunEventScoringInput>> LoadRunEventScoringInputsAsync(string runId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<RunEventScoringInput>>([]);
        public Task<IReadOnlyList<EventScoreSnapshot>> LoadRecentScoreSnapshotsAsync(IReadOnlyList<string> eventIds, DateTimeOffset since, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<EventScoreSnapshot>>([]);
        public Task InsertEventScoreSnapshotAsync(EventScoreSnapshot snapshot, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class StateRepository : IAppStateRepository
    {
        public Dictionary<string, AppState> States { get; } = new(StringComparer.Ordinal);
        public Task<AppState?> GetAsync(string key, CancellationToken cancellationToken) => Task.FromResult(States.GetValueOrDefault(key));
        public Task UpsertAsync(AppState state, CancellationToken cancellationToken) { States[state.Key] = state; return Task.CompletedTask; }
    }

    private sealed class DigestPusher : IPusher
    {
        public string Type => "fake";
        public bool IsConfigured => true;
        public List<PushMessage> Messages { get; } = [];
        public Task<PushResult> PushAsync(PushMessage message, CancellationToken cancellationToken)
        {
            Messages.Add(message);
            return Task.FromResult(new PushResult(true, "{\"ok\":true}", null));
        }
    }

    private sealed class ReportQuery : IReportReadModelQuery
    {
        public List<ReportPayload> Payloads { get; } = [];

        public Task<ReportPayload> BuildDigestReportAsync(DateTimeOffset windowStart, DateTimeOffset windowEnd, string slotTime, int limit, CancellationToken cancellationToken)
        {
            var payload = new ReportPayload
            {
                GeneratedAt = windowEnd,
                WindowStart = windowStart,
                WindowEnd = windowEnd,
                SlotTime = slotTime,
                Events = [new ReportEventItem { EventId = "event-ai", Title = "OpenAI", Summary = "摘要", TotalScore = 88, HeatValue = 2.7, UniqueSourceCount = 3 }]
            };
            Payloads.Add(payload);
            return Task.FromResult(payload);
        }
    }

    private sealed class ReportRenderer : IStaticHtmlReportRenderer
    {
        private readonly string _filePath;
        private readonly string _publicUrl;

        public ReportRenderer(string filePath, string publicUrl)
        {
            _filePath = filePath;
            _publicUrl = publicUrl;
        }

        public Task<RenderedReport> RenderAsync(ReportPayload payload, CancellationToken cancellationToken)
            => Task.FromResult(new RenderedReport(_filePath, _publicUrl));
    }

    private sealed class ReportSnapshotRepository : IReportSnapshotRepository
    {
        public List<ReportSnapshot> Snapshots { get; } = [];

        public Task UpsertAsync(ReportSnapshot snapshot, CancellationToken cancellationToken)
        {
            Snapshots.Add(snapshot);
            return Task.CompletedTask;
        }
    }
}
