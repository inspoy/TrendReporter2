using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TrendReporter2.Core.Configuration;
using TrendReporter2.Core.News;

namespace TrendReporter2.Infrastructure.News;

public sealed class NewsNowClient : INewsSourceClient
{
    private static readonly HashSet<string> AcceptedStatuses = new(StringComparer.OrdinalIgnoreCase)
    {
        "success",
        "cache"
    };

    private readonly HttpClient _httpClient;
    private readonly AppConfig _config;
    private readonly ILogger<NewsNowClient> _logger;

    public NewsNowClient(HttpClient httpClient, AppConfig config, ILogger<NewsNowClient> logger)
    {
        _httpClient = httpClient;
        _config = config;
        _logger = logger;
    }

    public async Task<IReadOnlyList<NewsItem>> FetchAsync(
        string category,
        string source,
        CancellationToken cancellationToken)
    {
        var requestUri = BuildRequestUri(source);
        using var response = await _httpClient.GetAsync(requestUri, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"NewsNow 请求失败，来源='{source}'，状态码={(int)response.StatusCode}: {response.ReasonPhrase}");
        }

        var root = JObject.Parse(responseBody);
        var status = root.Value<string>("status") ?? string.Empty;
        if (!AcceptedStatuses.Contains(status))
        {
            throw new InvalidOperationException($"NewsNow 返回了不支持的状态 '{status}'，来源='{source}'。");
        }

        var items = root["items"] as JArray ?? [];
        var sourceListSize = items.Count;
        var result = new List<NewsItem>(sourceListSize);

        for (var i = 0; i < sourceListSize; i++)
        {
            if (items[i] is not JObject item)
            {
                _logger.LogWarning("跳过非对象类型的 NewsNow 条目，来源={Source}，索引={Index}。", source, i);
                continue;
            }

            var title = item.Value<string>("title")?.Trim() ?? string.Empty;
            var url = item.Value<string>("url")?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(url))
            {
                _logger.LogWarning("跳过空 NewsNow 条目，来源={Source}，索引={Index}。", source, i);
                continue;
            }

            var sourceItemId = GetSourceItemId(source, item, title, url);
            result.Add(new NewsItem
            {
                Source = source,
                Category = category,
                SourceItemId = sourceItemId,
                Title = title,
                Url = url,
                MobileUrl = item.Value<string>("mobileUrl"),
                PubTime = ParseDate(item["pubDate"]) ?? ParseDate(item["extra"]?["date"]),
                HoverText = item["extra"]?.Value<string>("hover"),
                Rank = i + 1,
                SourceListSize = sourceListSize,
                RawPayload = item.ToString(Formatting.None)
            });
        }

        _logger.LogInformation(
            "从 NewsNow 获取到 {ItemCount} 条数据，来源={Source}，分类={Category}，状态={Status}。",
            result.Count,
            source,
            category,
            status);

        return result;
    }

    private Uri BuildRequestUri(string source)
    {
        var baseUrl = _config.NewsNow.BaseUrl.EndsWith("/", StringComparison.Ordinal)
            ? _config.NewsNow.BaseUrl
            : _config.NewsNow.BaseUrl + "/";

        var builder = new UriBuilder(new Uri(new Uri(baseUrl), "api/s"))
        {
            Query = "id=" + Uri.EscapeDataString(source)
        };

        return builder.Uri;
    }

    private static string GetSourceItemId(string source, JObject item, string title, string url)
    {
        var explicitId = item["id"]?.ToString();
        if (!string.IsNullOrWhiteSpace(explicitId))
        {
            return explicitId.Trim();
        }

        var fallback = string.IsNullOrWhiteSpace(url) ? title : url;
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{source}|{fallback}"));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static DateTimeOffset? ParseDate(JToken? token)
    {
        if (token is null || token.Type is JTokenType.Null or JTokenType.Undefined or JTokenType.Boolean)
        {
            return null;
        }

        if (token.Type is JTokenType.Integer or JTokenType.Float)
        {
            var value = token.Value<long>();
            return value > 10_000_000_000
                ? DateTimeOffset.FromUnixTimeMilliseconds(value)
                : DateTimeOffset.FromUnixTimeSeconds(value);
        }

        var text = token.ToString().Trim();
        if (long.TryParse(text, out var numericValue))
        {
            return numericValue > 10_000_000_000
                ? DateTimeOffset.FromUnixTimeMilliseconds(numericValue)
                : DateTimeOffset.FromUnixTimeSeconds(numericValue);
        }

        return DateTimeOffset.TryParse(text, out var parsed) ? parsed : null;
    }
}
