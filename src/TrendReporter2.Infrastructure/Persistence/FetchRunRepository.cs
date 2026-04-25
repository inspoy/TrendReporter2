using TrendReporter2.Core.Fetch;
using TrendReporter2.Core.Persistence;

namespace TrendReporter2.Infrastructure.Persistence;

public sealed class FetchRunRepository : IFetchRunRepository
{
    private readonly LiteDbConnectionFactory _connectionFactory;

    public FetchRunRepository(LiteDbConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public Task<FetchRun> CreateAsync(int sourceCount, DateTimeOffset startedAt, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var fetchRun = new FetchRun
        {
            Id = BuildFetchRunId(startedAt),
            StartedAt = startedAt,
            Status = FetchRunStatuses.Running,
            SourceCount = sourceCount
        };

        using var database = _connectionFactory.Open();
        database.GetCollection<FetchRun>(TrendCollectionNames.FetchRun).Insert(fetchRun);

        return Task.FromResult(fetchRun);
    }

    public Task CompleteAsync(FetchRun fetchRun, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var database = _connectionFactory.Open();
        database.GetCollection<FetchRun>(TrendCollectionNames.FetchRun).Update(fetchRun);

        return Task.CompletedTask;
    }

    private static string BuildFetchRunId(DateTimeOffset startedAt)
        => $"fr:{startedAt.UtcDateTime:yyyyMMddHHmmssfff}:{Guid.NewGuid():N}"[..31];
}
