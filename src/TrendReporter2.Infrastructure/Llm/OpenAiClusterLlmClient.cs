using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TrendReporter2.Core.Configuration;
using TrendReporter2.Core.Events;

namespace TrendReporter2.Infrastructure.Llm;

public sealed class OpenAiClusterLlmClient : IClusterLlmClient
{
    private readonly HttpClient _httpClient;
    private readonly AppConfig _config;
    private readonly ILogger<OpenAiClusterLlmClient> _logger;

    public OpenAiClusterLlmClient(HttpClient httpClient, AppConfig config, ILogger<OpenAiClusterLlmClient> logger)
    {
        _httpClient = httpClient;
        _config = config;
        _logger = logger;
    }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_config.Llm.Cluster.BaseUrl) &&
        !string.IsNullOrWhiteSpace(_config.Llm.Cluster.Model);

    public async Task<ClusterMatchResult> MatchAsync(ClusterMatchRequest request, CancellationToken cancellationToken)
    {
        if (!IsConfigured || request.Candidates.Count == 0)
        {
            return ClusterMatchResult.CreateNew("cluster llm is not configured or no candidates were provided");
        }

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, BuildEndpoint(_config.Llm.Cluster.BaseUrl));
        if (!string.IsNullOrWhiteSpace(_config.Llm.Cluster.ApiKey))
        {
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.Llm.Cluster.ApiKey.Trim());
        }

        httpRequest.Content = new StringContent(
            JsonConvert.SerializeObject(BuildPayload(request)),
            Encoding.UTF8,
            "application/json");

        try
        {
            using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Cluster LLM request failed for contentItemId={ContentItemId}. Status={StatusCode}, Body={Body}",
                    request.Item.Id,
                    (int)response.StatusCode,
                    Truncate(responseBody, 500));
                return ClusterMatchResult.CreateNew("cluster llm http failure");
            }

            return ParseResponse(responseBody, request);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or UriFormatException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Cluster LLM request failed for contentItemId={ContentItemId}.", request.Item.Id);
            return ClusterMatchResult.CreateNew("cluster llm request failed");
        }
    }

    private object BuildPayload(ClusterMatchRequest request)
        => new
        {
            model = _config.Llm.Cluster.Model,
            messages = new object[]
            {
                new
                {
                    role = "system",
                    content = "You judge whether one news item belongs to an existing event. Return only JSON with decision, eventId, canonicalTitle, summary, confidence, reason. decision must be same_event, follow_up, related_but_distinct, or unrelated."
                },
                new
                {
                    role = "user",
                    content = JsonConvert.SerializeObject(new
                    {
                        item = new
                        {
                            id = request.Item.Id,
                            title = request.Item.Title,
                            summary = request.Item.Summary,
                            hoverText = request.Item.HoverText,
                            source = request.Item.Source,
                            pubTime = request.Item.PubTime
                        },
                        candidates = request.Candidates.Select(candidate => new
                        {
                            eventId = candidate.Event.Id,
                            canonicalTitle = candidate.Event.CanonicalTitle,
                            summary = candidate.Event.Summary,
                            keyTerms = candidate.Event.KeyTerms,
                            representativeTitles = candidate.Event.RepresentativeTitles,
                            status = candidate.Event.Status,
                            score = candidate.Score,
                            matchedFeatures = candidate.MatchedFeatures
                        })
                    })
                }
            },
            max_tokens = Math.Max(1, _config.Llm.Cluster.MaxTokens),
            response_format = new { type = "json_object" }
        };

    private ClusterMatchResult ParseResponse(string responseBody, ClusterMatchRequest request)
    {
        try
        {
            var root = JObject.Parse(responseBody);
            var content = root["choices"]?.First?["message"]?.Value<string>("content");
            if (string.IsNullOrWhiteSpace(content))
            {
                return ClusterMatchResult.CreateNew("cluster llm returned empty content");
            }

            var result = JObject.Parse(content);
            var decision = result.Value<string>("decision")?.Trim().ToLowerInvariant();
            var eventId = result.Value<string>("eventId")?.Trim();
            var confidence = result.Value<double?>("confidence") ?? 0;
            var validDecisions = new[]
            {
                ClusterDecisions.SameEvent,
                ClusterDecisions.FollowUp,
                ClusterDecisions.RelatedButDistinct,
                ClusterDecisions.Unrelated
            };

            if (string.IsNullOrWhiteSpace(decision) || !validDecisions.Contains(decision, StringComparer.OrdinalIgnoreCase))
            {
                return ClusterMatchResult.CreateNew("cluster llm returned invalid decision");
            }

            if ((decision == ClusterDecisions.SameEvent || decision == ClusterDecisions.FollowUp) &&
                (string.IsNullOrWhiteSpace(eventId) || request.Candidates.All(candidate => candidate.Event.Id != eventId)))
            {
                return ClusterMatchResult.CreateNew("cluster llm returned unknown eventId");
            }

            return new ClusterMatchResult(
                decision,
                eventId,
                result.Value<string>("canonicalTitle"),
                result.Value<string>("summary"),
                Math.Clamp(confidence, 0, 1),
                result.Value<string>("reason"));
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Cluster LLM returned invalid JSON for contentItemId={ContentItemId}.", request.Item.Id);
            return ClusterMatchResult.CreateNew("cluster llm returned invalid json");
        }
    }

    private Uri BuildEndpoint(string baseUrl)
    {
        var normalized = baseUrl.Trim().TrimEnd('/');
        return new Uri(normalized + "/v1/chat/completions");
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];
}
