using Microsoft.Extensions.Logging.Abstractions;
using TrendReporter2.Core.Configuration;
using TrendReporter2.Core.Content;
using TrendReporter2.Core.Events;
using TrendReporter2.Core.Sources;

namespace TrendReporter2.Tests;

public sealed class EventScoringServiceTests
{
    [Fact]
    public async Task ScoreAndPushRunAsync_ComputesHeatTrendEligibilityAndFirstPush()
    {
        var now = DateTimeOffset.Parse("2026-05-05T08:00:00Z");
        var runStartedAt = now.AddMinutes(-5);
        var repository = new FakeEventRepository
        {
            Inputs = [BuildInput("event-1", now.AddHours(-3), BuildEvidence(3))],
            RecentSnapshots =
            [
                Snapshot("event-1", now.AddHours(-2), 0.1),
                Snapshot("event-1", now.AddHours(-1), 0.4)
            ]
        };
        var pusher = new FakePusher();
        var service = new EventScoringService(Config(), repository, new FakeJudgeLlmClient(), [pusher], EmptySourceRegistry(), NullLoggerFactory.Instance);

        var result = await service.ScoreAndPushRunAsync("run-1", runStartedAt, now, CancellationToken.None);

        Assert.Equal(new EventScoringRunResult(1, 1, 1), result);
        var score = Assert.Single(repository.ScoreSnapshots);
        Assert.Equal(3, score.UniqueSourceCount);
        Assert.Equal(3, score.RankedSourceCount);
        Assert.Equal(0, score.FlashSourceCount);
        Assert.Equal(0, score.FlashScore);
        Assert.Equal(0.9, score.RankScore, 3);
        Assert.Equal(3, score.TrendEvidenceCount);
        Assert.True(score.HeatValue > 2.4);
        Assert.Contains(TriggerReasons.CoverageRank, score.TriggerReasons);
        Assert.Contains(TriggerReasons.RisingTrend, score.TriggerReasons);
        Assert.Contains(TriggerReasons.FirstPush, score.TriggerReasons);
        Assert.Equal(EventProgressStages.Escalating, score.CurrentStage);
        Assert.Equal(EventProgressStages.Escalating, repository.UpdatedEvents.Single().CurrentStage);
        Assert.False(string.IsNullOrWhiteSpace(repository.UpdatedEvents.Single().ProgressSummary));
        Assert.NotEmpty(repository.UpdatedEvents.Single().Milestones);
        Assert.Single(pusher.Messages);
        Assert.Equal(1, repository.UpdatedEvents.Single().PushCount);
    }

    [Fact]
    public async Task ScoreAndPushRunAsync_AllowsFlashEvidenceWithoutRankToBecomeEligible()
    {
        var now = DateTimeOffset.Parse("2026-05-05T08:00:00Z");
        var repository = new FakeEventRepository
        {
            Inputs = [BuildInput("event-flash", now.AddMinutes(-30), BuildFlashEvidence(3, 1.0))]
        };
        var pusher = new FakePusher();
        var service = new EventScoringService(Config(), repository, new FakeJudgeLlmClient(), [pusher], EmptySourceRegistry(), NullLoggerFactory.Instance);

        var result = await service.ScoreAndPushRunAsync("run-flash", now.AddMinutes(-5), now, CancellationToken.None);

        Assert.Equal(new EventScoringRunResult(1, 1, 1), result);
        var score = Assert.Single(repository.ScoreSnapshots);
        Assert.Equal(0, score.RankedSourceCount);
        Assert.Equal(3, score.FlashSourceCount);
        Assert.Equal(0, score.RankScore);
        Assert.Equal(0, score.AvgRank);
        Assert.Equal(0, score.AvgNormalizedRank);
        Assert.Equal(1, score.FreshnessScore);
        Assert.Equal(1, score.FlashScore, 3);
        Assert.Contains(TriggerReasons.FlashMultiSource, score.TriggerReasons);
        Assert.Contains(TriggerReasons.FlashRepeated, score.TriggerReasons);
        Assert.Contains(TriggerReasons.FirstPush, score.TriggerReasons);
        Assert.Single(pusher.Messages);
        Assert.Equal(TriggerReasons.FlashMultiSource, pusher.Messages.Single().Reason);
    }

