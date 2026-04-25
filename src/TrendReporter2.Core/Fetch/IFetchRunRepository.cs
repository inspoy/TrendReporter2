namespace TrendReporter2.Core.Fetch;

public interface IFetchRunRepository
{
    Task<FetchRun> CreateAsync(int sourceCount, DateTimeOffset startedAt, CancellationToken cancellationToken);

    Task CompleteAsync(FetchRun fetchRun, CancellationToken cancellationToken);
}
