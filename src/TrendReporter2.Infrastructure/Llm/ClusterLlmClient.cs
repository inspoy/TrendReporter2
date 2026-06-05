using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TrendReporter2.Core.Configuration;
using TrendReporter2.Core.Events;
using TrendReporter2.Core.Observability;

namespace TrendReporter2.Infrastructure.Llm;

public sealed class ClusterLlmClient : IClusterLlmClient
{
    private const int MaxRetryCount = 3;

    private readonly HttpClient _httpClient;
    private readonly AppConfig _config;
    private readonly IRunTelemetryRecorder? _telemetryRecorder;
    private readonly ILogger _logger;

    public ClusterLlmClient(HttpClient httpClient, AppConfig config, ILoggerFactory loggerFactory, IRunTelemetryRecorder? telemetryRecorder = null)
    {
        _httpClient = httpClient;
        _config = config;
        _telemetryRecorder = telemetryRecorder;
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
        OpenAiChatParseResult<ClusterMatchResult>? finalResult = null;
        var retryCount = 0;
        for (var attempt = 0; attempt <= MaxRetryCount; attempt++)
        {
            if (attempt > 0)
            {
                retryCount++;
            }

            try
            {
                using var httpRequest = BuildRequest(request);
                using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
                var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    finalResult = new OpenAiChatParseResult<ClusterMatchResult>(
                        ClusterMatchResult.CreateNew("聚类 LLM HTTP 请求失败"),
                        false,
                        $"HTTP {(int)response.StatusCode}",
                        null,
                        new LlmUsageTokens(null, null, null));
                    _logger.LogWarning(
                        "聚类 LLM 请求失败，内容条目编号={ContentItemId}，HTTP状态={StatusCode}，耗时{ElapsedSec:F1}s，响应体={Body}",
                        request.Item.Id,
                        (int)response.StatusCode,
                        stopwatch.Elapsed.Seconds,
                        Truncate(NormalizeSnippet(responseBody), 500));
                    if (attempt < MaxRetryCount)
                    {
                        continue;
                    }

                    break;
                }

                finalResult = ParseResponse(responseBody, request, stopwatch.ElapsedMilliseconds);
                if (finalResult.Success || attempt == MaxRetryCount)
                {
                    break;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                _logger.LogWarning(
                    ex,
                    "聚类 LLM 网络异常，内容条目编号={ContentItemId}，耗时{ElapsedSec:F1}s",
                    request.Item.Id,
                    stopwatch.Elapsed.Seconds);
                finalResult = new OpenAiChatParseResult<ClusterMatchResult>(
                    ClusterMatchResult.CreateNew("聚类 LLM 网络异常"),
                    false,
                    ex.Message,
                    null,
                    new LlmUsageTokens(null, null, null));
                if (attempt >= MaxRetryCount)
                {
                    break;
                }
            }
        }

        finalResult ??= new OpenAiChatParseResult<ClusterMatchResult>(
            ClusterMatchResult.CreateNew("聚类 LLM 请求失败"), false, "聚类 LLM 请求失败", null, new LlmUsageTokens(null, null, null));
        await RecordUsageAsync(request, finalResult, stopwatch.ElapsedMilliseconds, retryCount, cancellationToken);
        return finalResult.Result;
    }

    private HttpRequestMessage BuildRequest(ClusterMatchRequest request)
    {
        var httpRequest = new HttpRequestMessage(HttpMethod.Post, BuildEndpoint(_config.Llm.Cluster.BaseUrl));
        if (!string.IsNullOrWhiteSpace(_config.Llm.Cluster.ApiKey))
        {
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.Llm.Cluster.ApiKey.Trim());
        }

        httpRequest.Content = new StringContent(
            JsonConvert.SerializeObject(BuildPayload(request)),
            Encoding.UTF8,
            "application/json");
        return httpRequest;
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

