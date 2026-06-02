using Microsoft.Extensions.Logging.Abstractions;
using TrendReporter2.Core.Configuration;
using TrendReporter2.Core.Events;

namespace TrendReporter2.Tests;

public sealed class SecondaryMergeServiceTests
{
    [Fact]
    public void HardFilters_ExcludesNonIntersectingEntities()
    {
        var candidate = Candidate(
            sourceEntities: ["OpenAI"],
            targetEntities: ["Baidu", "DeepSeek"]);

        Assert.True(SecondaryMergeHardFilters.ShouldExclude(candidate, out var reason));
        Assert.Equal("实体不相交", reason);
    }

    [Fact]
    public void HardFilters_PassesIntersectingEntities()
    {
        var candidate = Candidate(
            sourceEntities: ["OpenAI", "GPT-4o"],
            targetEntities: ["OpenAI", "Claude"]);

        Assert.False(SecondaryMergeHardFilters.ShouldExclude(candidate, out _));
    }

    [Fact]
    public void HardFilters_PassesWhenOneSideHasNoEntities()
    {
        var candidate = Candidate(
            sourceEntities: ["OpenAI"],
            targetEntities: []);

        Assert.False(SecondaryMergeHardFilters.ShouldExclude(candidate, out _));
    }

    [Fact]
    public void HardFilters_ExcludesNonOverlappingTimeWindowsWithDifferentType()
    {
        var source = Event("ev-src", "事件 A", EventType.NewsEvent);
        source.FirstSeenAt = DateTimeOffset.Parse("2026-06-01T00:00:00Z");
        source.LastSeenAt = DateTimeOffset.Parse("2026-06-01T12:00:00Z");
        var target = Event("ev-tgt", "事件 B", EventType.Topic);
        target.FirstSeenAt = DateTimeOffset.Parse("2026-06-05T00:00:00Z");
        target.LastSeenAt = DateTimeOffset.Parse("2026-06-05T12:00:00Z");
        var candidate = Candidate(source, target);

        Assert.True(SecondaryMergeHardFilters.ShouldExclude(candidate, out var reason));
        Assert.Equal("时间窗口不重叠且事件类型不同", reason);
    }

    [Fact]
    public void HardFilters_PassesNonOverlappingTimeWithSameType()
    {
        var source = Event("ev-src", "事件 A", EventType.NewsEvent);
        source.FirstSeenAt = DateTimeOffset.Parse("2026-06-01T00:00:00Z");
        source.LastSeenAt = DateTimeOffset.Parse("2026-06-01T12:00:00Z");
        var target = Event("ev-tgt", "事件 B", EventType.NewsEvent);
        target.FirstSeenAt = DateTimeOffset.Parse("2026-06-05T00:00:00Z");
        target.LastSeenAt = DateTimeOffset.Parse("2026-06-05T12:00:00Z");
        var candidate = Candidate(source, target);

        Assert.False(SecondaryMergeHardFilters.ShouldExclude(candidate, out _));
    }

    [Fact]
    public void HardFilters_ExcludesConflictingPlaces()
    {
        var candidate = Candidate(
            sourcePlaces: ["北京"],
            targetPlaces: ["上海", "广州"]);

        Assert.True(SecondaryMergeHardFilters.ShouldExclude(candidate, out var reason));
        Assert.Equal("地点冲突", reason);
    }

    [Fact]
    public void HardFilters_PassesIntersectingPlaces()
    {
        var candidate = Candidate(
            sourcePlaces: ["北京", "上海"],
            targetPlaces: ["上海", "广州"]);

        Assert.False(SecondaryMergeHardFilters.ShouldExclude(candidate, out _));
    }

    [Fact]
    public void HardFilters_PassesWhenOneSideHasNoPlaces()
    {
        var candidate = Candidate(
            sourcePlaces: ["北京"],
            targetPlaces: []);

        Assert.False(SecondaryMergeHardFilters.ShouldExclude(candidate, out _));
    }

