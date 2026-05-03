using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TrendReporter2.Core.Configuration;
using TrendReporter2.Core.Content;
using TrendReporter2.Core.Enrichment;

namespace TrendReporter2.Infrastructure.Enrichment;

public sealed class WebExtractEnrichmentClient : IEnrichmentClient
{
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);
    private const int SummaryMaxLength = 360;

    private readonly HttpClient _httpClient;
    private readonly AppConfig _config;
    private readonly ILogger<WebExtractEnrichmentClient> _logger;

    public WebExtractEnrichmentClient(
        HttpClient httpClient,
        AppConfig config,
        ILogger<WebExtractEnrichmentClient> logger)
    {
        _httpClient = httpClient;
        _config = config;
        _logger = logger;
    }

    public async Task<EnrichmentResult?> EnrichAsync(ContentItem item, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        using var request = new HttpRequestMessage(HttpMethod.Post, BuildFetchEndpoint(_config.Enrichment.WebExtractUrl));
        request.Content = new StringContent(
            JsonConvert.SerializeObject(new { url = item.Url }),
            Encoding.UTF8,
            "application/json");

        string responseBody;
        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

            // if (!response.IsSuccessStatusCode)
            // {
            //     _logger.LogWarning(
            //         "Web extract failed for contentItemId={ContentItemId}, status={StatusCode}, reason={ReasonPhrase}.",
            //         item.Id,
            //         (int)response.StatusCode,
            //         response.ReasonPhrase);
            //     return null;
            // }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(
                "Web extract timed out for contentItemId={ContentItemId}. ElapsedMs={ElapsedMs}",
                item.Id,
                stopwatch.ElapsedMilliseconds);
            return null;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(
                ex,
                "Web extract HTTP request failed for contentItemId={ContentItemId}. ElapsedMs={ElapsedMs}",
                item.Id,
                stopwatch.ElapsedMilliseconds);
            return null;
        }
        catch (UriFormatException ex)
        {
            _logger.LogWarning(
                ex,
                "Web extract URL is invalid for contentItemId={ContentItemId}. ElapsedMs={ElapsedMs}",
                item.Id,
                stopwatch.ElapsedMilliseconds);
            return null;
        }

        var result = ParseResponse(responseBody, item);
        if (!result.Success)
        {
            _logger.LogWarning(
                "Web extract returned failure for contentItemId={ContentItemId}. ElapsedMs={ElapsedMs}, Message={Message}, Title={Title}, Summary={Summary}",
                item.Id,
                stopwatch.ElapsedMilliseconds,
                Truncate(NormalizeSnippet(result.Message), 300),
                Truncate(NormalizeSnippet(result.Title), 160),
                Truncate(NormalizeSnippet(result.Summary), 300));
            return null;
        }

        var summary = BuildSummary(result.Summary);
        if (string.IsNullOrWhiteSpace(summary))
        {
            _logger.LogWarning(
                "Web extract returned no usable content for contentItemId={ContentItemId}. ElapsedMs={ElapsedMs}, Message={Message}, Title={Title}",
                item.Id,
                stopwatch.ElapsedMilliseconds,
                Truncate(NormalizeSnippet(result.Message), 300),
                Truncate(NormalizeSnippet(result.Title), 160));
            return null;
        }

        _logger.LogInformation(
            "Web extract succeeded for contentItemId={ContentItemId}. ElapsedMs={ElapsedMs}, Title={Title}, Summary={Summary}",
            item.Id,
            stopwatch.ElapsedMilliseconds,
            Truncate(NormalizeSnippet(result.Title ?? item.Title), 160),
            Truncate(summary, 300));

        return new EnrichmentResult
        {
            Summary = summary,
            Title = result.Title ?? item.Title,
            Url = result.Url ?? item.Url,
            RawPayload = responseBody
        };
    }

    private static Uri BuildFetchEndpoint(string baseUrl)
    {
        var normalized = baseUrl.Trim().TrimEnd('/');
        if (!normalized.Contains("://", StringComparison.Ordinal))
        {
            normalized = "http://" + normalized;
        }

        return new Uri(normalized + "/fetch");
    }

    private static WebExtractResult ParseResponse(string responseBody, ContentItem item)
    {
        try
        {
            var root = JObject.Parse(responseBody);
            var success = root.Value<bool?>("success") ?? root["data"]?.Value<bool?>("success") ?? true;
            var message = ReadFirstString(root, "message");
            return new WebExtractResult(
                success,
                message,
                ReadFirstString(root, "summary") ?? ReadInsights(root),
                ReadFirstString(root, "title"),
                ReadFirstString(root, "url"));
        }
        catch (JsonException)
        {
            return new WebExtractResult(true, null, responseBody, item.Title, item.Url);
        }
    }

    private static string? ReadInsights(JObject root)
    {
        var insights = root["insights"] as JArray ?? root["data"]?["insights"] as JArray;
        if (insights is null)
        {
            return null;
        }

        var values = insights
            .OfType<JValue>()
            .Select(value => value.Value as string)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!.Trim());
        var summary = string.Join(" ", values);
        return string.IsNullOrWhiteSpace(summary) ? null : summary;
    }

    private static string? ReadFirstString(JObject root, params string[] names)
    {
        foreach (var name in names)
        {
            var value = root.Value<string>(name) ?? root["data"]?.Value<string>(name);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
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

    private static string NormalizeSnippet(string? value)
        => Whitespace.Replace(value ?? string.Empty, " ").Trim();

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];

    private sealed record WebExtractResult(bool Success, string? Message, string? Summary, string? Title, string? Url);
}