    private OpenAiChatParseResult<ClusterMatchResult> ParseResponse(string responseBody, ClusterMatchRequest request, long elapsedMs)
    {
        LlmUsageTokens tokens = new(null, null, null);
        try
        {
            var root = JObject.Parse(responseBody);
            var requestId = root.Value<string>("id");
            tokens = OpenAiUsageParser.Parse(root);
            var content = root["choices"]?.First?["message"]?.Value<string>("content");
            if (string.IsNullOrWhiteSpace(content))
            {
                _logger.LogWarning(
                    "聚类 LLM 返回空内容，内容条目编号={ContentItemId}，耗时{ElapsedSec:F1}s，响应体={Body}",
                    request.Item.Id,
                    elapsedMs / 1000f,
                    Truncate(NormalizeSnippet(responseBody), 500));
                return new OpenAiChatParseResult<ClusterMatchResult>(
                    ClusterMatchResult.CreateNew("聚类 LLM 返回空内容"), false, "聚类 LLM 返回空内容", requestId, tokens);
            }

            var parsed = JObject.Parse(content.Trim('`'));
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
                    "聚类 LLM 返回无效决策，内容条目编号={ContentItemId}，耗时{ElapsedSec:F1}s，内容={Content}",
                    request.Item.Id,
                    elapsedMs / 1000f,
                    Truncate(NormalizeSnippet(content), 500));
                return new OpenAiChatParseResult<ClusterMatchResult>(
                    ClusterMatchResult.CreateNew("聚类 LLM 返回无效决策"), false, "聚类 LLM 返回无效决策", requestId, tokens);
            }

            if ((decision == ClusterDecisions.SameEvent || decision == ClusterDecisions.FollowUp) &&
                (string.IsNullOrWhiteSpace(eventId) || request.Candidates.All(candidate => candidate.Event.Id != eventId)))
            {
                _logger.LogWarning(
                    "聚类 LLM 返回未知事件编号，内容条目编号={ContentItemId}，耗时{ElapsedSec:F1}s，事件编号={EventId}，内容={Content}",
                    request.Item.Id,
                    elapsedMs / 1000f,
                    eventId,
                    Truncate(NormalizeSnippet(content), 500));
                return new OpenAiChatParseResult<ClusterMatchResult>(
                    ClusterMatchResult.CreateNew("聚类 LLM 返回未知事件编号"), false, "聚类 LLM 返回未知事件编号", requestId, tokens);
            }

            var result = new ClusterMatchResult(
                decision,
                eventId,
                parsed.Value<string>("canonicalTitle"),
                parsed.Value<string>("summary"),
                Math.Clamp(confidence, 0, 1),
                parsed.Value<string>("reason"));
            _logger.LogInformation(
                "聚类 LLM 解析结果，决策={Decision}，事件标题={EventTitle}，置信度={Confidence}，原因={Reason}，内容条目编号={ContentItemId}，耗时{ElapsedSec:F1}s，事件编号={EventId}",
                result.Decision,
                result.CanonicalTitle,
                result.Confidence,
                Truncate(NormalizeSnippet(result.Reason), 300),
                request.Item.Id,
                elapsedMs / 1000f,
                result.EventId);
            return new OpenAiChatParseResult<ClusterMatchResult>(result, true, null, requestId, tokens);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(
                ex,
                "聚类 LLM 返回无效 JSON，内容条目编号={ContentItemId}，耗时{ElapsedSec:F1}s，响应体={Body}",
                request.Item.Id,
                elapsedMs / 1000f,
                Truncate(NormalizeSnippet(responseBody), 500));
            return new OpenAiChatParseResult<ClusterMatchResult>(
                ClusterMatchResult.CreateNew("聚类 LLM 返回无效 JSON"), false, "聚类 LLM 返回无效 JSON", null, tokens);
        }
    }

    private Task RecordUsageAsync(
        ClusterMatchRequest request,
        OpenAiChatParseResult<ClusterMatchResult> result,
        long elapsedMs,
        int retryCount,
        CancellationToken cancellationToken)
    {
        if (_telemetryRecorder is null)
        {
            return Task.CompletedTask;
        }

        var eventId = result.Result is { Decision: ClusterDecisions.SameEvent or ClusterDecisions.FollowUp, EventId: not null }
            ? result.Result.EventId
            : null;
        var usage = new LlmUsageRecord(
            $"lu:{Guid.NewGuid():N}",
            request.RunId,
            LlmUsageStages.Cluster,
            _config.Llm.Cluster.Model,
            result.RequestId,
            request.Item.Id,
            eventId,
            result.Tokens.InputTokens,
            result.Tokens.OutputTokens,
            result.Tokens.CacheReadTokens,
            LlmUsageCostCalculator.EstimateCost(result.Tokens, _config.Llm.Cluster.Pricing),
            ToDurationMs(elapsedMs),
            result.Success,
            retryCount,
            result.Error,
            DateTimeOffset.UtcNow);
        return _telemetryRecorder.RecordLlmUsageAsync(usage, cancellationToken);
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

    private static int ToDurationMs(long elapsedMs)
        => elapsedMs > int.MaxValue ? int.MaxValue : Math.Max(0, (int)elapsedMs);
}
