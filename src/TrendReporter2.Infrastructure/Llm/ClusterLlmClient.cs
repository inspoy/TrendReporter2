using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TrendReporter2.Core.Configuration;
using TrendReporter2.Core.Events;

namespace TrendReporter2.Infrastructure.Llm;

public sealed class ClusterLlmClient : IClusterLlmClient
{
    private readonly HttpClient _httpClient;
    private readonly AppConfig _config;
    private readonly ILogger _logger;

    public ClusterLlmClient(HttpClient httpClient, AppConfig config, ILoggerFactory loggerFactory)
    {
        _httpClient = httpClient;
        _config = config;
        _logger = loggerFactory.CreateLogger("LLM.Cluster");
    }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_config.Llm.Cluster.BaseUrl) &&
        !string.IsNullOrWhiteSpace(_config.Llm.Cluster.Model);

    public async Task<ClusterMatchResult> MatchAsync(ClusterMatchRequest request, CancellationToken cancellationToken)
    {
        if (!IsConfigured || request.Candidates.Count == 0)
        {
            return ClusterMatchResult.CreateNew("聚类 LLM 未配置或没有提供候选事件");
        }

        var stopwatch = Stopwatch.StartNew();
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
                    "聚类 LLM 请求失败，内容条目编号={ContentItemId}，HTTP状态={StatusCode}，耗时毫秒={ElapsedMs}，响应体={Body}",
                    request.Item.Id,
                    (int)response.StatusCode,
                    stopwatch.ElapsedMilliseconds,
                    Truncate(NormalizeSnippet(responseBody), 500));
                return ClusterMatchResult.CreateNew("聚类 LLM HTTP 请求失败");
            }

            return ParseResponse(responseBody, request, stopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or UriFormatException or TaskCanceledException)
        {
            _logger.LogWarning(
                ex,
                "聚类 LLM 请求失败，内容条目编号={ContentItemId}，耗时毫秒={ElapsedMs}",
                request.Item.Id,
                stopwatch.ElapsedMilliseconds);
            return ClusterMatchResult.CreateNew("聚类 LLM 请求异常");
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
                    content = "你是一个新闻事件聚类助手。你需要判断一条新闻条目是否属于已有事件。只返回 JSON，包含 decision、eventId、canonicalTitle、summary、confidence、reason 字段。decision 必须为 same_event、follow_up、related_but_distinct 或 unrelated。"
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

    private ClusterMatchResult ParseResponse(string responseBody, ClusterMatchRequest request, long elapsedMs)
    {
        try
        {
            var root = JObject.Parse(responseBody);
            var content = root["choices"]?.First?["message"]?.Value<string>("content");
            if (string.IsNullOrWhiteSpace(content))
            {
                _logger.LogWarning(
                    "聚类 LLM 返回空内容，内容条目编号={ContentItemId}，耗时毫秒={ElapsedMs}，响应体={Body}",
                    request.Item.Id,
                    elapsedMs,
                    Truncate(NormalizeSnippet(responseBody), 500));
                return ClusterMatchResult.CreateNew("聚类 LLM 返回空内容");
            }

            var parsed = JObject.Parse(content);
            var decision = parsed.Value<string>("decision")?.Trim().ToLowerInvariant();
            var eventId = parsed.Value<string>("eventId")?.Trim();
            var confidence = parsed.Value<double?>("confidence") ?? 0;
            var validDecisions = new[]
            {
                ClusterDecisions.SameEvent,
                ClusterDecisions.FollowUp,
                ClusterDecisions.RelatedButDistinct,
                ClusterDecisions.Unrelated
            };

            if (string.IsNullOrWhiteSpace(decision) || !validDecisions.Contains(decision, StringComparer.OrdinalIgnoreCase))
            {
                _logger.LogWarning(
                    "聚类 LLM 返回无效决策，内容条目编号={ContentItemId}，耗时毫秒={ElapsedMs}，内容={Content}",
                    request.Item.Id,
                    elapsedMs,
                    Truncate(NormalizeSnippet(content), 500));
                return ClusterMatchResult.CreateNew("聚类 LLM 返回无效决策");
            }

            if ((decision == ClusterDecisions.SameEvent || decision == ClusterDecisions.FollowUp) &&
                (string.IsNullOrWhiteSpace(eventId) || request.Candidates.All(candidate => candidate.Event.Id != eventId)))
            {
                _logger.LogWarning(
                    "聚类 LLM 返回未知事件编号，内容条目编号={ContentItemId}，耗时毫秒={ElapsedMs}，事件编号={EventId}，内容={Content}",
                    request.Item.Id,
                    elapsedMs,
                    eventId,
                    Truncate(NormalizeSnippet(content), 500));
                return ClusterMatchResult.CreateNew("聚类 LLM 返回未知事件编号");
            }

            var result = new ClusterMatchResult(
                decision,
                eventId,
                parsed.Value<string>("canonicalTitle"),
                parsed.Value<string>("summary"),
                Math.Clamp(confidence, 0, 1),
                parsed.Value<string>("reason"));
            _logger.LogInformation(
                "聚类 LLM 解析结果，决策={Decision}，事件标题={EventTitle}，置信度={Confidence}，原因={Reason}，内容条目编号={ContentItemId}，耗时毫秒={ElapsedMs}，事件编号={EventId}",
                result.Decision,
                result.CanonicalTitle,
                result.Confidence,
                Truncate(NormalizeSnippet(result.Reason), 300),
                request.Item.Id,
                elapsedMs,
                result.EventId);
            return result;
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(
                ex,
                "聚类 LLM 返回无效 JSON，内容条目编号={ContentItemId}，耗时毫秒={ElapsedMs}，响应体={Body}",
                request.Item.Id,
                elapsedMs,
                Truncate(NormalizeSnippet(responseBody), 500));
            return ClusterMatchResult.CreateNew("聚类 LLM 返回无效 JSON");
        }
    }

    private Uri BuildEndpoint(string baseUrl)
    {
        var normalized = baseUrl.Trim().TrimEnd('/');
        return new Uri(normalized + "/v1/chat/completions");
    }

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength];

    private static string NormalizeSnippet(string? value)
        => string.Join(' ', (value ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
