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
        _logger.LogInformation("摘要任务占位执行完成，业务逻辑将在 M5 中实现。");
        return Task.CompletedTask;
    }
}
