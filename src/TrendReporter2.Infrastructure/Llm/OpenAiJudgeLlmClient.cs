using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TrendReporter2.Core.Configuration;
using TrendReporter2.Core.Events;

namespace TrendReporter2.Infrastructure.Llm;

public sealed class OpenAiJudgeLlmClient : IJudgeLlmClient
{
    private readonly HttpClient _httpClient;
    private readonly AppConfig _config;
    private readonly ILogger<OpenAiJudgeLlmClient> _logger;

    public OpenAiJudgeLlmClient(HttpClient httpClient, AppConfig config, ILogger<OpenAiJudgeLlmClient> logger)
    {
        _httpClient = httpClient;
        _config = config;
        _logger = logger;
    }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_config.Llm.Judge.BaseUrl) &&
        !string.IsNullOrWhiteSpace(_config.Llm.Judge.Model);

    public async Task<JudgeResult> JudgeAsync(JudgeRequest request, CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            return JudgeResult.Neutral("judge llm is not configured");
        }

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, BuildEndpoint(_config.Llm.Judge.BaseUrl));
        if (!string.IsNullOrWhiteSpace(_config.Llm.Judge.ApiKey))
        {
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.Llm.Judge.ApiKey.Trim());
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
                    "Judge LLM request failed for eventId={EventId}. Status={StatusCode}, Body={Body}",
                    request.Event.Id,
                    (int)response.StatusCode,
                    Truncate(responseBody, 500));
                return JudgeResult.Neutral("judge llm http failure");
            }

            return ParseResponse(responseBody, request.Event.Id);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or UriFormatException or TaskCanceledException)
        {
            _logger.LogWarning(ex, "Judge LLM request failed for eventId={EventId}.", request.Event.Id);
            return JudgeResult.Neutral("judge llm request failed");
        }
    }

    private object BuildPayload(JudgeRequest request)
        => new
        {
            model = _config.Llm.Judge.Model,
            messages = new object[]
            {
                new
                {
                    role = "system",
                    content = "You judge news event importance. Return only JSON with importance, boostScore, labels, reason, and optional summary, stage, progressSummary. boostScore must be 0 to 1. stage must be Initial, Expanding, Escalating, FollowUp, or Cooling if present."
                },
                new
                {
                    role = "user",
                    content = JsonConvert.SerializeObject(new
                    {
                        eventId = request.Event.Id,
                        canonicalTitle = request.Event.CanonicalTitle,
                        summary = request.Event.Summary,
                        keyTerms = request.Event.KeyTerms,
                        representativeTitles = request.Event.RepresentativeTitles,
                        score = new
                        {
                            request.Score.UniqueSourceCount,
                            request.Score.AvgNormalizedRank,
                            request.Score.HeatValue,
                            request.Score.TrendScore,
                            request.Score.TotalScore
                        },
                        triggerReasons = request.TriggerReasons,
                        items = request.Evidence.Take(6).Select(evidence => new
                        {
                            title = evidence.ContentItem.Title,
                            summary = evidence.ContentItem.Summary,
                            source = evidence.ContentItem.Source,
                            rank = evidence.Snapshot.Rank,
                            normalizedRankScore = evidence.Snapshot.NormalizedRankScore
                        })
                    })
                }
            },
            max_tokens = Math.Max(1, _config.Llm.Judge.MaxTokens),
            response_format = new { type = "json_object" }
        };

    private JudgeResult ParseResponse(string responseBody, string eventId)
    {
        try
        {
            var root = JObject.Parse(responseBody);
            var content = root["choices"]?.First?["message"]?.Value<string>("content");
            if (string.IsNullOrWhiteSpace(content))
            {
                return JudgeResult.Neutral("judge llm returned empty content");
            }

            var result = JObject.Parse(content);
            var labels = result["labels"] is JArray labelArray
                ? ParseLabels(labelArray)
                : [];
            return new JudgeResult(
                result.Value<string>("importance"),
                Math.Clamp(result.Value<double?>("boostScore") ?? 0, 0, 1),
                labels,
                result.Value<string>("reason"),
                result.Value<string>("summary"),
                NormalizeStage(result.Value<string>("stage")),
                result.Value<string>("progressSummary"));
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Judge LLM returned invalid JSON for eventId={EventId}.", eventId);
            return JudgeResult.Neutral("judge llm returned invalid json");
        }
    }

    private static List<string> ParseLabels(JArray labelArray)
    {
        var labels = new List<string>();
        foreach (var label in labelArray.Values<string>())
        {
            if (string.IsNullOrWhiteSpace(label))
            {
                continue;
            }

            labels.Add(label.Trim());
        }

        return labels;
    }

    private static string? NormalizeStage(string? stage)
    {
        if (string.IsNullOrWhiteSpace(stage))
        {
            return null;
        }

        var value = stage.Trim();
        var validStages = new[]
        {
            EventProgressStages.Initial,
            EventProgressStages.Expanding,
            EventProgressStages.Escalating,
            EventProgressStages.FollowUp,
            EventProgressStages.Cooling
        };
        return validStages.FirstOrDefault(valid => string.Equals(valid, value, StringComparison.OrdinalIgnoreCase));
    }

    private static Uri BuildEndpoint(string baseUrl)
    {
        var normalized = baseUrl.Trim().TrimEnd('/');
        return new Uri(normalized + "/v1/chat/completions");
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];
}
