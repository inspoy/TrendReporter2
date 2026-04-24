using Microsoft.Extensions.Logging;
using TrendReporter2.Core.Jobs;

namespace TrendReporter2.App.Scheduling;

public sealed class EmptyFetchJob : IFetchJob
{
    private readonly ILogger<EmptyFetchJob> _logger;

    public EmptyFetchJob(ILogger<EmptyFetchJob> logger)
    {
        _logger = logger;
    }

    public Task RunAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Fetch job placeholder executed. Business logic starts in M1.");
        return Task.CompletedTask;
    }
}
