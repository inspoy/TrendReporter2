using Dapper;
using Npgsql;
using TrendReporter2.Core.Sources;

namespace TrendReporter2.Infrastructure.Persistence;

public sealed class PostgresSourceRepository : ISourceRepository
{
    private readonly NpgsqlDataSource _dataSource;

    public PostgresSourceRepository(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task UpsertSourcesAsync(IReadOnlyList<SourceDefinition> sources, CancellationToken cancellationToken)
    {
        if (sources.Count == 0)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        foreach (var source in sources)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await connection.ExecuteAsync(new CommandDefinition("""
            insert into source (id, provider, external_id, category, name, display_name, content_kind, enabled, weight, created_at, updated_at)
            values (@Id, @Provider, @ExternalId, @Category, @Name, @DisplayName, @ContentKind, @Enabled, @Weight, @CreatedAt, @UpdatedAt)
            on conflict (id) do update
            set provider = excluded.provider,
                external_id = excluded.external_id,
                category = excluded.category,
                name = excluded.name,
                display_name = excluded.display_name,
                content_kind = excluded.content_kind,
                enabled = excluded.enabled,
                weight = excluded.weight,
                updated_at = excluded.updated_at;
            """, new
            {
                source.Id,
                source.Provider,
                source.ExternalId,
                source.Category,
                Name = source.ExternalId,
                source.DisplayName,
                source.ContentKind,
                source.Enabled,
                source.Weight,
                CreatedAt = PostgresTimestamp.ToUtc(now),
                UpdatedAt = PostgresTimestamp.ToUtc(now)
            }, transaction, cancellationToken: cancellationToken));
        }

        await transaction.CommitAsync(cancellationToken);
    }
}
