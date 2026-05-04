using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using TrendReporter2.Core.Configuration;
using TrendReporter2.Core.Jobs;

namespace TrendReporter2.App.Scheduling;

public sealed class DigestSchedulerService : BackgroundService
{
    private readonly AppConfig _config;
    private readonly IDigestJob _digestJob;
    private readonly ILogger _logger;
    private readonly TimeZoneInfo _timeZone;

    public DigestSchedulerService(
        AppConfig config,
        IDigestJob digestJob,
        ILoggerFactory loggerFactory)
    {
        _config = config;
        _digestJob = digestJob;
        _logger = loggerFactory.CreateLogger("DigestScheduler");
        _timeZone = TimeZoneResolver.Find(config.System.TimeZone);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "摘要调度器已启动，推送时间={PushTimes}，时区={TimeZone}。",
            string.Join(",", _config.Analysis.Push.PushTime),
            _config.System.TimeZone);

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));

        while (!stoppingToken.IsCancellationRequested)
        {
            var localNow = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, _timeZone);
            var currentTime = localNow.ToString("HH:mm");

            if (_config.Analysis.Push.PushTime.Contains(currentTime, StringComparer.Ordinal))
            {
                _logger.LogInformation("摘要调度触发，本地时间={LocalTime}。", currentTime);
                await _digestJob.RunAsync(stoppingToken);
            }

            await timer.WaitForNextTickAsync(stoppingToken);
        }
    }
}
