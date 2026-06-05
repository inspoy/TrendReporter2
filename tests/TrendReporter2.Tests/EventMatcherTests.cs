using Microsoft.Extensions.Logging.Abstractions;
using TrendReporter2.Core.Configuration;
using TrendReporter2.Core.Content;
using TrendReporter2.Core.Events;

namespace TrendReporter2.Tests;

public sealed class EventMatcherTests
{
    [Fact]
    public async Task MatchRunAsync_ReusesPrecomputedClusterDecisionWhenRevalidationCandidatesAreUnchanged()
    {
        var now = DateTimeOffset.Parse("2026-05-05T08:00:00Z");
        var existingEvent = BuildEvent("event-existing", "Existing market event", now.AddHours(-2));
        var repository = new MatcherRepository([BuildItem("ci-first"), BuildItem("ci-second")], [existingEvent]);
        var candidateService = new SequenceCandidateService(new Dictionary<string, Queue<IReadOnlyList<EventCandidate>>>
        {
            ["ci-first"] = new Queue<IReadOnlyList<EventCandidate>>([[], []]),
            ["ci-second"] = new Queue<IReadOnlyList<EventCandidate>>([
                [new EventCandidate(existingEvent, 0.3, ["fixture"])],
                [new EventCandidate(existingEvent, 0.3, ["fixture"])]
            ])
        });
        var llm = new CountingClusterClient([
            ClusterMatchResult.CreateNew("precomputed distinct event")
        ]);
        var matcher = new EventMatcher(Config(), repository, candidateService, llm, NullLoggerFactory.Instance);

        var result = await matcher.MatchRunAsync("run-1", now, CancellationToken.None);

        Assert.Equal(1, llm.CallCount);
        Assert.Equal(2, result.CreatedEventCount);
        Assert.Equal(2, result.MappedItemCount);
    }

    [Fact]
    public async Task MatchRunAsync_CallsClusterAgainWhenRevalidationCandidatesChange()
    {
        var now = DateTimeOffset.Parse("2026-05-05T08:00:00Z");
        var existingEvent = BuildEvent("event-existing", "Existing market event", now.AddHours(-2));
        var newCandidate = BuildEvent("event-new-candidate", "Newly committed event", now.AddMinutes(-1));
        var repository = new MatcherRepository([BuildItem("ci-first"), BuildItem("ci-second")], [existingEvent, newCandidate]);
        var candidateService = new SequenceCandidateService(new Dictionary<string, Queue<IReadOnlyList<EventCandidate>>>
        {
            ["ci-first"] = new Queue<IReadOnlyList<EventCandidate>>([[], []]),
            ["ci-second"] = new Queue<IReadOnlyList<EventCandidate>>([
                [new EventCandidate(existingEvent, 0.3, ["fixture"])],
                [new EventCandidate(existingEvent, 0.3, ["fixture"]), new EventCandidate(newCandidate, 0.2, ["new_commit"])]
            ])
        });
        var llm = new CountingClusterClient([
            ClusterMatchResult.CreateNew("precomputed distinct event"),
            ClusterMatchResult.CreateNew("changed candidates distinct event")
        ]);
        var matcher = new EventMatcher(Config(), repository, candidateService, llm, NullLoggerFactory.Instance);

        var result = await matcher.MatchRunAsync("run-1", now, CancellationToken.None);

        Assert.Equal(2, llm.CallCount);
        Assert.Equal(2, result.CreatedEventCount);
        Assert.Equal(2, result.MappedItemCount);
    }

    private static AppConfig Config()
        => new()
        {
            Analysis = new AnalysisConfig
            {
                Event = new EventAnalysisConfig
                {
                    MergeThreshold = 0.82,
                    RuleMergeThreshold = 0.95,
                    StaleMergeThreshold = 0.88,
                    StaleHours = 24
                }
            },
            System = new SystemConfig { MaxParallelLlm = 1 }
        };

    private static ContentItem BuildItem(string id)
        => new()
        {
            Id = id,
            Title = $"Incoming title {id}",
            Summary = $"Incoming summary {id}",
            Source = "fixture",
            SourceItemId = id,
            Url = $"https://example.com/{id}"
        };

