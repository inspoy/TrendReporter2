using LiteDB;
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

    public Task<IReadOnlyList<RunEventScoringInput>> LoadRunEventScoringInputsAsync(string runId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var database = _connectionFactory.Open();
        var snapshots = database.GetCollection<ContentSnapshot>(TrendCollectionNames.ContentSnapshot)
            .Find(snapshot => snapshot.RunId == runId)
            .ToList();
        if (snapshots.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<RunEventScoringInput>>([]);
        }

        var contentIds = snapshots.Select(snapshot => snapshot.ContentItemId).ToHashSet(StringComparer.Ordinal);
        var eventItems = database.GetCollection<EventItem>(TrendCollectionNames.EventItem)
            .Find(item => contentIds.Contains(item.ContentItemId))
            .ToList();
        if (eventItems.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<RunEventScoringInput>>([]);
        }

        var contentItems = database.GetCollection<ContentItem>(TrendCollectionNames.ContentItem)
            .Find(item => contentIds.Contains(item.Id))
            .ToDictionary(item => item.Id, StringComparer.Ordinal);
        var snapshotsByContent = snapshots
            .GroupBy(snapshot => snapshot.ContentItemId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.OrderByDescending(snapshot => snapshot.CapturedAt).First(), StringComparer.Ordinal);
        var eventIds = eventItems.Select(item => item.EventId).Distinct(StringComparer.Ordinal).ToHashSet(StringComparer.Ordinal);
        var events = database.GetCollection<EventAggregate>(TrendCollectionNames.Event)
            .Find(eventAggregate => eventIds.Contains(eventAggregate.Id))
            .ToDictionary(eventAggregate => eventAggregate.Id, StringComparer.Ordinal);

        var result = eventItems
            .Where(item => events.ContainsKey(item.EventId) && contentItems.ContainsKey(item.ContentItemId) && snapshotsByContent.ContainsKey(item.ContentItemId))
            .GroupBy(item => item.EventId, StringComparer.Ordinal)
            .Select(group => new RunEventScoringInput(
                events[group.Key],
                group.Select(item => new RunEventContentEvidence(
                        contentItems[item.ContentItemId],
                        snapshotsByContent[item.ContentItemId],
                        item.MatchedAt))
                    .OrderBy(evidence => evidence.Snapshot.Rank)
                    .ToList()))
            .OrderBy(input => input.Event.LastSeenAt)
            .ToList();

        return Task.FromResult<IReadOnlyList<RunEventScoringInput>>(result);
    }

    public Task<IReadOnlyList<EventScoreSnapshot>> LoadRecentScoreSnapshotsAsync(
        IReadOnlyList<string> eventIds,
        DateTimeOffset since,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (eventIds.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<EventScoreSnapshot>>([]);
        }

        var ids = eventIds.ToHashSet(StringComparer.Ordinal);
        using var database = _connectionFactory.Open();
        var result = database.GetCollection<EventScoreSnapshot>(TrendCollectionNames.EventScoreSnapshot)
            .Find(snapshot => ids.Contains(snapshot.EventId) && snapshot.CalculatedAt >= since)
            .OrderBy(snapshot => snapshot.CalculatedAt)
            .ToList();

        return Task.FromResult<IReadOnlyList<EventScoreSnapshot>>(result);
    }

    public Task InsertEventScoreSnapshotAsync(EventScoreSnapshot snapshot, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var database = _connectionFactory.Open();
        database.GetCollection<EventScoreSnapshot>(TrendCollectionNames.EventScoreSnapshot).Upsert(snapshot);
        return Task.CompletedTask;
    }

    public Task<bool> InsertPushLogIfMissingAsync(PushLog pushLog, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var database = _connectionFactory.Open();
        var pushLogs = database.GetCollection<PushLog>(TrendCollectionNames.PushLog);
        try
        {
            if (pushLogs.Exists(log => log.DedupKey == pushLog.DedupKey))
            {
                return Task.FromResult(false);
            }

            pushLogs.Insert(pushLog);
            return Task.FromResult(true);
        }
        catch (LiteException ex) when (ex.ErrorCode == LiteException.INDEX_DUPLICATE_KEY)
        {
            return Task.FromResult(false);
        }
    }

    public Task UpdatePushLogAsync(PushLog pushLog, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var database = _connectionFactory.Open();
        database.GetCollection<PushLog>(TrendCollectionNames.PushLog).Update(pushLog);
        return Task.CompletedTask;
    }

    public Task UpdateEventsAsync(IReadOnlyList<EventAggregate> eventAggregates, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (eventAggregates.Count == 0)
        {
            return Task.CompletedTask;
        }

        using var database = _connectionFactory.Open();
        var events = database.GetCollection<EventAggregate>(TrendCollectionNames.Event);
        foreach (var eventAggregate in eventAggregates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            events.Update(eventAggregate);
        }

        return Task.CompletedTask;
    }

    public static string BuildDedupKey(string eventId, string contentItemId)
        => $"{eventId.Trim()}|{contentItemId.Trim()}";
}
