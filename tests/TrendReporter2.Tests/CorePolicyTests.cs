using TrendReporter2.Core.Configuration;
using TrendReporter2.Core.Content;
using TrendReporter2.Core.Enrichment;
using TrendReporter2.Core.Events;
using TrendReporter2.Core.Sources;

namespace TrendReporter2.Tests;

public sealed class CorePolicyTests
{
    [Fact]
    public void EnrichmentPolicy_UsesSourceSummaryAndTitleSignals()
    {
        var policy = new EnrichmentPolicy(new AppConfig
        {
            Enrichment = new EnrichmentConfig
            {
                EnabledSources = ["forced"],
                DisabledSources = ["disabled", "forced-disabled"],
                MinTitleLength = 10
            }
        });

        Assert.True(policy.NeedEnrichment(new FetchedContentItem { SourceId = "forced", Title = "完整标题", SummaryText = CompleteSummary() }));
        Assert.False(policy.NeedEnrichment(new FetchedContentItem { SourceId = "disabled", Title = "突发", SummaryText = null }));
        Assert.False(policy.NeedEnrichment(new FetchedContentItem { SourceId = "forced-disabled", Title = "突发", SummaryText = null }));
        Assert.False(policy.NeedEnrichment(new FetchedContentItem { SourceId = "other", Title = "OpenAI 发布新的模型能力", SummaryText = CompleteSummary() }));
        Assert.True(policy.NeedEnrichment(new FetchedContentItem { SourceId = "other", Title = "突发", SummaryText = null }));
        Assert.False(policy.NeedEnrichment(new ContentItem { Source = "disabled", Title = "突发", Summary = null }));
        Assert.False(policy.NeedEnrichment(new ContentItem { Source = "other", Title = "OpenAI 发布新的模型能力", Summary = null }));
    }

    [Fact]
    public void EventBlacklistPolicy_MarksCaseInsensitiveKeywordHitsOnly()
    {
        var filters = new FilterConfig { BlacklistKeywords = ["lottery", "广告"] };
        var hit = new EventAggregate { CanonicalTitle = "Lottery results", Summary = "daily draw" };
        var miss = new EventAggregate { CanonicalTitle = "央行发布政策", Summary = "市场关注" };

        Assert.True(EventBlacklistPolicy.Apply(hit, filters));
        Assert.True(hit.IsBlacklisted);
        Assert.Contains("lottery", hit.BlacklistReason, StringComparison.OrdinalIgnoreCase);

        Assert.False(EventBlacklistPolicy.Apply(miss, filters));
        Assert.False(miss.IsBlacklisted);
        Assert.Null(miss.BlacklistReason);
    }

    [Fact]
    public async Task EventCandidateService_RanksSimilarCandidatesAndRespectsLimit()
    {
        var now = DateTimeOffset.Parse("2026-05-05T08:00:00Z");
        var repository = new CandidateRepository([
            Event("event-ai", "OpenAI launches GPT-4o voice assistant", now.AddHours(-1), ["OpenAI", "GPT-4o"], ["OpenAI launches GPT-4o"]),
            Event("event-eu", "European Union approves AI Act implementation rules", now.AddHours(-2), ["EU", "AI Act"], ["EU AI Act implementation"]),
            Event("event-weather", "Heavy rainfall causes railway disruption", now.AddHours(-1), ["railway", "rain"], ["railway disruption"])
        ]);
        var service = new EventCandidateService(new AppConfig
        {
            Analysis = new AnalysisConfig { Event = new EventAnalysisConfig { CandidateLimit = 2 } }
        }, repository);

        var candidates = await service.RecallAsync(new ContentItem
        {
            Title = "OpenAI starts rollout of GPT-4o voice features",
            Summary = "The company said GPT-4o voice features will reach more paid users this week."
        }, now, CancellationToken.None);

        Assert.Equal(2, candidates.Count);
        Assert.Equal("event-ai", candidates[0].Event.Id);
        Assert.Contains("char_ngram_jaccard", candidates[0].MatchedFeatures);
    }

    private static string CompleteSummary()
        => "OpenAI 发布新的模型能力，多个来源报道该功能已经开始灰度，行业正在评估影响。";

    private static EventAggregate Event(string id, string title, DateTimeOffset lastSeenAt, List<string> keyTerms, List<string> representativeTitles)
        => new()
        {
            Id = id,
            CanonicalTitle = title,
            Summary = title,
            KeyTerms = keyTerms,
            RepresentativeTitles = representativeTitles,
            Status = EventStatus.Active,
            LastSeenAt = lastSeenAt
        };

    private sealed class CandidateRepository : IEventRepository
    {
        private readonly IReadOnlyList<EventAggregate> _events;

        public CandidateRepository(IReadOnlyList<EventAggregate> events) => _events = events;

        public Task<IReadOnlyList<EventAggregate>> LoadRecallCandidatesAsync(DateTimeOffset now, int historyHours, int staleHours, int archiveRecallDays, CancellationToken cancellationToken) => Task.FromResult(_events);
        public Task<IReadOnlyList<ContentItem>> LoadUnmappedRunContentItemsAsync(string runId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ContentItem>>([]);
        public Task MarkStaleEventsAsync(DateTimeOffset now, int staleHours, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<EventAggregate?> GetEventAsync(string eventId, CancellationToken cancellationToken) => Task.FromResult<EventAggregate?>(null);
        public Task UpsertEventAsync(EventAggregate eventAggregate, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<bool> MapEventItemIfMissingAsync(EventItem eventItem, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task<IReadOnlyList<RunEventScoringInput>> LoadRunEventScoringInputsAsync(string runId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<RunEventScoringInput>>([]);
        public Task<IReadOnlyList<EventScoreSnapshot>> LoadRecentScoreSnapshotsAsync(IReadOnlyList<string> eventIds, DateTimeOffset since, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<EventScoreSnapshot>>([]);
        public Task<IReadOnlyList<DigestCandidate>> LoadDigestCandidatesAsync(DateTimeOffset since, int limit, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<DigestCandidate>>([]);
        public Task InsertEventScoreSnapshotAsync(EventScoreSnapshot snapshot, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<bool> InsertPushLogIfMissingAsync(PushLog pushLog, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task UpdatePushLogAsync(PushLog pushLog, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task UpdateEventsAsync(IReadOnlyList<EventAggregate> events, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
