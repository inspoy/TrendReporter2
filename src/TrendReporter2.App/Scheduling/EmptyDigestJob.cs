using Microsoft.Extensions.Logging;
using TrendReporter2.Core.Jobs;

namespace TrendReporter2.App.Scheduling;

public sealed class EmptyDigestJob : IDigestJob
{
    private readonly ILogger<EmptyDigestJob> _logger;

    public EmptyDigestJob(ILogger<EmptyDigestJob> logger)
    {
        _logger = logger;
    }

    public Task RunAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Digest job placeholder executed. Business logic starts in M5.");
        return Task.CompletedTask;
    }
}