    [Fact]
    public void HardFilters_ExcludesNewsEventAndTopicMismatch()
    {
        var source = Event("ev-src", "新闻事件", EventType.NewsEvent);
        var target = Event("ev-tgt", "话题", EventType.Topic);
        var candidate = Candidate(source, target);

        Assert.True(SecondaryMergeHardFilters.ShouldExclude(candidate, out var reason));
        Assert.Equal("NewsEvent 与 Topic 类型不兼容", reason);
    }

    [Fact]
    public void HardFilters_PassesSameEventType()
    {
        var candidate = Candidate(
            sourceEntities: ["OpenAI"],
            targetEntities: ["OpenAI"]);

        Assert.False(SecondaryMergeHardFilters.ShouldExclude(candidate, out _));
    }

    [Fact]
    public void HardFilters_PassesAllChecksForSimilarEvents()
    {
        var candidate = Candidate(
            sourceEntities: ["OpenAI", "GPT-4o"],
            targetEntities: ["OpenAI", "GPT-4o", "Claude"],
            sourcePlaces: ["San Francisco"],
            targetPlaces: ["San Francisco", "New York"]);

        Assert.False(SecondaryMergeHardFilters.ShouldExclude(candidate, out var reason));
        Assert.Empty(reason);
    }

    [Fact]
    public void HasIntersection_ReturnsTrueForOverlap()
    {
        Assert.True(SecondaryMergeHardFilters.HasIntersection(["a", "b", "c"], ["c", "d"]));
        Assert.True(SecondaryMergeHardFilters.HasIntersection(["OpenAI"], ["openai"]));
        Assert.True(SecondaryMergeHardFilters.HasIntersection(["北京"], ["北京", "上海"]));
    }

    [Fact]
    public void HasIntersection_ReturnsFalseForDisjoint()
    {
        Assert.False(SecondaryMergeHardFilters.HasIntersection(["a"], ["b", "c"]));
    }

    [Fact]
    public void HasIntersection_ReturnsFalseWhenOneIsEmpty()
    {
        Assert.False(SecondaryMergeHardFilters.HasIntersection([], ["a"]));
        Assert.False(SecondaryMergeHardFilters.HasIntersection(["a"], []));
    }

    [Fact]
    public void IsNewsTopicMismatch_ReturnsTrueForCrossTypePairs()
    {
        Assert.True(SecondaryMergeHardFilters.IsNewsTopicMismatch(EventType.NewsEvent, EventType.Topic));
        Assert.True(SecondaryMergeHardFilters.IsNewsTopicMismatch(EventType.Topic, EventType.NewsEvent));
    }

    [Fact]
    public void IsNewsTopicMismatch_ReturnsFalseForSameTypePairs()
    {
        Assert.False(SecondaryMergeHardFilters.IsNewsTopicMismatch(EventType.NewsEvent, EventType.NewsEvent));
        Assert.False(SecondaryMergeHardFilters.IsNewsTopicMismatch(EventType.Topic, EventType.Topic));
    }

    [Fact]
    public void TimeWindowsOverlap_ReturnsTrueForOverlapping()
    {
        var left = Event("left", "left", EventType.NewsEvent);
        left.FirstSeenAt = DateTimeOffset.Parse("2026-06-01T00:00:00Z");
        left.LastSeenAt = DateTimeOffset.Parse("2026-06-03T00:00:00Z");
        var right = Event("right", "right", EventType.NewsEvent);
        right.FirstSeenAt = DateTimeOffset.Parse("2026-06-02T00:00:00Z");
        right.LastSeenAt = DateTimeOffset.Parse("2026-06-05T00:00:00Z");

        Assert.True(SecondaryMergeHardFilters.TimeWindowsOverlap(left, right));
        Assert.True(SecondaryMergeHardFilters.TimeWindowsOverlap(right, left));
    }

    [Fact]
    public void TimeWindowsOverlap_ReturnsTrueForAdjacent()
    {
        var left = Event("left", "left", EventType.NewsEvent);
        left.FirstSeenAt = DateTimeOffset.Parse("2026-06-01T00:00:00Z");
        left.LastSeenAt = DateTimeOffset.Parse("2026-06-02T00:00:00Z");
        var right = Event("right", "right", EventType.NewsEvent);
        right.FirstSeenAt = DateTimeOffset.Parse("2026-06-02T00:00:00Z");
        right.LastSeenAt = DateTimeOffset.Parse("2026-06-03T00:00:00Z");

        Assert.True(SecondaryMergeHardFilters.TimeWindowsOverlap(left, right));
    }

