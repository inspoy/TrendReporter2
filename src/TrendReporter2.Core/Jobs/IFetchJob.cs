namespace TrendReporter2.Core.Jobs;

public interface IFetchJob
{
    Task RunAsync(CancellationToken cancellationToken);
}