    [Fact]
    public async Task ScoreAndPushRunAsync_DoesNotMakeTopicEvidenceStrongPushEligible()
    {
        var now = DateTimeOffset.Parse("2026-05-05T08:00:00Z");
        var repository = new FakeEventRepository
        {
            Inputs = [BuildInput("event-topic", now.AddMinutes(-30), BuildTopicEvidence(3))]
        };
        var pusher = new FakePusher();
        var service = new EventScoringService(Config(), repository, new FakeJudgeLlmClient(), [pusher], EmptySourceRegistry(), NullLoggerFactory.Instance);

        var result = await service.ScoreAndPushRunAsync("run-topic", now.AddMinutes(-5), now, CancellationToken.None);

        Assert.Equal(new EventScoringRunResult(1, 0, 0), result);
        var score = Assert.Single(repository.ScoreSnapshots);
        Assert.Equal(0, score.UniqueSourceCount);
        Assert.Equal(0, score.RankedSourceCount);
        Assert.Equal(0, score.FlashSourceCount);
        Assert.Equal(0, score.RankScore);
        Assert.Equal(0, score.FlashScore);
        Assert.DoesNotContain(TriggerReasons.CoverageRank, score.TriggerReasons);
        Assert.DoesNotContain(TriggerReasons.FlashMultiSource, score.TriggerReasons);
        Assert.Empty(pusher.Messages);
    }

    [Fact]
    public async Task ScoreAndPushRunAsync_AppliesRepeatPushThresholdsAndDedupSkip()
    {
        var now = DateTimeOffset.Parse("2026-05-05T08:00:00Z");
        var repeatEvent = BuildEvent("event-repeat", now.AddHours(-6));
        repeatEvent.PushCount = 1;
        repeatEvent.LastPushedAt = now.AddHours(-1);
        repeatEvent.LastPushSourceCount = 3;
        repeatEvent.LastPushRankScore = 0.5;
        repeatEvent.LastPushScore = 50;
        var repository = new FakeEventRepository
        {
            Inputs = [new RunEventScoringInput(repeatEvent, BuildEvidence(5))]
        };
        var pusher = new FakePusher();
        var service = new EventScoringService(Config(), repository, new FakeJudgeLlmClient(), [pusher], EmptySourceRegistry(), NullLoggerFactory.Instance);

        var result = await service.ScoreAndPushRunAsync("run-repeat", now.AddMinutes(-5), now, CancellationToken.None);

        Assert.Equal(1, result.PushedEventCount);
        var score = Assert.Single(repository.ScoreSnapshots);
        Assert.Contains(TriggerReasons.SourceIncrease, score.TriggerReasons);
        Assert.Single(pusher.Messages);

        var dedupRepository = new FakeEventRepository
        {
            Inputs = [BuildInput("event-dedup", now.AddHours(-6), BuildEvidence(3))],
            InsertPushLogResult = false
        };
        var dedupPusher = new FakePusher();
        var dedupService = new EventScoringService(Config(), dedupRepository, new FakeJudgeLlmClient(), [dedupPusher], EmptySourceRegistry(), NullLoggerFactory.Instance);

        var dedupResult = await dedupService.ScoreAndPushRunAsync("run-dedup", now.AddMinutes(-5), now, CancellationToken.None);

        Assert.Equal(0, dedupResult.PushedEventCount);
        Assert.Empty(dedupPusher.Messages);
        Assert.Single(dedupRepository.PushLogs);
        Assert.Single(dedupRepository.ScoreSnapshots);
    }

    [Fact]
    public async Task ScoreAndPushRunAsync_BlocksBlacklistedEvents()
    {
        var now = DateTimeOffset.Parse("2026-05-05T08:00:00Z");
        var repository = new FakeEventRepository
        {
            Inputs = [BuildInput("event-blacklist", now.AddHours(-3), BuildEvidence(3), "广告事件")]
        };
        var pusher = new FakePusher();
        var service = new EventScoringService(Config(blacklistKeywords: ["广告"]), repository, new FakeJudgeLlmClient(), [pusher], EmptySourceRegistry(), NullLoggerFactory.Instance);

        var result = await service.ScoreAndPushRunAsync("run-blacklist", now.AddMinutes(-5), now, CancellationToken.None);

        Assert.Equal(0, result.EligibleEventCount);
        Assert.Equal(0, result.PushedEventCount);
        Assert.True(repository.UpdatedEvents.Single().IsBlacklisted);
        Assert.Empty(pusher.Messages);
    }

