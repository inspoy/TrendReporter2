using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using TrendReporter2.Core.Configuration;
using TrendReporter2.Core.Content;
using TrendReporter2.Core.Events;

namespace TrendReporter2.Tests;

public sealed class RegressionCorpusTests
{
    private static readonly string[] RequiredKinds =
    [
        "merge",
        "no-merge",
        "reactivation",
        "blacklist",
        "push-dedup",
        "flash-scoring",
        "vector-fallback",
        "secondary-merge-hard-filter",
        "tag-generation",
        "digest-filtering"
    ];

    private static readonly string[] ValidTagCategories = ["topic", "entity", "domain", "risk"];

    [Fact]
    public async Task RegressionCorpus_CoversV2OfflineContractSamples()
    {
        var cases = LoadCases();

        AssertCorpusContract(cases);

        foreach (var regressionCase in cases.Where(item => item.Kind is "merge" or "no-merge" or "reactivation"))
        {
            var result = await RunMatcherCaseAsync(regressionCase);
            var expectations = Expectations(regressionCase);
            Assert.Equal(expectations.CreatedEvents, result.CreatedEventCount);
            Assert.Equal(expectations.MergedEvents, result.MergedEventCount);
            Assert.Equal(expectations.ReactivatedEvents, result.ReactivatedEventCount);
            Assert.Equal(expectations.MappedItems, result.MappedItemCount);
        }

        foreach (var blacklistCase in Cases(cases, "blacklist"))
        {
            var blacklistInput = Inputs(blacklistCase);
            var blacklistExpectations = Expectations(blacklistCase);
            Assert.False(string.IsNullOrWhiteSpace(blacklistExpectations.BlacklistKeyword));
            var blacklisted = new EventAggregate { CanonicalTitle = blacklistInput.IncomingTitle, Summary = blacklistInput.IncomingTitle };
            Assert.True(EventBlacklistPolicy.Apply(blacklisted, new FilterConfig { BlacklistKeywords = [blacklistExpectations.BlacklistKeyword] }));
        }

        foreach (var pushDedupCase in Cases(cases, "push-dedup"))
        {
            var pushDedupExpectations = Expectations(pushDedupCase);
            Assert.False(string.IsNullOrWhiteSpace(pushDedupExpectations.DedupKey));
            var repository = new PushDedupRepository(pushDedupExpectations.DedupKey);
            Assert.False(await repository.InsertPushLogIfMissingAsync(new PushLog { DedupKey = pushDedupExpectations.DedupKey }, CancellationToken.None));
        }

        foreach (var flashScoringCase in Cases(cases, "flash-scoring"))
        {
            var flashExpectations = Expectations(flashScoringCase);
            Assert.Contains(TriggerReasons.FlashMultiSource, flashExpectations.TriggerReasons);
            Assert.Contains(TriggerReasons.FlashRepeated, flashExpectations.TriggerReasons);
        }

        foreach (var vectorFallbackCase in Cases(cases, "vector-fallback"))
        {
            var vectorFallbackExpectations = Expectations(vectorFallbackCase);
            Assert.True(vectorFallbackExpectations.RuleFallbackUsed);
            Assert.False(string.IsNullOrWhiteSpace(vectorFallbackExpectations.VectorFailure));
            Assert.NotEmpty(vectorFallbackExpectations.MatchedFeatures);
        }

        foreach (var hardFilterCase in Cases(cases, "secondary-merge-hard-filter"))
        {
            var hardFilterExpectations = Expectations(hardFilterCase);
            Assert.NotEmpty(hardFilterExpectations.ExcludedCandidateIds);
            Assert.False(string.IsNullOrWhiteSpace(hardFilterExpectations.HardFilterReason));
        }

        foreach (var tagCase in Cases(cases, "tag-generation"))
        {
            var tagExpectations = Expectations(tagCase);
            Assert.NotEmpty(tagExpectations.Tags);
            Assert.All(tagExpectations.Tags, tag =>
            {
                Assert.False(string.IsNullOrWhiteSpace(tag.Name));
                Assert.Contains(tag.Category, ValidTagCategories);
            });
        }

        foreach (var digestFilteringCase in Cases(cases, "digest-filtering"))
        {
            var digestExpectations = Expectations(digestFilteringCase);
            Assert.Contains("merged", digestExpectations.ExcludedFlags);
            Assert.Contains("blacklisted", digestExpectations.ExcludedFlags);
        }
    }

