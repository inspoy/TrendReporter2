using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TrendReporter2.Core.Configuration;
using TrendReporter2.Core.Embeddings;
using TrendReporter2.Core.Observability;

namespace TrendReporter2.Infrastructure.Llm;

public sealed class EmbeddingClient : IEmbeddingClient
{
    private const int MaxRetryCount = 3;

    private readonly HttpClient _httpClient;
    private readonly AppConfig _config;
    private readonly IRunTelemetryRecorder? _telemetryRecorder;
    private readonly ILogger _logger;

    public EmbeddingClient(HttpClient httpClient, AppConfig config, ILoggerFactory loggerFactory, IRunTelemetryRecorder? telemetryRecorder = null)
    {
        _httpClient = httpClient;
        _config = config;
        _telemetryRecorder = telemetryRecorder;
        _logger = loggerFactory.CreateLogger("LLM.Embedding");
    }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_config.Llm.Embedding.BaseUrl) &&
        !string.IsNullOrWhiteSpace(_config.Llm.Embedding.Model);

    public async Task<EmbeddingResult> EmbedAsync(EmbeddingRequest request, CancellationToken cancellationToken)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(request.Text))
        {
            return EmbeddingResult.Failed("embedding LLM 未配置或输入为空");
        }

        var stopwatch = Stopwatch.StartNew();
        EmbeddingParseResult? finalResult = null;
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
                    finalResult = new EmbeddingParseResult(EmbeddingResult.Failed($"HTTP {(int)response.StatusCode}"), false, $"HTTP {(int)response.StatusCode}", null, new LlmUsageTokens(null, null, null));
                    _logger.LogWarning("embedding LLM 请求失败，内容条目编号={ContentItemId}，事件编号={EventId}，HTTP状态={StatusCode}，响应体={Body}", request.ContentItemId, request.EventId, (int)response.StatusCode, Truncate(NormalizeSnippet(responseBody), 500));
                    if (attempt < MaxRetryCount)
                    {
                        continue;
                    }

                    break;
                }

                finalResult = ParseResponse(responseBody);
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
                _logger.LogWarning(ex, "embedding LLM 网络异常，内容条目编号={ContentItemId}，事件编号={EventId}", request.ContentItemId, request.EventId);
                finalResult = new EmbeddingParseResult(EmbeddingResult.Failed(ex.Message), false, ex.Message, null, new LlmUsageTokens(null, null, null));
                if (attempt >= MaxRetryCount)
                {
                    break;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "embedding LLM 调用异常，内容条目编号={ContentItemId}，事件编号={EventId}", request.ContentItemId, request.EventId);
                finalResult = new EmbeddingParseResult(EmbeddingResult.Failed(ex.Message), false, ex.Message, null, new LlmUsageTokens(null, null, null));
                break;
            }
        }

        finalResult ??= new EmbeddingParseResult(EmbeddingResult.Failed("embedding LLM 请求失败"), false, "embedding LLM 请求失败", null, new LlmUsageTokens(null, null, null));
        await RecordUsageAsync(request, finalResult, stopwatch.ElapsedMilliseconds, retryCount, cancellationToken);
        return finalResult.Result;
    }

    private HttpRequestMessage BuildRequest(EmbeddingRequest request)
    {
        var httpRequest = new HttpRequestMessage(HttpMethod.Post, BuildEndpoint(_config.Llm.Embedding.BaseUrl));
        if (!string.IsNullOrWhiteSpace(_config.Llm.Embedding.ApiKey))
        {
            httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.Llm.Embedding.ApiKey.Trim());
        }

        var input = EmbeddingTextBuilder.CapText(request.Text, _config.Llm.Embedding.MaxTokens);
        httpRequest.Content = new StringContent(JsonConvert.SerializeObject(new
        {
            model = _config.Llm.Embedding.Model,
            input,
            dimensions = _config.Llm.Embedding.Dimensions,
            encoding_format = "float"
        }), Encoding.UTF8, "application/json");
        return httpRequest;
    }

    private EmbeddingParseResult ParseResponse(string responseBody)
    {
        LlmUsageTokens tokens = new(null, null, null);
        try
        {
            var root = JObject.Parse(responseBody);
            var requestId = root.Value<string>("id");
            tokens = OpenAiUsageParser.Parse(root);
            var values = root["data"]?.First?["embedding"] as JArray;
            if (values is null)
            {
                return new EmbeddingParseResult(EmbeddingResult.Failed("embedding LLM 未返回向量"), false, "embedding LLM 未返回向量", requestId, tokens);
            }

            var embedding = values.Select(value => value.Value<float>()).ToArray();
            if (embedding.Length != _config.Llm.Embedding.Dimensions)
            {
                var error = $"embedding 维度不匹配：期望 {_config.Llm.Embedding.Dimensions}，实际 {embedding.Length}";
                return new EmbeddingParseResult(EmbeddingResult.Failed(error), false, error, requestId, tokens);
            }

            return new EmbeddingParseResult(new EmbeddingResult(true, embedding, tokens.InputTokens, requestId, null), true, null, requestId, tokens);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "embedding LLM 返回无效 JSON，响应体={Body}", Truncate(NormalizeSnippet(responseBody), 500));
            return new EmbeddingParseResult(EmbeddingResult.Failed("embedding LLM 返回无效 JSON"), false, "embedding LLM 返回无效 JSON", null, tokens);
        }
    }

    private Task RecordUsageAsync(EmbeddingRequest request, EmbeddingParseResult result, long elapsedMs, int retryCount, CancellationToken cancellationToken)
    {
        if (_telemetryRecorder is null)
        {
            return Task.CompletedTask;
        }

        var usage = new LlmUsageRecord(
            $"lu:{Guid.NewGuid():N}",
            request.RunId,
            LlmUsageStages.Embedding,
            _config.Llm.Embedding.Model,
            result.RequestId,
            request.ContentItemId,
            request.EventId,
            result.Tokens.InputTokens,
            result.Tokens.OutputTokens,
            result.Tokens.CacheReadTokens,
            LlmUsageCostCalculator.EstimateCost(result.Tokens, _config.Llm.Embedding.Pricing),
            ToDurationMs(elapsedMs),
            result.Success,
            retryCount,
            result.Error,
            DateTimeOffset.UtcNow);
        return _telemetryRecorder.RecordLlmUsageAsync(usage, cancellationToken);
    }

    private static string BuildEndpoint(string baseUrl)
        => $"{baseUrl.TrimEnd('/')}/v1/embeddings";

    private static int ToDurationMs(long elapsedMs)
        => elapsedMs > int.MaxValue ? int.MaxValue : Math.Max(0, (int)elapsedMs);

    private static string NormalizeSnippet(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : string.Join(' ', value.Split(default(char[]), StringSplitOptions.RemoveEmptyEntries));

    private static string Truncate(string? value, int maxLength)
        => string.IsNullOrEmpty(value) || value.Length <= maxLength ? value ?? string.Empty : value[..maxLength];

    private sealed record EmbeddingParseResult(EmbeddingResult Result, bool Success, string? Error, string? RequestId, LlmUsageTokens Tokens);
}
