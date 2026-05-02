using TrendReporter2.Core.Content;
using TrendReporter2.Core.Events;
using TrendReporter2.Core.Persistence;

namespace TrendReporter2.Infrastructure.Persistence;

public sealed class LiteDbEventRepository : IEventRepository
{
    private readonly LiteDbConnectionFactory _connectionFactory;

    public LiteDbEventRepository(LiteDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public Task<IReadOnlyList<ContentItem>> LoadUnmappedRunContentItemsAsync(string runId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var database = _connectionFactory.Open();
        var contentItems = database.GetCollection<ContentItem>(TrendCollectionNames.ContentItem);
        var eventItems = database.GetCollection<EventItem>(TrendCollectionNames.EventItem);
        var mappedIds = eventItems.FindAll().Select(item => item.ContentItemId).ToHashSet(StringComparer.Ordinal);
        var result = contentItems
            .Find(item => item.LastSeenRunId == runId)
            .Where(item => !mappedIds.Contains(item.Id))
            .OrderBy(item => item.LastSeenRank)
            .ThenBy(item => item.Source, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return Task.FromResult<IReadOnlyList<ContentItem>>(result);
    }

    public Task<IReadOnlyList<EventAggregate>> LoadRecallCandidatesAsync(
        DateTimeOffset now,
        int historyHours,
        int staleHours,
        int archiveRecallDays,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var activeCutoff = now.AddHours(-Math.Max(1, historyHours));
        var staleCutoff = now.AddDays(-Math.Max(1, archiveRecallDays));
        using var database = _connectionFactory.Open();
        var events = database.GetCollection<EventAggregate>(TrendCollectionNames.Event);
        var staleCutoffByHours = now.AddHours(-Math.Max(1, staleHours));
        foreach (var activeEvent in events.Find(eventAggregate => eventAggregate.Status == EventStatus.Active && eventAggregate.LastSeenAt < staleCutoffByHours))
        {
            activeEvent.Status = EventStatus.Stale;
            activeEvent.UpdatedAt = now;
            events.Update(activeEvent);
        }

        var result = events
            .Find(eventAggregate =>
                (eventAggregate.Status == EventStatus.Active && eventAggregate.LastSeenAt >= activeCutoff) ||
                (eventAggregate.Status == EventStatus.Stale && eventAggregate.LastSeenAt >= staleCutoff))
            .ToList();

        return Task.FromResult<IReadOnlyList<EventAggregate>>(result);
    }

    public Task<EventAggregate?> GetEventAsync(string eventId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var database = _connectionFactory.Open();
        EventAggregate? result = database.GetCollection<EventAggregate>(TrendCollectionNames.Event).FindById(eventId);
        return Task.FromResult<EventAggregate?>(result);
    }

    public Task UpsertEventAsync(EventAggregate eventAggregate, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var database = _connectionFactory.Open();
        var events = database.GetCollection<EventAggregate>(TrendCollectionNames.Event);
        events.Upsert(eventAggregate);
        return Task.CompletedTask;
    }

    public Task<bool> MapEventItemIfMissingAsync(EventItem eventItem, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        eventItem.DedupKey = BuildDedupKey(eventItem.EventId, eventItem.ContentItemId);
        using var database = _connectionFactory.Open();
        var eventItems = database.GetCollection<EventItem>(TrendCollectionNames.EventItem);
        if (eventItems.Exists(item => item.DedupKey == eventItem.DedupKey || item.ContentItemId == eventItem.ContentItemId))
        {
            return Task.FromResult(false);
        }

        eventItems.Insert(eventItem);
        return Task.FromResult(true);
    }

    public static string BuildDedupKey(string eventId, string contentItemId)
        => $"{eventId.Trim()}|{contentItemId.Trim()}";
}
