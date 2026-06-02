using Dapper;
using Npgsql;
using TrendReporter2.Core.Events;

namespace TrendReporter2.Infrastructure.Persistence;

public sealed class PostgresEventMergeRepository : IEventMergeRepository
{
    private readonly NpgsqlDataSource _dataSource;

    static PostgresEventMergeRepository()
    {
        DefaultTypeMap.MatchNamesWithUnderscores = true;
    }

    public PostgresEventMergeRepository(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task InsertMergeHistoryAsync(EventMergeHistory mergeHistory, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition("""
        insert into event_merge_history (id, source_event_id, target_event_id, confidence, reason, decided_by, evidence_snapshot, created_at)
        values (@Id, @SourceEventId, @TargetEventId, @Confidence, @Reason, @DecidedBy, @EvidenceSnapshot::jsonb, @CreatedAt)
        on conflict (id) do nothing;
        """, ToParameters(mergeHistory), cancellationToken: cancellationToken));
    }

    public async Task<bool> HasBeenProcessedAsync(string sourceEventId, string targetEventId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        var count = await connection.QuerySingleAsync<int>(new CommandDefinition("""
        select count(*)
        from event_merge_history
        where (source_event_id = @A and target_event_id = @B)
           or (source_event_id = @B and target_event_id = @A);
        """, new { A = sourceEventId, B = targetEventId }, cancellationToken: cancellationToken));
        return count > 0;
    }

    public async Task MigrateEventItemsAsync(string sourceEventId, string targetEventId, string mergeHistoryId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition("""
        insert into event_item (id, dedup_key, event_id, content_item_id, confidence, matched_at, match_reason, is_active, created_by_merge_id)
        select 'ei:' || substr(md5(@TargetEventId || '|' || content_item_id), 1, 20),
               @TargetEventId || '|' || content_item_id,
               @TargetEventId,
               content_item_id,
               confidence,
               @Now,
               match_reason,
               true,
               @MergeHistoryId
        from event_item
        where event_id = @SourceEventId
          and is_active = true
        on conflict (content_item_id) do nothing;
        """, new
        {
            SourceEventId = sourceEventId,
            TargetEventId = targetEventId,
            MergeHistoryId = mergeHistoryId,
            Now = PostgresTimestamp.ToUtc(now)
        }, transaction, cancellationToken: cancellationToken));
        await connection.ExecuteAsync(new CommandDefinition("""
        update event_item
        set is_active = false
        where event_id = @SourceEventId
          and is_active = true;
        """, new { SourceEventId = sourceEventId }, transaction, cancellationToken: cancellationToken));
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task DeactivateEventItemsAsync(string eventId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition("""
        update event_item
        set is_active = false
        where event_id = @EventId;
        """, new { EventId = eventId }, cancellationToken: cancellationToken));
    }

    private static object ToParameters(EventMergeHistory mergeHistory)
        => new
        {
            mergeHistory.Id,
            mergeHistory.SourceEventId,
            mergeHistory.TargetEventId,
            mergeHistory.Confidence,
            mergeHistory.Reason,
            mergeHistory.DecidedBy,
            EvidenceSnapshot = PostgresJson.EmptyObjectIfBlank(mergeHistory.EvidenceSnapshot),
            CreatedAt = PostgresTimestamp.ToUtc(mergeHistory.CreatedAt)
        };
}