    [Fact]
    public async Task ScoreAndPushRunAsync_SkipsJudgeUntilSourceThresholdUnlessReactivated()
    {
        var now = DateTimeOffset.Parse("2026-05-05T08:00:00Z");
        var runStartedAt = now.AddMinutes(-5);
        var singleSourceJudge = new FakeJudgeLlmClient();
        var singleSourceRepository = new FakeEventRepository
        {
            Inputs = [BuildInput("event-single-source", now.AddHours(-1), BuildEvidence(1))]
        };
        var singleSourceService = new EventScoringService(
            Config(sourceCount: 2),
            singleSourceRepository,
            singleSourceJudge,
            [new FakePusher()],
            EmptySourceRegistry(),
            NullLoggerFactory.Instance);

        var singleSourceResult = await singleSourceService.ScoreAndPushRunAsync("run-single-source", runStartedAt, now, CancellationToken.None);

        Assert.Equal(0, singleSourceJudge.CallCount);
        Assert.Equal(0, singleSourceResult.EligibleEventCount);

        var enoughSourceJudge = new FakeJudgeLlmClient();
        var enoughSourceRepository = new FakeEventRepository
        {
            Inputs = [BuildInput("event-enough-source", now.AddHours(-1), BuildEvidence(2))]
        };
        var enoughSourceService = new EventScoringService(
            Config(sourceCount: 2),
            enoughSourceRepository,
            enoughSourceJudge,
            [new FakePusher()],
            EmptySourceRegistry(),
            NullLoggerFactory.Instance);

        var enoughSourceResult = await enoughSourceService.ScoreAndPushRunAsync("run-enough-source", runStartedAt, now, CancellationToken.None);

        Assert.Equal(1, enoughSourceJudge.CallCount);
        Assert.Equal(1, enoughSourceResult.EligibleEventCount);
    }

    private static AppConfig Config(List<string>? blacklistKeywords = null, int sourceCount = 3)
        => new()
        {
            Analysis = new AnalysisConfig
            {
                HistoryHours = 24,
                Event = new EventAnalysisConfig
                {
                    SourceCount = sourceCount,
                    NormalizedRankThreshold = 0.7,
                    TrendWindowHours = 6,
                    MinTrendSamples = 3,
                    MinTrendHeat = 1.0
                },
                RepeatPush = new RepeatPushConfig
                {
                    SourceAddThreshold = 2,
                    RankScoreImproveThreshold = 0.15,
                    ScoreImproveThreshold = 12
                }
            },
            Filters = new FilterConfig { BlacklistKeywords = blacklistKeywords ?? [] },
            System = new SystemConfig { MaxParallelLlm = 1 }
        };

    private static ISourceRegistry EmptySourceRegistry()
        => new FakeSourceRegistry();

    private static EventScoringService CreateScoringService(
        AppConfig config,
        IEventRepository repository,
        IJudgeLlmClient judgeLlmClient,
        IEnumerable<IPusher> pushers)
        => new(config, repository, judgeLlmClient, pushers, EmptySourceRegistry(), NullLoggerFactory.Instance);

    private static RunEventScoringInput BuildInput(string eventId, DateTimeOffset firstSeenAt, IReadOnlyList<RunEventContentEvidence> evidence, string title = "重要事件")
        => new(BuildEvent(eventId, firstSeenAt, title), evidence);

    private static EventAggregate BuildEvent(string eventId, DateTimeOffset firstSeenAt, string title = "重要事件")
        => new()
        {
            Id = eventId,
            CanonicalTitle = title,
            Summary = title,
            FirstSeenAt = firstSeenAt,
            LastSeenAt = firstSeenAt,
            LastActivatedAt = firstSeenAt,
            CreatedAt = firstSeenAt,
            UpdatedAt = firstSeenAt
        };

    private static List<RunEventContentEvidence> BuildEvidence(int sourceCount)
    {
        var now = DateTimeOffset.Parse("2026-05-05T08:00:00Z");
        return Enumerable.Range(1, sourceCount)
            .Select(index => new RunEventContentEvidence(
                new ContentItem { Id = $"ci-{index}", Source = $"source-{index}", ContentKind = ContentKind.RankedNews, Title = $"重要事件 {index}", Url = $"https://example.com/{index}" },
                new ContentSnapshot { Id = $"snap-{index}", Source = $"source-{index}", ContentKind = ContentKind.RankedNews, Rank = 1, SourceListSize = 10, NormalizedRankScore = 0.9, CapturedAt = now },
                now))
            .ToList();
    }

    private static List<RunEventContentEvidence> BuildFlashEvidence(int sourceCount, double freshnessScore)
    {
        var now = DateTimeOffset.Parse("2026-05-05T08:00:00Z");
        return Enumerable.Range(1, sourceCount)
            .Select(index => new RunEventContentEvidence(
                new ContentItem { Id = $"flash-ci-{index}", Source = $"flash-source-{index}", ContentKind = ContentKind.FlashFeed, Title = $"突发快讯 {index}", Url = $"https://example.com/flash/{index}" },
                new ContentSnapshot { Id = $"flash-snap-{index}", Source = $"flash-source-{index}", ContentKind = ContentKind.FlashFeed, Rank = null, SourceListSize = null, NormalizedRankScore = null, FreshnessScore = freshnessScore, CapturedAt = now },
                now))
            .ToList();
    }

