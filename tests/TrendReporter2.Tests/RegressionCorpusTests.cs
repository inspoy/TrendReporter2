using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using TrendReporter2.Core.Configuration;
using TrendReporter2.Core.Content;
using TrendReporter2.Core.Events;

namespace TrendReporter2.Tests;

public sealed class RegressionCorpusTests
{
    [Fact]
    public async Task RegressionCorpus_CoversMatchingBlacklistAndPushDedupSamples()
    {
        var cases = LoadCases();

        Assert.All(cases, item => Assert.False(string.IsNullOrWhiteSpace(item.Summary)));
        Assert.Contains(cases, item => item.Kind == "merge");
        Assert.Contains(cases, item => item.Kind == "no-merge");
        Assert.Contains(cases, item => item.Kind == "reactivation");
        Assert.Contains(cases, item => item.Kind == "blacklist");
        Assert.Contains(cases, item => item.Kind == "push-dedup");

        foreach (var regressionCase in cases.Where(item => item.Kind is "merge" or "no-merge" or "reactivation"))
        {
            var result = await RunMatcherCaseAsync(regressionCase);
            Assert.Equal(regressionCase.ExpectedCreated, result.CreatedEventCount);
            Assert.Equal(regressionCase.ExpectedMerged, result.MergedEventCount);
            Assert.Equal(regressionCase.ExpectedReactivated, result.ReactivatedEventCount);
            Assert.Equal(1, result.MappedItemCount);
        }

        var blacklistCase = cases.Single(item => item.Kind == "blacklist");
        var blacklisted = new EventAggregate { CanonicalTitle = blacklistCase.IncomingTitle, Summary = blacklistCase.IncomingTitle };
        Assert.True(EventBlacklistPolicy.Apply(blacklisted, new FilterConfig { BlacklistKeywords = [blacklistCase.BlacklistKeyword!] }));

        var pushDedupCase = cases.Single(item => item.Kind == "push-dedup");
        var repository = new PushDedupRepository(pushDedupCase.DedupKey!);
        Assert.False(await repository.InsertPushLogIfMissingAsync(new PushLog { DedupKey = pushDedupCase.DedupKey! }, CancellationToken.None));
    }

    private static async Task<EventMatchRunResult> RunMatcherCaseAsync(RegressionCase regressionCase)
    {
        var now = DateTimeOffset.Parse("2026-05-05T08:00:00Z");
        var stale = regressionCase.Stale ? now.AddHours(-30) : now.AddHours(-1);
        var candidateEvent = new EventAggregate
        {
            Id = $"event-{regressionCase.Id}",
            CanonicalTitle = regressionCase.CandidateTitle,
            Summary = regressionCase.CandidateTitle,
            Entities = ["OpenAI", "GPT", "GPT-4o"],
            Aliases = [regressionCase.CandidateTitle],
            Status = regressionCase.Stale ? EventStatus.Stale : EventStatus.Active,
            FirstSeenAt = stale.AddHours(-1),
            LastSeenAt = stale,
            LastActivatedAt = stale,
            CreatedAt = stale,
            UpdatedAt = stale
        };
        var item = new ContentItem
        {
            Id = $"ci-{regressionCase.Id}",
            Title = regressionCase.IncomingTitle,
            Summary = regressionCase.IncomingTitle,
            Source = "fixture",
            Category = "tech",
            SourceItemId = regressionCase.Id,
            Url = "https://example.com/fixture"
        };
        var repository = new MatcherRepository(item, candidateEvent);
        var candidateService = new FixtureCandidateService(candidateEvent);
        var llm = new FixtureClusterClient(new ClusterMatchResult(
            regressionCase.Decision,
            regressionCase.Decision == ClusterDecisions.RelatedButDistinct ? null : candidateEvent.Id,
            regressionCase.CandidateTitle,
            regressionCase.CandidateTitle,
            regressionCase.Confidence,
            regressionCase.Kind));
        var matcher = new EventMatcher(Config(), repository, candidateService, llm, NullLoggerFactory.Instance);

        return await matcher.MatchRunAsync("run-fixture", now, CancellationToken.None);
    }

