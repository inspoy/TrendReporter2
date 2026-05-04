using System.Text;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using TrendReporter2.Core.Configuration;
using TrendReporter2.Core.Events;

namespace TrendReporter2.Infrastructure.Push;

public sealed class UnipushPusher : IPusher
{
    private readonly HttpClient _httpClient;
    private readonly AppConfig _config;
    private readonly ILogger _logger;

    public UnipushPusher(HttpClient httpClient, AppConfig config, ILoggerFactory loggerFactory)
    {
        _httpClient = httpClient;
        _config = config;
        _logger = loggerFactory.CreateLogger("Unipush");
    }

    public string Type => "unipush";

    public bool IsConfigured => GetConfig() is not null;

    public async Task<PushResult> PushAsync(PushMessage message, CancellationToken cancellationToken)
    {
        var config = GetConfig();
        var payload = JsonConvert.SerializeObject(new
        {
            cate = string.IsNullOrWhiteSpace(config?.Cate) ? "default" : config.Cate,
            title = message.Title,
            msg = message.Message,
            link = message.Link
        });

        if (config is null)
        {
            return PushResult.Skipped("unipush 未配置", payload);
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, BuildEndpoint(config));
            request.Headers.TryAddWithoutValidation("Push-Key", config.Secret.Trim());
            request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                return new PushResult(true, payload, null);
            }

            var error = $"unipush HTTP {(int)response.StatusCode}: {Truncate(responseBody, 300)}";
            _logger.LogWarning("Unipush 推送失败，事件编号={EventId}。{Error}", message.EventId, error);
            return new PushResult(false, payload, error);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or UriFormatException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Unipush 请求失败，事件编号={EventId}。", message.EventId);
            return new PushResult(false, payload, "unipush 请求失败");
        }
    }

    private PusherConfig? GetConfig()
        => _config.Pushers.FirstOrDefault(pusher =>
            string.Equals(pusher.Type, Type, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(pusher.Url) &&
            !string.IsNullOrWhiteSpace(pusher.Secret));

    private static Uri BuildEndpoint(PusherConfig config)
    {
        var separator = config.Url.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        var channels = Uri.EscapeDataString(config.Channels ?? string.Empty);
        return new Uri(config.Url.Trim() + separator + "channels=" + channels);
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];
}