    private static List<RunEventContentEvidence> BuildTopicEvidence(int sourceCount)
    {
        var now = DateTimeOffset.Parse("2026-05-05T08:00:00Z");
        return Enumerable.Range(1, sourceCount)
            .Select(index => new RunEventContentEvidence(
                new ContentItem { Id = $"topic-ci-{index}", Source = $"topic-source-{index}", ContentKind = ContentKind.Topic, Title = $"话题 {index}", Url = $"https://example.com/topic/{index}" },
                new ContentSnapshot { Id = $"topic-snap-{index}", Source = $"topic-source-{index}", ContentKind = ContentKind.Topic, Rank = 1, SourceListSize = 10, NormalizedRankScore = 1, FreshnessScore = 1, CapturedAt = now },
                now))
            .ToList();
    }

    private static EventScoreSnapshot Snapshot(string eventId, DateTimeOffset calculatedAt, double heatValue)
        => new() { Id = $"{eventId}-{calculatedAt:O}", EventId = eventId, CalculatedAt = calculatedAt, HeatValue = heatValue };

    private sealed class FakeEventRepository : IEventRepository
    {
        public IReadOnlyList<RunEventScoringInput> Inputs { get; init; } = [];
        public IReadOnlyList<EventScoreSnapshot> RecentSnapshots { get; init; } = [];
        public bool InsertPushLogResult { get; init; } = true;
        public List<EventScoreSnapshot> ScoreSnapshots { get; } = [];
        public List<PushLog> PushLogs { get; } = [];
        public List<EventAggregate> UpdatedEvents { get; } = [];

        public Task<IReadOnlyList<ContentItem>> LoadUnmappedRunContentItemsAsync(string runId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ContentItem>>([]);
        public Task<IReadOnlyList<EventAggregate>> LoadRecallCandidatesAsync(DateTimeOffset now, int historyHours, int staleHours, int archiveRecallDays, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<EventAggregate>>([]);
        public Task MarkStaleEventsAsync(DateTimeOffset now, int staleHours, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<EventAggregate?> GetEventAsync(string eventId, CancellationToken cancellationToken) => Task.FromResult<EventAggregate?>(null);
        public Task UpsertEventAsync(EventAggregate eventAggregate, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<bool> MapEventItemIfMissingAsync(EventItem eventItem, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task<IReadOnlyList<RunEventScoringInput>> LoadRunEventScoringInputsAsync(string runId, CancellationToken cancellationToken) => Task.FromResult(Inputs);
        public Task<IReadOnlyList<EventScoreSnapshot>> LoadRecentScoreSnapshotsAsync(IReadOnlyList<string> eventIds, DateTimeOffset since, CancellationToken cancellationToken) => Task.FromResult(RecentSnapshots);
        public Task<IReadOnlyList<DigestCandidate>> LoadDigestCandidatesAsync(DateTimeOffset since, int limit, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<DigestCandidate>>([]);
        public Task InsertEventScoreSnapshotAsync(EventScoreSnapshot snapshot, CancellationToken cancellationToken) { ScoreSnapshots.Add(snapshot); return Task.CompletedTask; }
        public Task<bool> InsertPushLogIfMissingAsync(PushLog pushLog, CancellationToken cancellationToken) { PushLogs.Add(pushLog); return Task.FromResult(InsertPushLogResult); }
        public Task UpdatePushLogAsync(PushLog pushLog, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task UpdateEventsAsync(IReadOnlyList<EventAggregate> events, CancellationToken cancellationToken) { UpdatedEvents.AddRange(events); return Task.CompletedTask; }
    }

    private sealed class FakeJudgeLlmClient : IJudgeLlmClient
    {
        public bool IsConfigured => false;
        public int CallCount { get; private set; }
        public Task<JudgeResult> JudgeAsync(JudgeRequest request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(JudgeResult.Neutral("test"));
        }
    }

    private sealed class FakePusher : IPusher
    {
        public string Type => "fake";
        public bool IsConfigured => true;
        public List<PushMessage> Messages { get; } = [];
        public Task<PushResult> PushAsync(PushMessage message, CancellationToken cancellationToken)
        {
            Messages.Add(message);
            return Task.FromResult(new PushResult(true, "{}", null));
        }
    }

    private sealed class FakeSourceRegistry : ISourceRegistry
    {
        public IReadOnlyList<SourceDefinition> GetSources() => Array.Empty<SourceDefinition>();
        public IReadOnlyList<SourceDefinition> GetEnabledSources() => Array.Empty<SourceDefinition>();
        public IReadOnlyDictionary<string, IReadOnlyList<SourceDefinition>> GetEnabledSourcesByProvider() => new Dictionary<string, IReadOnlyList<SourceDefinition>>();
    }
}
