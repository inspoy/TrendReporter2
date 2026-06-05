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

public sealed class JudgeLlmClient : IJudgeLlmClient
{
    private const int MaxRetryCount = 3;

    private readonly HttpClient _httpClient;
    private readonly AppConfig _config;
    private readonly IRunTelemetryRecorder? _telemetryRecorder;
    private readonly ILogger _logger;

    public JudgeLlmClient(HttpClient httpClient, AppConfig config, ILoggerFactory loggerFactory, IRunTelemetryRecorder? telemetryRecorder = null)
    {
        _httpClient = httpClient;
        _config = config;
        _telemetryRecorder = telemetryRecorder;
        _logger = loggerFactory.CreateLogger("LLM.Judge");
    }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_config.Llm.Judge.BaseUrl) &&
        !string.IsNullOrWhiteSpace(_config.Llm.Judge.Model);

    public async Task<JudgeResult> JudgeAsync(JudgeRequest request, CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            return JudgeResult.Neutral("评判 LLM 未配置");
        }

        var stopwatch = Stopwatch.StartNew();
        OpenAiChatParseResult<JudgeResult>? finalResult = null;
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
                    finalResult = new OpenAiChatParseResult<JudgeResult>(
                        JudgeResult.Neutral("评判 LLM HTTP 请求失败"),
                        false,
                        $"HTTP {(int)response.StatusCode}",
                        null,
                        new LlmUsageTokens(null, null, null));
                    _logger.LogWarning(
                        "评判 LLM 请求失败，事件编号={EventId}，HTTP状态={StatusCode}，耗时{ElapsedSec:F1}s，响应体={Body}",
                        request.Event.Id,
                        (int)response.StatusCode,
                        stopwatch.Elapsed.Seconds,
                        Truncate(NormalizeSnippet(responseBody), 500));
                    if (attempt < MaxRetryCount)
                    {
                        continue;
                    }

                    break;
                }

                finalResult = ParseResponse(responseBody, request.Event.Id, stopwatch.ElapsedMilliseconds);
                if (finalResult.Success || attempt == MaxRetryCount)
                {
                    break;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                _logger.LogWarning(
                    ex,
                    "评判 LLM 网络异常，事件编号={EventId}，耗时{ElapsedSec:F1}s",
                    request.Event.Id,
                    stopwatch.Elapsed.Seconds);
                finalResult = new OpenAiChatParseResult<JudgeResult>(
                    JudgeResult.Neutral("评判 LLM 网络异常"),
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

        finalResult ??= new OpenAiChatParseResult<JudgeResult>(
            JudgeResult.Neutral("评判 LLM 请求失败"), false, "评判 LLM 请求失败", null, new LlmUsageTokens(null, null, null));
        await RecordUsageAsync(request, finalResult, stopwatch.ElapsedMilliseconds, retryCount, cancellationToken);
        return finalResult.Result;
    }

    private HttpRequestMessage BuildRequest(JudgeRequest request)
    {
        var httpRequest = new HttpRequestMessage(HttpMethod.Post, BuildEndpoint(_config.Llm.Judge.BaseUrl));
        if (!string.IsNullOrWhiteSpace(_config.Llm.Judge.ApiKey))
        {
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.Llm.Judge.ApiKey.Trim());
        }

        httpRequest.Content = new StringContent(
            JsonConvert.SerializeObject(BuildPayload(request)),
            Encoding.UTF8,
            "application/json");
        return httpRequest;
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
                    content = "你是一个新闻事件重要性评判助手。你需要判断新闻事件的重要程度。只返回 JSON，包含 importance、boostScore、labels、reason 字段，以及可选的 summary、stage、progressSummary 字段。boostScore 必须在 0 到 1 之间。stage 如果存在，必须为 Initial、Expanding、Escalating、FollowUp 或 Cooling。"
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

    private OpenAiChatParseResult<JudgeResult> ParseResponse(string responseBody, string eventId, long elapsedMs)
    {
        LlmUsageTokens tokens = new(null, null, null);
        try
        {
            var root = JObject.Parse(responseBody.Trim('`'));
            var requestId = root.Value<string>("id");
            tokens = OpenAiUsageParser.Parse(root);
            var content = root["choices"]?.First?["message"]?.Value<string>("content");
            if (string.IsNullOrWhiteSpace(content))
            {
                _logger.LogWarning(
                    "评判 LLM 返回空内容，事件编号={EventId}，耗时{ElapsedSec:F1}s，响应体={Body}",
                    eventId,
                    elapsedMs / 1000f,
                    Truncate(NormalizeSnippet(responseBody), 500));
                return new OpenAiChatParseResult<JudgeResult>(
                    JudgeResult.Neutral("评判 LLM 返回空内容"), false, "评判 LLM 返回空内容", requestId, tokens);
            }

            var parsed = JObject.Parse(content);
            var labels = parsed["labels"] is JArray labelArray
                ? ParseLabels(labelArray)
                : [];
            var result = new JudgeResult(
                parsed.Value<string>("importance"),
                Math.Clamp(parsed.Value<double?>("boostScore") ?? 0, 0, 1),
                labels,
                parsed.Value<string>("reason"),
                parsed.Value<string>("summary"),
                NormalizeStage(parsed.Value<string>("stage")),
                parsed.Value<string>("progressSummary"));
            _logger.LogInformation(
                "评判 LLM 解析结果，事件编号={EventId}，耗时{ElapsedSec:F1}s，重要性={Importance}，加权分数={BoostScore}，阶段={Stage}，原因={Reason}",
                eventId,
                elapsedMs / 1000,
                result.Importance,
                result.BoostScore,
                result.Stage,
                Truncate(NormalizeSnippet(result.Reason), 300));
            return new OpenAiChatParseResult<JudgeResult>(result, true, null, requestId, tokens);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(
                ex,
                "评判 LLM 返回无效 JSON，事件编号={EventId}，耗时{Elapsed:F1}s，响应体={Body}",
                eventId,
                elapsedMs / 1000f,
                Truncate(NormalizeSnippet(responseBody), 500));
            return new OpenAiChatParseResult<JudgeResult>(
                JudgeResult.Neutral("评判 LLM 返回无效 JSON"), false, "评判 LLM 返回无效 JSON", null, tokens);
        }
    }

    private Task RecordUsageAsync(
        JudgeRequest request,
        OpenAiChatParseResult<JudgeResult> result,
        long elapsedMs,
        int retryCount,
        CancellationToken cancellationToken)
    {
        if (_telemetryRecorder is null)
        {
            return Task.CompletedTask;
        }

        var usage = new LlmUsageRecord(
            $"lu:{Guid.NewGuid():N}",
            request.RunId,
            LlmUsageStages.Judge,
            _config.Llm.Judge.Model,
            result.RequestId,
            null,
            request.Event.Id,
            result.Tokens.InputTokens,
            result.Tokens.OutputTokens,
            result.Tokens.CacheReadTokens,
            LlmUsageCostCalculator.EstimateCost(result.Tokens, _config.Llm.Judge.Pricing),
            ToDurationMs(elapsedMs),
            result.Success,
            retryCount,
            result.Error,
            DateTimeOffset.UtcNow);
        return _telemetryRecorder.RecordLlmUsageAsync(usage, cancellationToken);
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

    private static string NormalizeSnippet(string? value)
        => string.Join(' ', (value ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static int ToDurationMs(long elapsedMs)
        => elapsedMs > int.MaxValue ? int.MaxValue : Math.Max(0, (int)elapsedMs);
}
