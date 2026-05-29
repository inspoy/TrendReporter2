using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TrendReporter2.Core.Configuration;
using TrendReporter2.Core.Sources;

namespace TrendReporter2.Infrastructure.News;

public sealed class DailyHotApiClient : IContentSourceClient
{
    private readonly HttpClient _httpClient;
    private readonly AppConfig _config;
    private readonly ILogger _logger;

    public DailyHotApiClient(HttpClient httpClient, AppConfig config, ILoggerFactory loggerFactory)
    {
        _httpClient = httpClient;
        _config = config;
        _logger = loggerFactory.CreateLogger("DailyHotApi");
    }

    public string Provider => SourceProviders.DailyHotApi;

    public async Task<IReadOnlyList<FetchedContentItem>> FetchAsync(
        SourceDefinition source,
        CancellationToken cancellationToken)
    {
        var requestUri = BuildRequestUri(source.ExternalId);
        using var response = await _httpClient.GetAsync(requestUri, cancellationToken);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"DailyHotApi 请求失败，来源='{source.ExternalId}'，状态码={(int)response.StatusCode}: {response.ReasonPhrase}");
        }

        var root = JObject.Parse(responseBody);
        var code = ParseCode(root["code"]);
        if (code is not null && code != 200)
        {
            throw new InvalidOperationException($"DailyHotApi 返回了不支持的 code '{code}'，来源='{source.ExternalId}'。");
        }

        var items = root["data"] as JArray ?? [];
        var result = new List<FetchedContentItem>(items.Count);

        for (var i = 0; i < items.Count; i++)
        {
            if (items[i] is not JObject item)
            {
                _logger.LogWarning("跳过非对象类型的 DailyHotApi 条目，来源={Source}，索引={Index}。", source.ExternalId, i);
                continue;
            }

            var title = FirstText(item, "title", "name");
            var url = FirstText(item, "url", "mobileUrl", "link");
            var summaryText = FirstText(item, "desc", "description", "summary", "hot");
            if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(url) && string.IsNullOrWhiteSpace(summaryText))
            {
                _logger.LogWarning("跳过空 DailyHotApi 条目，来源={Source}，索引={Index}。", source.ExternalId, i);
                continue;
            }

            var sourceItemId = FirstText(item, "id");
            if (string.IsNullOrWhiteSpace(sourceItemId))
            {
                sourceItemId = BuildHash(source.ExternalId, string.IsNullOrWhiteSpace(url) ? title + summaryText : url);
            }

            result.Add(new FetchedContentItem
            {
                SourceId = source.Id,
                SourceItemId = sourceItemId,
                DedupKey = BuildDedupKey(source.Id, sourceItemId),
                ContentKind = source.ContentKind,
                Category = source.Category,
                Title = title,
                Url = url,
                MobileUrl = FirstText(item, "mobileUrl"),
                PublishedAt = source.ContentKind == ContentKind.FlashFeed
                    ? ParseDate(item["pubDate"]) ?? ParseDate(item["publishedAt"]) ?? ParseDate(item["date"]) ?? ParseDate(item["extra"]?["date"])
                    : null,
                Rank = source.ContentKind == ContentKind.RankedNews ? i + 1 : null,
                SourceListSize = source.ContentKind == ContentKind.RankedNews ? items.Count : null,
                HoverText = FirstText(item, "desc", "description", "summary", "hot")
                    ?? FirstText(item["extra"] as JObject, "hover"),
                SummaryText = summaryText,
                RawPayload = item.ToString(Formatting.None)
            });
        }

        _logger.LogInformation(
            "从 DailyHotApi 获取到 {ItemCount} 条数据，来源={Source}，类型={ContentKind}。",
            result.Count,
            source.ExternalId,
            source.ContentKind);

        return result;
    }

    private Uri BuildRequestUri(string externalId)
    {
        var baseUrl = _config.Sources.DailyHotApi.BaseUrl.TrimEnd('/');
        return new Uri($"{baseUrl}/{Uri.EscapeDataString(externalId)}");
    }

    private static int? ParseCode(JToken? token)
    {
        if (token is null || token.Type is JTokenType.Null or JTokenType.Undefined)
        {
            return null;
        }

        return int.TryParse(token.ToString().Trim(), out var code) ? code : null;
    }

    private static string FirstText(JObject? item, params string[] names)
    {
        if (item is null)
        {
            return string.Empty;
        }

        foreach (var name in names)
        {
            var value = item[name]?.ToString().Trim();
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return string.Empty;
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

        return DateTimeOffset.TryParse(
            text,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var parsed)
            ? parsed
            : null;
    }

    private static string BuildHash(string source, string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{source}|{value}"));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string BuildDedupKey(string sourceId, string sourceItemId)
        => $"{sourceId}:{sourceItemId}".ToLowerInvariant();
}
