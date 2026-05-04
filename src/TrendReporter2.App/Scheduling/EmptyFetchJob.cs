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
        _logger.LogInformation("抓取任务占位执行完成，业务逻辑将在 M1 中实现。");
        return Task.CompletedTask;
    }
}