    private static List<RegressionCase> LoadCases()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "regression-corpus.json");
        return JsonConvert.DeserializeObject<List<RegressionCase>>(File.ReadAllText(path)) ?? [];
    }

    private static AppConfig Config()
        => new()
        {
            Analysis = new AnalysisConfig
            {
                Event = new EventAnalysisConfig
                {
                    MergeThreshold = 0.82,
                    StaleMergeThreshold = 0.88,
                    StaleHours = 24
                }
            },
            System = new SystemConfig { MaxParallelLlm = 1 }
        };

    private sealed record RegressionCase(
        string Id,
        string Kind,
        string IncomingTitle,
        string? Summary,
        string CandidateTitle,
        string Decision,
        double Confidence,
        bool Stale,
        int ExpectedCreated,
        int ExpectedMerged,
        int ExpectedReactivated,
        string? BlacklistKeyword,
        string? DedupKey);

    private sealed class FixtureClusterClient : IClusterLlmClient
    {
        private readonly ClusterMatchResult _result;
        public FixtureClusterClient(ClusterMatchResult result) => _result = result;
        public bool IsConfigured => true;
        public Task<ClusterMatchResult> MatchAsync(ClusterMatchRequest request, CancellationToken cancellationToken) => Task.FromResult(_result);
    }

    private sealed class FixtureCandidateService : IEventCandidateService
    {
        private readonly EventAggregate _candidate;
        public FixtureCandidateService(EventAggregate candidate) => _candidate = candidate;
        public Task<IReadOnlyList<EventCandidate>> RecallAsync(ContentItem item, DateTimeOffset now, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<EventCandidate>>([new EventCandidate(_candidate, 0.9, ["fixture"])]);
    }

    private sealed class MatcherRepository : IEventRepository
    {
        private readonly ContentItem _item;
        private readonly EventAggregate _candidate;
        public MatcherRepository(ContentItem item, EventAggregate candidate)
        {
            _item = item;
            _candidate = candidate;
        }

        public List<EventAggregate> UpsertedEvents { get; } = [];
        public Task<IReadOnlyList<ContentItem>> LoadUnmappedRunContentItemsAsync(string runId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ContentItem>>([_item]);
        public Task<IReadOnlyList<EventAggregate>> LoadRecallCandidatesAsync(DateTimeOffset now, int historyHours, int staleHours, int archiveRecallDays, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<EventAggregate>>([_candidate]);
        public Task MarkStaleEventsAsync(DateTimeOffset now, int staleHours, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<EventAggregate?> GetEventAsync(string eventId, CancellationToken cancellationToken) => Task.FromResult<EventAggregate?>(_candidate.Id == eventId ? _candidate : null);
        public Task UpsertEventAsync(EventAggregate eventAggregate, CancellationToken cancellationToken) { UpsertedEvents.Add(eventAggregate); return Task.CompletedTask; }
        public Task<bool> MapEventItemIfMissingAsync(EventItem eventItem, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task<IReadOnlyList<RunEventScoringInput>> LoadRunEventScoringInputsAsync(string runId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<RunEventScoringInput>>([]);
        public Task<IReadOnlyList<EventScoreSnapshot>> LoadRecentScoreSnapshotsAsync(IReadOnlyList<string> eventIds, DateTimeOffset since, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<EventScoreSnapshot>>([]);
        public Task<IReadOnlyList<DigestCandidate>> LoadDigestCandidatesAsync(DateTimeOffset since, int limit, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<DigestCandidate>>([]);
        public Task InsertEventScoreSnapshotAsync(EventScoreSnapshot snapshot, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<bool> InsertPushLogIfMissingAsync(PushLog pushLog, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task UpdatePushLogAsync(PushLog pushLog, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task UpdateEventsAsync(IReadOnlyList<EventAggregate> events, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class PushDedupRepository : IEventRepository
    {
        private readonly string _existingDedupKey;
        public PushDedupRepository(string existingDedupKey) => _existingDedupKey = existingDedupKey;
        public Task<bool> InsertPushLogIfMissingAsync(PushLog pushLog, CancellationToken cancellationToken) => Task.FromResult(!string.Equals(pushLog.DedupKey, _existingDedupKey, StringComparison.Ordinal));
        public Task<IReadOnlyList<ContentItem>> LoadUnmappedRunContentItemsAsync(string runId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ContentItem>>([]);
        public Task<IReadOnlyList<EventAggregate>> LoadRecallCandidatesAsync(DateTimeOffset now, int historyHours, int staleHours, int archiveRecallDays, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<EventAggregate>>([]);
        public Task MarkStaleEventsAsync(DateTimeOffset now, int staleHours, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<EventAggregate?> GetEventAsync(string eventId, CancellationToken cancellationToken) => Task.FromResult<EventAggregate?>(null);
        public Task UpsertEventAsync(EventAggregate eventAggregate, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<bool> MapEventItemIfMissingAsync(EventItem eventItem, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task<IReadOnlyList<RunEventScoringInput>> LoadRunEventScoringInputsAsync(string runId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<RunEventScoringInput>>([]);
        public Task<IReadOnlyList<EventScoreSnapshot>> LoadRecentScoreSnapshotsAsync(IReadOnlyList<string> eventIds, DateTimeOffset since, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<EventScoreSnapshot>>([]);
        public Task<IReadOnlyList<DigestCandidate>> LoadDigestCandidatesAsync(DateTimeOffset since, int limit, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<DigestCandidate>>([]);
        public Task InsertEventScoreSnapshotAsync(EventScoreSnapshot snapshot, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task UpdatePushLogAsync(PushLog pushLog, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task UpdateEventsAsync(IReadOnlyList<EventAggregate> events, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
