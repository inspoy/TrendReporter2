namespace TrendReporter2.Core.Jobs;

public interface IDigestJob
{
    Task RunAsync(DateOnly localDate, string slotTime, DateTimeOffset localNow, CancellationToken cancellationToken);
}
