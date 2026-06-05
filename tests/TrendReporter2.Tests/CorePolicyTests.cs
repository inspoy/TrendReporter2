using TrendReporter2.Core.Configuration;
using TrendReporter2.Core.Content;
using TrendReporter2.Core.Embeddings;
using TrendReporter2.Core.Enrichment;
using TrendReporter2.Core.Events;
using TrendReporter2.Core.Sources;
using Microsoft.Extensions.Logging.Abstractions;

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

    [Fact]
    public async Task CompositeEventCandidateService_MergesRuleAndVectorCandidatesDedupesAndCaps()
    {
        var now = DateTimeOffset.Parse("2026-05-05T08:00:00Z");
        var openAiEvent = Event("event-ai", "OpenAI launches GPT-4o voice assistant", now.AddHours(-1), ["OpenAI", "GPT-4o"], ["OpenAI launches GPT-4o"]);
        var vectorOnlyEvent = Event("event-vector", "Anthropic launches Claude voice assistant", now.AddHours(-2), ["Claude"], ["Claude voice assistant"]);
        var ruleService = new EventCandidateService(new AppConfig { Analysis = new AnalysisConfig { Event = new EventAnalysisConfig { CandidateLimit = 5 } } }, new CandidateRepository([openAiEvent]));
        var embeddingRepository = new CandidateEmbeddingRepository([new VectorEventCandidate(openAiEvent, 0.95, "cosine_similarity:0.9500"), new VectorEventCandidate(vectorOnlyEvent, 0.90, "cosine_similarity:0.9000")]);
        var vectorService = new VectorEventCandidateService(EmbeddingConfig(candidateLimit: 2), embeddingRepository);
        var composite = new CompositeEventCandidateService(EmbeddingConfig(candidateLimit: 2), ruleService, vectorService, NullLoggerFactory.Instance);

        var candidates = await composite.RecallAsync(new ContentItem
        {
            Id = "ci-1",
            Title = "OpenAI starts rollout of GPT-4o voice features",
            Summary = "The company said GPT-4o voice features will reach more paid users this week."
        }, now, CancellationToken.None);

        Assert.Equal(2, candidates.Count);
        Assert.Equal("event-ai", candidates[0].Event.Id);
        Assert.Contains("vector_cosine_similarity", candidates[0].MatchedFeatures);
        Assert.Contains("char_ngram_jaccard", candidates[0].MatchedFeatures);
        Assert.Equal("event-vector", candidates[1].Event.Id);
    }

    [Fact]
    public async Task CompositeEventCandidateService_FallsBackToRuleCandidatesWhenVectorFails()
    {
        var now = DateTimeOffset.Parse("2026-05-05T08:00:00Z");
        var openAiEvent = Event("event-ai", "OpenAI launches GPT-4o voice assistant", now.AddHours(-1), ["OpenAI", "GPT-4o"], ["OpenAI launches GPT-4o"]);
        var ruleService = new EventCandidateService(new AppConfig { Analysis = new AnalysisConfig { Event = new EventAnalysisConfig { CandidateLimit = 5 } } }, new CandidateRepository([openAiEvent]));
        var vectorService = new VectorEventCandidateService(EmbeddingConfig(candidateLimit: 5), new CandidateEmbeddingRepository([], throwOnQuery: true));
        var composite = new CompositeEventCandidateService(EmbeddingConfig(candidateLimit: 5), ruleService, vectorService, NullLoggerFactory.Instance);

        var candidates = await composite.RecallAsync(new ContentItem
        {
            Id = "ci-1",
            Title = "OpenAI starts rollout of GPT-4o voice features",
            Summary = "The company said GPT-4o voice features will reach more paid users this week."
        }, now, CancellationToken.None);

        var candidate = Assert.Single(candidates);
        Assert.Equal("event-ai", candidate.Event.Id);
        Assert.DoesNotContain("vector_cosine_similarity", candidate.MatchedFeatures);
    }

    [Fact]
    public async Task EmbeddingService_UsesRemainingBudgetAcrossContentAndEventPhases()
    {
        var now = DateTimeOffset.Parse("2026-05-05T08:00:00Z");
        var client = new CountingEmbeddingClient();
        var repository = new BudgetEmbeddingRepository(
            Enumerable.Range(1, 5).Select(index => new ContentEmbeddingInput(new ContentItem { Id = $"ci-{index}", Title = $"内容 {index}" }, $"内容 {index}", $"hash-{index}")).ToList(),
            Enumerable.Range(1, 5).Select(index => new EventEmbeddingInput(Event($"event-{index}", $"事件 {index}", now, [], []), [], $"事件 {index}", $"hash-event-{index}")).ToList());
        var service = new EmbeddingService(EmbeddingConfig(candidateLimit: 5), client, repository, NullLoggerFactory.Instance);

        var contentResult = await service.GenerateContentEmbeddingsAsync("run-1", now, maxRequests: 3, CancellationToken.None);
        var remaining = Math.Max(0, 3 - contentResult.CandidateCount);
        var eventResult = await service.GenerateEventEmbeddingsAsync("run-1", now, remaining, CancellationToken.None);

        Assert.Equal(3, contentResult.CandidateCount);
        Assert.Equal(0, remaining);
        Assert.Equal(0, eventResult.CandidateCount);
        Assert.Equal(3, client.CallCount);
        Assert.Equal(3, repository.LastContentLimit);
        Assert.Equal(0, repository.EventLoadCount);
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

    private static AppConfig EmbeddingConfig(int candidateLimit)
        => new()
        {
            Analysis = new AnalysisConfig { Event = new EventAnalysisConfig { CandidateLimit = candidateLimit } },
            Llm = new LlmConfig { Embedding = new EmbeddingLlmConfig { Model = "embedding-model", Dimensions = 768, VectorCandidateLimit = candidateLimit } }
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

    private sealed class CandidateEmbeddingRepository : IEmbeddingRepository
    {
        private readonly IReadOnlyList<VectorEventCandidate> _candidates;
        private readonly bool _throwOnQuery;

        public CandidateEmbeddingRepository(IReadOnlyList<VectorEventCandidate> candidates, bool throwOnQuery = false)
        {
            _candidates = candidates;
            _throwOnQuery = throwOnQuery;
        }

        public Task<ContentEmbeddingRecord?> GetContentEmbeddingAsync(string contentItemId, string model, string version, int dimensions, CancellationToken cancellationToken)
            => Task.FromResult<ContentEmbeddingRecord?>(new ContentEmbeddingRecord(contentItemId, model, version, dimensions, "hash", Enumerable.Repeat(0.1f, dimensions).ToArray(), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

        public Task<IReadOnlyList<VectorEventCandidate>> QuerySimilarEventsAsync(float[] embedding, string model, string version, int dimensions, DateTimeOffset now, int historyHours, int archiveRecallDays, double threshold, int limit, CancellationToken cancellationToken)
        {
            if (_throwOnQuery)
            {
                throw new InvalidOperationException("vector unavailable");
            }

            return Task.FromResult<IReadOnlyList<VectorEventCandidate>>(_candidates.Take(limit).ToList());
        }

        public Task<IReadOnlyList<ContentEmbeddingInput>> LoadRunContentEmbeddingInputsAsync(string runId, string model, string version, int dimensions, int limit, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ContentEmbeddingInput>>([]);
        public Task<IReadOnlyList<EventEmbeddingInput>> LoadRunEventEmbeddingInputsAsync(string runId, string model, string version, int dimensions, int limit, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<EventEmbeddingInput>>([]);
        public Task UpsertContentEmbeddingAsync(ContentEmbeddingRecord embedding, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task UpsertEventEmbeddingAsync(EventEmbeddingRecord embedding, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class CountingEmbeddingClient : IEmbeddingClient
    {
        public int CallCount { get; private set; }

        public bool IsConfigured => true;

        public Task<EmbeddingResult> EmbedAsync(EmbeddingRequest request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(new EmbeddingResult(true, Enumerable.Repeat(0.1f, 768).ToArray(), 1, $"emb-{CallCount}", null));
        }
    }

    private sealed class BudgetEmbeddingRepository : IEmbeddingRepository
    {
        private readonly IReadOnlyList<ContentEmbeddingInput> _contentInputs;
        private readonly IReadOnlyList<EventEmbeddingInput> _eventInputs;

        public BudgetEmbeddingRepository(IReadOnlyList<ContentEmbeddingInput> contentInputs, IReadOnlyList<EventEmbeddingInput> eventInputs)
        {
            _contentInputs = contentInputs;
            _eventInputs = eventInputs;
        }

        public int LastContentLimit { get; private set; }

        public int EventLoadCount { get; private set; }

        public Task<IReadOnlyList<ContentEmbeddingInput>> LoadRunContentEmbeddingInputsAsync(string runId, string model, string version, int dimensions, int limit, CancellationToken cancellationToken)
        {
            LastContentLimit = limit;
            return Task.FromResult<IReadOnlyList<ContentEmbeddingInput>>(_contentInputs.Take(limit).ToList());
        }

        public Task<IReadOnlyList<EventEmbeddingInput>> LoadRunEventEmbeddingInputsAsync(string runId, string model, string version, int dimensions, int limit, CancellationToken cancellationToken)
        {
            EventLoadCount++;
            return Task.FromResult<IReadOnlyList<EventEmbeddingInput>>(_eventInputs.Take(limit).ToList());
        }

        public Task UpsertContentEmbeddingAsync(ContentEmbeddingRecord embedding, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task UpsertEventEmbeddingAsync(EventEmbeddingRecord embedding, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<ContentEmbeddingRecord?> GetContentEmbeddingAsync(string contentItemId, string model, string version, int dimensions, CancellationToken cancellationToken) => Task.FromResult<ContentEmbeddingRecord?>(null);
        public Task<IReadOnlyList<VectorEventCandidate>> QuerySimilarEventsAsync(float[] embedding, string model, string version, int dimensions, DateTimeOffset now, int historyHours, int archiveRecallDays, double threshold, int limit, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<VectorEventCandidate>>([]);
    }
}
