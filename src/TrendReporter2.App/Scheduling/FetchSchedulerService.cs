using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TrendReporter2.Core.Configuration;
using TrendReporter2.Core.Jobs;

namespace TrendReporter2.App.Scheduling;

public sealed class FetchSchedulerService : BackgroundService
{
    private readonly AppConfig _config;
    private readonly IFetchJob _fetchJob;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _runLock = new(1, 1);

    public FetchSchedulerService(
        AppConfig config,
        IFetchJob fetchJob,
        ILoggerFactory loggerFactory)
    {
        _config = config;
        _fetchJob = fetchJob;
        _logger = loggerFactory.CreateLogger("FetchScheduler");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(_config.Analysis.FetchInterval);
        _logger.LogInformation("抓取调度器已启动，间隔={Interval}。", interval);

        await TryRunFetchAsync(stoppingToken);

        using var timer = new PeriodicTimer(interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await TryRunFetchAsync(stoppingToken);
        }
    }

    private async Task TryRunFetchAsync(CancellationToken cancellationToken)
    {
        if (!await _runLock.WaitAsync(0, cancellationToken))
        {
            _logger.LogWarning("跳过本次抓取调度，上一次运行尚未完成。");
            return;
        }

        try
        {
            _logger.LogInformation("抓取调度周期开始。");
            await _fetchJob.RunAsync(cancellationToken);
            _logger.LogInformation("抓取调度周期结束。");
        }
        finally
        {
            _runLock.Release();
        }
    }
}