    [Fact]
    public void TimeWindowsOverlap_ReturnsFalseForNonOverlapping()
    {
        var left = Event("left", "left", EventType.NewsEvent);
        left.FirstSeenAt = DateTimeOffset.Parse("2026-06-01T00:00:00Z");
        left.LastSeenAt = DateTimeOffset.Parse("2026-06-02T00:00:00Z");
        var right = Event("right", "right", EventType.NewsEvent);
        right.FirstSeenAt = DateTimeOffset.Parse("2026-06-03T00:00:00Z");
        right.LastSeenAt = DateTimeOffset.Parse("2026-06-05T00:00:00Z");

        Assert.False(SecondaryMergeHardFilters.TimeWindowsOverlap(left, right));
    }

    [Fact]
    public void EventMergeDecision_ShouldMergeReturnsTrueForSameEvent()
    {
        var decision = EventMergeDecision.SameEvent(0.9, "同一事件");
        Assert.True(decision.ShouldMerge);
        Assert.Equal("same_event", decision.Decision);
    }

    [Fact]
    public void EventMergeDecision_ShouldMergeReturnsFalseForRelatedButDistinct()
    {
        var decision = EventMergeDecision.RelatedButDistinct(0.5, "相关但不同");
        Assert.False(decision.ShouldMerge);
    }

    [Fact]
    public void EventMergeDecision_ShouldMergeReturnsFalseForUnrelated()
    {
        var decision = EventMergeDecision.Unrelated(0.1, "无关");
        Assert.False(decision.ShouldMerge);
    }

    [Fact]
    public void SecondaryMergeRunResult_CorrectlyRecordsStats()
    {
        var result = new SecondaryMergeRunResult(10, 4, 6, 3);
        Assert.Equal(10, result.CandidatePairCount);
        Assert.Equal(4, result.HardFilterExcludedCount);
        Assert.Equal(6, result.LlmDecidedCount);
        Assert.Equal(3, result.MergedCount);
    }

    private static EventMergeCandidate Candidate(
        List<string>? sourceEntities = null,
        List<string>? targetEntities = null,
        List<string>? sourcePlaces = null,
        List<string>? targetPlaces = null)
    {
        var source = Event("ev-src", "来源事件", EventType.NewsEvent);
        source.Entities = sourceEntities ?? ["OpenAI"];
        source.Places = sourcePlaces ?? [];
        source.FirstSeenAt = DateTimeOffset.Parse("2026-06-01T00:00:00Z");
        source.LastSeenAt = DateTimeOffset.Parse("2026-06-02T00:00:00Z");

        var target = Event("ev-tgt", "目标事件", EventType.NewsEvent);
        target.Entities = targetEntities ?? ["OpenAI"];
        target.Places = targetPlaces ?? [];
        target.FirstSeenAt = DateTimeOffset.Parse("2026-06-01T12:00:00Z");
        target.LastSeenAt = DateTimeOffset.Parse("2026-06-02T12:00:00Z");

        return new EventMergeCandidate(source, target, 0.85, ["cosine_similarity"]);
    }

    private static EventMergeCandidate Candidate(EventAggregate source, EventAggregate target)
        => new(source, target, 0.85, ["cosine_similarity"]);

    private static EventAggregate Event(string id, string title, string type)
        => new()
        {
            Id = id,
            CanonicalTitle = title,
            Summary = title,
            Type = type,
            Entities = [],
            Places = [],
            KeyTerms = [],
            RepresentativeTitles = [title],
            Aliases = [],
            Status = EventStatus.Active,
            FirstSeenAt = DateTimeOffset.Parse("2026-06-01T00:00:00Z"),
            LastSeenAt = DateTimeOffset.Parse("2026-06-01T12:00:00Z"),
            LastActivatedAt = DateTimeOffset.Parse("2026-06-01T00:00:00Z"),
            CreatedAt = DateTimeOffset.Parse("2026-06-01T00:00:00Z"),
            UpdatedAt = DateTimeOffset.Parse("2026-06-01T00:00:00Z")
        };
}
