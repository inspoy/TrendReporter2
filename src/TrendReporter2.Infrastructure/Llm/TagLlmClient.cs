using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TrendReporter2.Core.Configuration;
using TrendReporter2.Core.Observability;
using TrendReporter2.Core.Tags;

namespace TrendReporter2.Infrastructure.Llm;

public sealed class TagLlmClient : ITagLlmClient
{
    private const int MaxRetryCount = 3;

    private readonly HttpClient _httpClient;
    private readonly AppConfig _config;
    private readonly ITagService _tagService;
    private readonly IRunTelemetryRecorder? _telemetryRecorder;
    private readonly ILogger _logger;

    public TagLlmClient(
        HttpClient httpClient,
        AppConfig config,
        ITagService tagService,
        ILoggerFactory loggerFactory,
        IRunTelemetryRecorder? telemetryRecorder = null)
    {
        _httpClient = httpClient;
        _config = config;
        _tagService = tagService;
        _telemetryRecorder = telemetryRecorder;
        _logger = loggerFactory.CreateLogger("LLM.Tagging");
    }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_config.Llm.Tagging.BaseUrl) &&
        !string.IsNullOrWhiteSpace(_config.Llm.Tagging.Model);

    public async Task<TagLlmResult> GenerateTagsAsync(TagLlmRequest request, CancellationToken cancellationToken)
    {
        if (!IsConfigured || IsEmptyContent(request))
        {
            return new TagLlmResult([]);
        }

        var stopwatch = Stopwatch.StartNew();
        OpenAiChatParseResult<TagLlmResult>? finalResult = null;
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
                    finalResult = new OpenAiChatParseResult<TagLlmResult>(
                        new TagLlmResult([]),
                        false,
                        $"HTTP {(int)response.StatusCode}",
                        null,
                        new LlmUsageTokens(null, null, null));
                    _logger.LogWarning(
                        "标签 LLM 请求失败，内容条目编号={ContentItemId}，HTTP状态={StatusCode}，耗时{ElapsedSec:F1}s，响应体={Body}",
                        request.ContentItem.Id,
                        (int)response.StatusCode,
                        stopwatch.Elapsed.Seconds,
                        Truncate(NormalizeSnippet(responseBody), 500));
                    if (attempt < MaxRetryCount)
                    {
                        continue;
                    }

                    break;
                }

                finalResult = ParseResponse(responseBody, request.ContentItem.Id, stopwatch.ElapsedMilliseconds);
                if (finalResult.Success || attempt == MaxRetryCount)
                {
                    break;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                _logger.LogWarning(
                    ex,
                    "标签 LLM 网络异常，内容条目编号={ContentItemId}，耗时{ElapsedSec:F1}s",
                    request.ContentItem.Id,
                    stopwatch.Elapsed.Seconds);
                finalResult = new OpenAiChatParseResult<TagLlmResult>(
                    new TagLlmResult([]),
                    false,
                    ex.Message,
                    null,
                    new LlmUsageTokens(null, null, null));
                if (attempt >= MaxRetryCount)
                {
                    break;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(
                    ex,
                    "标签 LLM 调用异常，内容条目编号={ContentItemId}，耗时{ElapsedSec:F1}s",
                    request.ContentItem.Id,
                    stopwatch.Elapsed.Seconds);
                finalResult = new OpenAiChatParseResult<TagLlmResult>(
                    new TagLlmResult([]),
                    false,
                    ex.Message,
                    null,
                    new LlmUsageTokens(null, null, null));
                break;
            }
        }

        finalResult ??= new OpenAiChatParseResult<TagLlmResult>(
            new TagLlmResult([]), false, "标签 LLM 请求失败", null, new LlmUsageTokens(null, null, null));
        await RecordUsageAsync(request, finalResult, stopwatch.ElapsedMilliseconds, retryCount, cancellationToken);
        return finalResult.Result;
    }

    private HttpRequestMessage BuildRequest(TagLlmRequest request)
    {
        var httpRequest = new HttpRequestMessage(HttpMethod.Post, BuildEndpoint(_config.Llm.Tagging.BaseUrl));
        if (!string.IsNullOrWhiteSpace(_config.Llm.Tagging.ApiKey))
        {
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.Llm.Tagging.ApiKey.Trim());
        }

        httpRequest.Content = new StringContent(
            JsonConvert.SerializeObject(BuildPayload(request)),
            Encoding.UTF8,
            "application/json");
        return httpRequest;
    }

    private object BuildPayload(TagLlmRequest request)
        => new
        {
            model = _config.Llm.Tagging.Model,
            messages = new object[]
            {
                new
                {
                    role = "system",
                    content = "你是一个新闻标签抽取助手。只返回 JSON 对象，格式为 {\"tags\":[{\"name\":string,\"displayName\":string,\"category\":string,\"confidence\":number}]}。category 只能是 topic、entity、domain、risk。返回 3-5 个对检索有用的短标签。"
                },
                new
                {
                    role = "user",
                    content = JsonConvert.SerializeObject(new
                    {
                        id = request.ContentItem.Id,
                        title = request.ContentItem.Title,
                        summary = request.ContentItem.Summary,
                        source = request.ContentItem.Source,
                        category = request.ContentItem.Category,
                        url = request.ContentItem.Url
                    })
                }
            },
            max_tokens = Math.Max(1, _config.Llm.Tagging.MaxTokens),
            response_format = new { type = "json_object" }
        };

    private OpenAiChatParseResult<TagLlmResult> ParseResponse(string responseBody, string contentItemId, long elapsedMs)
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
                    "标签 LLM 返回空内容，内容条目编号={ContentItemId}，耗时{ElapsedSec:F1}s，响应体={Body}",
                    contentItemId,
                    elapsedMs / 1000f,
                    Truncate(NormalizeSnippet(responseBody), 500));
                return new OpenAiChatParseResult<TagLlmResult>(new TagLlmResult([]), false, "标签 LLM 返回空内容", requestId, tokens);
            }

            var parsed = JObject.Parse(content);
            var llmTags = parsed["tags"] is JArray tagArray
                ? ParseTags(tagArray)
                : [];
            var tags = _tagService.FromLlmTags(llmTags);
            if (tags.Count == 0)
            {
                return new OpenAiChatParseResult<TagLlmResult>(new TagLlmResult([]), false, "标签 LLM 未返回有效标签", requestId, tokens);
            }

            _logger.LogInformation(
                "标签 LLM 解析完成，内容条目编号={ContentItemId}，标签数={TagCount}，耗时{ElapsedSec:F1}s，标签={Tags}。",
                contentItemId,
                tags.Count,
                elapsedMs / 1000f,
                JsonConvert.SerializeObject(tags.Select(t => new { t.Name, t.DisplayName, t.Category, t.Confidence })));
            return new OpenAiChatParseResult<TagLlmResult>(new TagLlmResult(tags), true, null, requestId, tokens);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(
                ex,
                "标签 LLM 返回无效 JSON，内容条目编号={ContentItemId}，耗时{ElapsedSec:F1}s，响应体={Body}",
                contentItemId,
                elapsedMs / 1000f,
                Truncate(NormalizeSnippet(responseBody), 500));
            return new OpenAiChatParseResult<TagLlmResult>(new TagLlmResult([]), false, "标签 LLM 返回无效 JSON", null, tokens);
        }
    }

    private Task RecordUsageAsync(
        TagLlmRequest request,
        OpenAiChatParseResult<TagLlmResult> result,
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
            LlmUsageStages.Tagging,
            _config.Llm.Tagging.Model,
            result.RequestId,
            request.ContentItem.Id,
            null,
            result.Tokens.InputTokens,
            result.Tokens.OutputTokens,
            result.Tokens.CacheReadTokens,
            LlmUsageCostCalculator.EstimateCost(result.Tokens, _config.Llm.Tagging.Pricing),
            ToDurationMs(elapsedMs),
            result.Success,
            retryCount,
            result.Error,
            DateTimeOffset.UtcNow);
        return _telemetryRecorder.RecordLlmUsageAsync(usage, cancellationToken);
    }

    private static List<TagLlmTag> ParseTags(JArray tagArray)
    {
        var tags = new List<TagLlmTag>();
        foreach (var token in tagArray.OfType<JObject>())
        {
            var name = token.Value<string>("name") ?? token.Value<string>("displayName") ?? string.Empty;
            var displayName = token.Value<string>("displayName");
            var category = token.Value<string>("category");
            var confidence = ParseConfidence(token["confidence"]);
            tags.Add(new TagLlmTag(name, displayName, category, confidence));
        }

        return tags;
    }

    private static double? ParseConfidence(JToken? token)
    {
        if (token is null || token.Type == JTokenType.Null)
        {
            return null;
        }

        return double.TryParse(token.ToString(), out var value) ? value : null;
    }

    private static bool IsEmptyContent(TagLlmRequest request)
        => string.IsNullOrWhiteSpace(request.ContentItem.Title) &&
            string.IsNullOrWhiteSpace(request.ContentItem.Summary);

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