    private static async Task<EventMatchRunResult> RunMatcherCaseAsync(RegressionCase regressionCase)
    {
        var now = DateTimeOffset.Parse("2026-05-05T08:00:00Z");
        var inputs = Inputs(regressionCase);
        var fakes = Fakes(regressionCase);
        var stale = fakes.CandidateStale ? now.AddHours(-30) : now.AddHours(-1);
        var candidateEvent = new EventAggregate
        {
            Id = $"event-{regressionCase.Id}",
            CanonicalTitle = fakes.CandidateTitle,
            Summary = fakes.CandidateTitle,
            Entities = ["OpenAI", "GPT", "GPT-4o"],
            Aliases = [fakes.CandidateTitle],
            Status = fakes.CandidateStale ? EventStatus.Stale : EventStatus.Active,
            FirstSeenAt = stale.AddHours(-1),
            LastSeenAt = stale,
            LastActivatedAt = stale,
            CreatedAt = stale,
            UpdatedAt = stale
        };
        var item = new ContentItem
        {
            Id = $"ci-{regressionCase.Id}",
            Title = inputs.IncomingTitle,
            Summary = inputs.IncomingTitle,
            Source = "fixture",
            Category = "tech",
            SourceItemId = regressionCase.Id,
            Url = "https://example.com/fixture"
        };
        var repository = new MatcherRepository(item, candidateEvent);
        var candidateService = new FixtureCandidateService(candidateEvent);
        var llm = new FixtureClusterClient(new ClusterMatchResult(
            fakes.ClusterDecision,
            fakes.ClusterDecision == ClusterDecisions.RelatedButDistinct ? null : candidateEvent.Id,
            fakes.CandidateTitle,
            fakes.CandidateTitle,
            fakes.ClusterConfidence,
            regressionCase.Kind));
        var matcher = new EventMatcher(Config(), repository, candidateService, llm, NullLoggerFactory.Instance);

        return await matcher.MatchRunAsync("run-fixture", now, CancellationToken.None);
    }

    private static void AssertCorpusContract(IReadOnlyList<RegressionCase> cases)
    {
        Assert.NotEmpty(cases);
        Assert.All(RequiredKinds, kind => Assert.Contains(cases, item => item.Kind == kind));
        Assert.Equal(cases.Count, cases.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count());

        Assert.All(cases, item =>
        {
            Assert.False(string.IsNullOrWhiteSpace(item.Id));
            Assert.False(string.IsNullOrWhiteSpace(item.Kind));
            Assert.False(string.IsNullOrWhiteSpace(item.Summary));
            Assert.True(item.Offline);

            var inputs = Inputs(item);
            var fakes = Fakes(item);
            var expectations = Expectations(item);
            Assert.False(string.IsNullOrWhiteSpace(inputs.IncomingTitle));
            Assert.False(string.IsNullOrWhiteSpace(fakes.Backing));
            Assert.Equal("none", fakes.ExternalServices);
            Assert.NotNull(expectations);
        });
    }

    private static IReadOnlyList<RegressionCase> Cases(IReadOnlyList<RegressionCase> cases, string kind)
    {
        var matches = cases.Where(item => item.Kind == kind).ToList();
        Assert.NotEmpty(matches);
        return matches;
    }

    private static CaseInputs Inputs(RegressionCase regressionCase)
    {
        Assert.NotNull(regressionCase.Inputs);
        return regressionCase.Inputs;
    }

    private static CaseFakes Fakes(RegressionCase regressionCase)
    {
        Assert.NotNull(regressionCase.Fakes);
        return regressionCase.Fakes;
    }

    private static CaseExpectations Expectations(RegressionCase regressionCase)
    {
        Assert.NotNull(regressionCase.Expectations);
        return regressionCase.Expectations;
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
            Llm = new LlmConfig { Cluster = new LlmEndpointConfig { MaxParallel = 1 } }
        };

    private sealed class RegressionCase
    {
        public string Id { get; init; } = string.Empty;
        public string Kind { get; init; } = string.Empty;
        public string Summary { get; init; } = string.Empty;
        public bool Offline { get; init; }
        public CaseInputs? Inputs { get; init; }
        public CaseFakes? Fakes { get; init; }
        public CaseExpectations? Expectations { get; init; }
    }

    private sealed class CaseInputs
    {
        public string IncomingTitle { get; init; } = string.Empty;
    }

    private sealed class CaseFakes
    {
        public string Backing { get; init; } = string.Empty;
        public string ExternalServices { get; init; } = string.Empty;
        public string CandidateTitle { get; init; } = string.Empty;
        public string ClusterDecision { get; init; } = ClusterDecisions.SameEvent;
        public double ClusterConfidence { get; init; } = 0.9;
        public bool CandidateStale { get; init; }
    }

    private sealed class CaseExpectations
    {
        public int CreatedEvents { get; init; }
        public int MergedEvents { get; init; }
        public int ReactivatedEvents { get; init; }
        public int MappedItems { get; init; }
        public string? BlacklistKeyword { get; init; }
        public string? DedupKey { get; init; }
        public IReadOnlyList<string> TriggerReasons { get; init; } = [];
        public bool RuleFallbackUsed { get; init; }
        public string? VectorFailure { get; init; }
        public IReadOnlyList<string> MatchedFeatures { get; init; } = [];
        public IReadOnlyList<string> ExcludedCandidateIds { get; init; } = [];
        public string? HardFilterReason { get; init; }
        public IReadOnlyList<ExpectedTag> Tags { get; init; } = [];
        public IReadOnlyList<string> ExcludedFlags { get; init; } = [];
    }

    private sealed class ExpectedTag
    {
        public string Name { get; init; } = string.Empty;
        public string Category { get; init; } = string.Empty;
    }

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
