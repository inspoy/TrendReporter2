using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TrendReporter2.Core.Configuration;
using TrendReporter2.Core.Content;
using TrendReporter2.Core.Enrichment;

namespace TrendReporter2.Infrastructure.Enrichment;

public sealed class TavilyClient : ITavilyClient
{
    private static readonly Uri ExtractEndpoint = new("https://api.tavily.com/extract");
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);
    private const int SummaryMaxLength = 360;

    private readonly HttpClient _httpClient;
    private readonly AppConfig _config;
    private readonly ILogger<TavilyClient> _logger;

    public TavilyClient(HttpClient httpClient, AppConfig config, ILogger<TavilyClient> logger)
    {
        _httpClient = httpClient;
        _config = config;
        _logger = logger;
    }

    public async Task<EnrichmentResult?> EnrichAsync(ContentItem item, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, ExtractEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.Tavily.ApiKey);
        request.Content = new StringContent(
            JsonConvert.SerializeObject(new
            {
                urls = item.Url,
                query = item.Title,
                chunks_per_source = 3,
                extract_depth = "basic",
                format = "text",
                include_images = false,
                include_favicon = false,
                timeout = 10,
                include_usage = false
            }),
            Encoding.UTF8,
            "application/json");

        string responseBody;
        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Tavily Extract failed for contentItemId={ContentItemId}, status={StatusCode}, reason={ReasonPhrase}.",
                    item.Id,
                    (int)response.StatusCode,
                    response.ReasonPhrase);
                return null;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("Tavily Extract timed out for contentItemId={ContentItemId}.", item.Id);
            return null;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Tavily Extract HTTP request failed for contentItemId={ContentItemId}.", item.Id);
            return null;
        }

        var root = JObject.Parse(responseBody);
        var firstResult = root["results"]?.OfType<JObject>().FirstOrDefault();
        var rawContent = firstResult?.Value<string>("raw_content");
        var summary = BuildSummary(rawContent);
        if (string.IsNullOrWhiteSpace(summary))
        {
            _logger.LogWarning("Tavily Extract returned no usable content for contentItemId={ContentItemId}.", item.Id);
            return null;
        }

        return new EnrichmentResult
        {
            Summary = summary,
            Title = item.Title,
            Url = firstResult?.Value<string>("url") ?? item.Url,
            RawPayload = responseBody
        };
    }

    private static string BuildSummary(string? rawContent)
    {
        var normalized = Whitespace.Replace(rawContent ?? string.Empty, " ").Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return string.Empty;
        }

        return normalized.Length <= SummaryMaxLength
            ? normalized
            : normalized[..SummaryMaxLength].TrimEnd() + "...";
    }
}