    private static EventAggregate BuildEvent(string id, string title, DateTimeOffset seenAt)
        => new()
        {
            Id = id,
            CanonicalTitle = title,
            Summary = title,
            Status = EventStatus.Active,
            FirstSeenAt = seenAt,
            LastSeenAt = seenAt,
            LastActivatedAt = seenAt,
            CreatedAt = seenAt,
            UpdatedAt = seenAt
        };

    private sealed class MatcherRepository : IEventRepository
    {
        private readonly IReadOnlyList<ContentItem> _items;
        private readonly Dictionary<string, EventAggregate> _events;

        public MatcherRepository(IReadOnlyList<ContentItem> items, IReadOnlyList<EventAggregate> events)
        {
            _items = items;
            _events = events.ToDictionary(eventAggregate => eventAggregate.Id, StringComparer.Ordinal);
        }

        public List<EventItem> MappedItems { get; } = [];
        public Task<IReadOnlyList<ContentItem>> LoadUnmappedRunContentItemsAsync(string runId, CancellationToken cancellationToken) => Task.FromResult(_items);
        public Task<IReadOnlyList<EventAggregate>> LoadRecallCandidatesAsync(DateTimeOffset now, int historyHours, int staleHours, int archiveRecallDays, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<EventAggregate>>(_events.Values.ToList());
        public Task MarkStaleEventsAsync(DateTimeOffset now, int staleHours, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<EventAggregate?> GetEventAsync(string eventId, CancellationToken cancellationToken) => Task.FromResult(_events.GetValueOrDefault(eventId));
        public Task UpsertEventAsync(EventAggregate eventAggregate, CancellationToken cancellationToken) { _events[eventAggregate.Id] = eventAggregate; return Task.CompletedTask; }
        public Task<bool> MapEventItemIfMissingAsync(EventItem eventItem, CancellationToken cancellationToken) { MappedItems.Add(eventItem); return Task.FromResult(true); }
        public Task<IReadOnlyList<RunEventScoringInput>> LoadRunEventScoringInputsAsync(string runId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<RunEventScoringInput>>([]);
        public Task<IReadOnlyList<EventScoreSnapshot>> LoadRecentScoreSnapshotsAsync(IReadOnlyList<string> eventIds, DateTimeOffset since, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<EventScoreSnapshot>>([]);
        public Task<IReadOnlyList<DigestCandidate>> LoadDigestCandidatesAsync(DateTimeOffset since, int limit, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<DigestCandidate>>([]);
        public Task InsertEventScoreSnapshotAsync(EventScoreSnapshot snapshot, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<bool> InsertPushLogIfMissingAsync(PushLog pushLog, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task UpdatePushLogAsync(PushLog pushLog, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task UpdateEventsAsync(IReadOnlyList<EventAggregate> events, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class SequenceCandidateService : IEventCandidateService
    {
        private readonly Dictionary<string, Queue<IReadOnlyList<EventCandidate>>> _candidatesByItem;

        public SequenceCandidateService(Dictionary<string, Queue<IReadOnlyList<EventCandidate>>> candidatesByItem)
        {
            _candidatesByItem = candidatesByItem;
        }

        public Task<IReadOnlyList<EventCandidate>> RecallAsync(ContentItem item, DateTimeOffset now, CancellationToken cancellationToken)
        {
            var candidates = _candidatesByItem.TryGetValue(item.Id, out var sequence) && sequence.Count > 0
                ? sequence.Dequeue()
                : [];
            return Task.FromResult(candidates);
        }
    }

    private sealed class CountingClusterClient : IClusterLlmClient
    {
        private readonly Queue<ClusterMatchResult> _results;

        public CountingClusterClient(IReadOnlyList<ClusterMatchResult> results)
        {
            _results = new Queue<ClusterMatchResult>(results);
        }

        public bool IsConfigured => true;
        public int CallCount { get; private set; }

        public Task<ClusterMatchResult> MatchAsync(ClusterMatchRequest request, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(_results.Count > 0 ? _results.Dequeue() : ClusterMatchResult.CreateNew("default"));
        }
    }
}
