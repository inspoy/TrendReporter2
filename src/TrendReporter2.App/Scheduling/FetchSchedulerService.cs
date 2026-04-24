using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TrendReporter2.Core.Configuration;
using TrendReporter2.Core.Jobs;

namespace TrendReporter2.App.Scheduling;

public sealed class FetchSchedulerService : BackgroundService
{
    private readonly AppConfig _config;
    private readonly IFetchJob _fetchJob;
    private readonly ILogger<FetchSchedulerService> _logger;
    private readonly SemaphoreSlim _runLock = new(1, 1);

    public FetchSchedulerService(
        AppConfig config,
        IFetchJob fetchJob,
        ILogger<FetchSchedulerService> logger)
    {
        _config = config;
        _fetchJob = fetchJob;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(_config.Analysis.FetchInterval);
        _logger.LogInformation("Fetch scheduler started. Interval={Interval}.", interval);

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
            _logger.LogWarning("Skipping fetch schedule tick because the previous run is still active.");
            return;
        }

        try
        {
            _logger.LogInformation("Fetch schedule tick started.");
            await _fetchJob.RunAsync(cancellationToken);
            _logger.LogInformation("Fetch schedule tick finished.");
        }
        finally
        {
            _runLock.Release();
        }
    }
}
