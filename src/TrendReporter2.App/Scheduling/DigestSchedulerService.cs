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
    private readonly SemaphoreSlim _runLock = new(1, 1);
    private string? _lastTriggeredSlot;

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
            var currentSlot = $"{localNow:yyyy-MM-dd}:{currentTime}";

            try
            {
                if (_config.Analysis.Push.PushTime.Contains(currentTime, StringComparer.Ordinal) && !string.Equals(_lastTriggeredSlot, currentSlot, StringComparison.Ordinal))
                {
                    _lastTriggeredSlot = currentSlot;
                    await TryRunDigestAsync(DateOnly.FromDateTime(localNow.DateTime), currentTime, localNow, stoppingToken);
                }

                await timer.WaitForNextTickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "摘要调度循环发生未预期错误；调度器将继续等待下一轮。");
            }
        }
    }

    private async Task TryRunDigestAsync(DateOnly localDate, string slotTime, DateTimeOffset localNow, CancellationToken cancellationToken)
    {
        if (!await _runLock.WaitAsync(0, cancellationToken))
        {
            _logger.LogWarning("跳过本次摘要调度，上一次运行尚未完成。时段={SlotTime}。", slotTime);
            return;
        }

        try
        {
            _logger.LogInformation("摘要调度触发，本地日期={LocalDate}，时段={SlotTime}。", localDate, slotTime);
            await _digestJob.RunAsync(localDate, slotTime, localNow, cancellationToken);
            _logger.LogInformation("摘要调度完成，本地日期={LocalDate}，时段={SlotTime}。", localDate, slotTime);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "摘要调度发生未预期错误；本轮已终止，后续调度将继续。本地日期={LocalDate}，时段={SlotTime}。", localDate, slotTime);
        }
        finally
        {
            _runLock.Release();
        }
    }
}
