namespace TrendReporter2.Core.Jobs;

public interface IDigestJob
{
    Task RunAsync(CancellationToken cancellationToken);
}
