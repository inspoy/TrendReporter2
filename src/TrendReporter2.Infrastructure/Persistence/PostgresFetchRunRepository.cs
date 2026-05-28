using System.Security.Cryptography;
using System.Text;
using Dapper;
using Npgsql;
using TrendReporter2.Core.Fetch;

namespace TrendReporter2.Infrastructure.Persistence;

public sealed class PostgresFetchRunRepository : IFetchRunRepository
{
    private readonly NpgsqlDataSource _dataSource;

    static PostgresFetchRunRepository()
    {
        DefaultTypeMap.MatchNamesWithUnderscores = true;
    }

    public PostgresFetchRunRepository(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task<FetchRun> CreateAsync(int sourceCount, DateTimeOffset startedAt, CancellationToken cancellationToken)
    {
        var fetchRun = new FetchRun
        {
            Id = BuildFetchRunId(startedAt),
            StartedAt = startedAt,
            Status = FetchRunStatuses.Running,
            SourceCount = sourceCount
        };

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition("""
        insert into fetch_run (id, started_at, finished_at, status, source_count, success_source_count, failure_source_count,
            fetched_item_count, enriched_item_count, matched_event_count, pushed_event_count, errors)
        values (@Id, @StartedAt, @FinishedAt, @Status, @SourceCount, @SuccessSourceCount, @FailureSourceCount,
            @FetchedItemCount, @EnrichedItemCount, @MatchedEventCount, @PushedEventCount, @Errors::jsonb);
        """, ToParameters(fetchRun), cancellationToken: cancellationToken));

        return fetchRun;
    }

    public async Task CompleteAsync(FetchRun fetchRun, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition("""
        update fetch_run
        set finished_at = @FinishedAt,
            status = @Status,
            source_count = @SourceCount,
            success_source_count = @SuccessSourceCount,
            failure_source_count = @FailureSourceCount,
            fetched_item_count = @FetchedItemCount,
            enriched_item_count = @EnrichedItemCount,
            matched_event_count = @MatchedEventCount,
            pushed_event_count = @PushedEventCount,
            errors = @Errors::jsonb
        where id = @Id;
        """, ToParameters(fetchRun), cancellationToken: cancellationToken));
    }

    private static object ToParameters(FetchRun fetchRun)
        => new
        {
            fetchRun.Id,
            StartedAt = PostgresTimestamp.ToUtc(fetchRun.StartedAt),
            FinishedAt = PostgresTimestamp.ToUtc(fetchRun.FinishedAt),
            fetchRun.Status,
            fetchRun.SourceCount,
            fetchRun.SuccessSourceCount,
            fetchRun.FailureSourceCount,
            fetchRun.FetchedItemCount,
            fetchRun.EnrichedItemCount,
            fetchRun.MatchedEventCount,
            fetchRun.PushedEventCount,
            Errors = PostgresJson.Serialize(fetchRun.Errors)
        };

    private static string BuildFetchRunId(DateTimeOffset startedAt)
        => $"fr:{startedAt.UtcDateTime:yyyyMMddHHmmssfff}:{Guid.NewGuid():N}"[..31];
}

public sealed class PostgresAppStateRepository : TrendReporter2.Core.Events.IAppStateRepository
{
    private readonly NpgsqlDataSource _dataSource;

    static PostgresAppStateRepository()
    {
        DefaultTypeMap.MatchNamesWithUnderscores = true;
    }

    public PostgresAppStateRepository(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task<TrendReporter2.Core.Events.AppState?> GetAsync(string key, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<AppStateRow>(new CommandDefinition("""
        select id as Id, key as Key, value as Value, updated_at as UpdatedAt
        from app_state
        where key = @Key;
        """, new { Key = key }, cancellationToken: cancellationToken));

        return row is null ? null : new TrendReporter2.Core.Events.AppState
        {
            Id = row.Id,
            Key = row.Key,
            Value = row.Value,
            UpdatedAt = row.UpdatedAt
        };
    }

    public async Task UpsertAsync(TrendReporter2.Core.Events.AppState state, CancellationToken cancellationToken)
    {
        state.Id = string.IsNullOrWhiteSpace(state.Id) ? BuildId(state.Key) : state.Id;
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition("""
        insert into app_state (id, key, value, updated_at)
        values (@Id, @Key, @Value, @UpdatedAt)
        on conflict (key) do update
        set value = excluded.value,
            updated_at = excluded.updated_at;
        """, ToParameters(state), cancellationToken: cancellationToken));
    }

    private static string BuildId(string key)
        => "as:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..24].ToLowerInvariant();

    private static object ToParameters(TrendReporter2.Core.Events.AppState state)
        => new
        {
            state.Id,
            state.Key,
            state.Value,
            UpdatedAt = PostgresTimestamp.ToUtc(state.UpdatedAt)
        };

    private sealed record AppStateRow(string Id, string Key, string Value, DateTimeOffset UpdatedAt);
}
