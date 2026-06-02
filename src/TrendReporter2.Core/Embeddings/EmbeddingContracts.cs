using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using TrendReporter2.Core.Configuration;
using TrendReporter2.Core.Content;
using TrendReporter2.Core.Events;
using TrendReporter2.Core.Tags;

namespace TrendReporter2.Core.Embeddings;

public interface IEmbeddingClient
{
    bool IsConfigured { get; }

    Task<EmbeddingResult> EmbedAsync(EmbeddingRequest request, CancellationToken cancellationToken);
}

public interface IEmbeddingRepository
{
    Task<IReadOnlyList<ContentEmbeddingInput>> LoadRunContentEmbeddingInputsAsync(string runId, string model, string version, int dimensions, int limit, CancellationToken cancellationToken);

    Task<IReadOnlyList<EventEmbeddingInput>> LoadRunEventEmbeddingInputsAsync(string runId, string model, string version, int dimensions, int limit, CancellationToken cancellationToken);

    Task UpsertContentEmbeddingAsync(ContentEmbeddingRecord embedding, CancellationToken cancellationToken);

    Task UpsertEventEmbeddingAsync(EventEmbeddingRecord embedding, CancellationToken cancellationToken);

    Task<ContentEmbeddingRecord?> GetContentEmbeddingAsync(string contentItemId, string model, string version, int dimensions, CancellationToken cancellationToken);

    Task<EventEmbeddingRecord?> GetEventEmbeddingAsync(string eventId, string model, string version, int dimensions, CancellationToken cancellationToken)
        => Task.FromResult<EventEmbeddingRecord?>(null);

    Task<IReadOnlyList<VectorEventCandidate>> QuerySimilarEventsAsync(float[] embedding, string model, string version, int dimensions, DateTimeOffset now, int historyHours, int archiveRecallDays, double threshold, int limit, CancellationToken cancellationToken);
}

public interface IEmbeddingService
{
    Task<EmbeddingRunResult> GenerateContentEmbeddingsAsync(string runId, DateTimeOffset now, int maxRequests, CancellationToken cancellationToken);

    Task<EmbeddingRunResult> GenerateEventEmbeddingsAsync(string runId, DateTimeOffset now, int maxRequests, CancellationToken cancellationToken);
}

public sealed record EmbeddingRequest(string? RunId, string? ContentItemId, string? EventId, string Text);

public sealed record EmbeddingResult(bool Success, float[] Embedding, int? InputTokens, string? RequestId, string? Error)
{
    public static EmbeddingResult Failed(string error) => new(false, [], null, null, error);
}

public sealed record ContentEmbeddingInput(ContentItem ContentItem, string SourceText, string SourceTextHash);

public sealed record EventEmbeddingInput(EventAggregate Event, IReadOnlyList<EventTag> Tags, string SourceText, string SourceTextHash);

public sealed record ContentEmbeddingRecord(string ContentItemId, string Model, string Version, int Dimensions, string SourceTextHash, float[] Embedding, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record EventEmbeddingRecord(string EventId, string Model, string Version, int Dimensions, string SourceTextHash, float[] Embedding, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record VectorEventCandidate(EventAggregate Event, double Similarity, string Reason);

public sealed record EmbeddingRunResult(int CandidateCount, int GeneratedCount, int SkippedCount, int FailedCount);

public static class EmbeddingTextBuilder
{
    public static string BuildContentText(ContentItem item)
        => JoinParts(item.Title, item.Summary);

    public static string BuildEventText(EventAggregate eventAggregate, IReadOnlyList<EventTag> tags)
        => JoinParts(
            eventAggregate.CanonicalTitle,
            eventAggregate.Summary,
            string.Join(' ', eventAggregate.RepresentativeTitles),
            string.Join(' ', eventAggregate.KeyTerms),
            string.Join(' ', eventAggregate.Aliases),
            string.Join(' ', eventAggregate.Entities),
            string.Join(' ', tags.Select(tag => tag.Tag.DisplayName)));

    public static string HashSourceText(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(text)))).ToLowerInvariant();

    private static string JoinParts(params string?[] parts)
        => Normalize(string.Join('\n', parts.Where(part => !string.IsNullOrWhiteSpace(part)).Select(part => part!.Trim())));

    private static string Normalize(string text)
        => string.Join('\n', text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0));
}

public sealed class EmbeddingService : IEmbeddingService
{
    private readonly AppConfig _config;
    private readonly IEmbeddingClient _client;
    private readonly IEmbeddingRepository _repository;
    private readonly ILogger _logger;

    public EmbeddingService(AppConfig config, IEmbeddingClient client, IEmbeddingRepository repository, ILoggerFactory loggerFactory)
    {
        _config = config;
        _client = client;
        _repository = repository;
        _logger = loggerFactory.CreateLogger("EmbeddingService");
    }

    public Task<EmbeddingRunResult> GenerateContentEmbeddingsAsync(string runId, DateTimeOffset now, int maxRequests, CancellationToken cancellationToken)
        => GenerateAsync(
            runId,
            now,
            maxRequests,
            loadInputs: limit => _repository.LoadRunContentEmbeddingInputsAsync(runId, _config.Llm.Embedding.Model, _config.Llm.Embedding.Version, _config.Llm.Embedding.Dimensions, limit, cancellationToken),
            embedOne: input => EmbedContentAsync(runId, input, now, cancellationToken),
            cancellationToken);

    public Task<EmbeddingRunResult> GenerateEventEmbeddingsAsync(string runId, DateTimeOffset now, int maxRequests, CancellationToken cancellationToken)
        => GenerateAsync(
            runId,
            now,
            maxRequests,
            loadInputs: limit => _repository.LoadRunEventEmbeddingInputsAsync(runId, _config.Llm.Embedding.Model, _config.Llm.Embedding.Version, _config.Llm.Embedding.Dimensions, limit, cancellationToken),
            embedOne: input => EmbedEventAsync(runId, input, now, cancellationToken),
            cancellationToken);

    private async Task<EmbeddingRunResult> GenerateAsync<TInput>(
        string runId,
        DateTimeOffset now,
        int maxRequests,
        Func<int, Task<IReadOnlyList<TInput>>> loadInputs,
        Func<TInput, Task<bool>> embedOne,
        CancellationToken cancellationToken)
    {
        if (!_client.IsConfigured || maxRequests <= 0)
        {
            return new EmbeddingRunResult(0, 0, 0, 0);
        }

        var limit = Math.Max(0, maxRequests);
        var inputs = await loadInputs(limit);
        var generated = 0;
        var failed = 0;
        using var semaphore = new SemaphoreSlim(Math.Max(1, _config.System.MaxParallelLlm));
        var tasks = inputs.Select(async input =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                if (await embedOne(input))
                {
                    Interlocked.Increment(ref generated);
                }
                else
                {
                    Interlocked.Increment(ref failed);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "embedding 生成失败，运行编号={RunId}；抓取流程将继续。", runId);
                Interlocked.Increment(ref failed);
            }
            finally
            {
                semaphore.Release();
            }
        });

        await Task.WhenAll(tasks);
        return new EmbeddingRunResult(inputs.Count, generated, 0, failed);
    }

    private async Task<bool> EmbedContentAsync(string runId, ContentEmbeddingInput input, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var result = await _client.EmbedAsync(new EmbeddingRequest(runId, input.ContentItem.Id, null, input.SourceText), cancellationToken);
        if (!result.Success)
        {
            _logger.LogWarning("内容 embedding 生成失败，内容条目编号={ContentItemId}，错误={Error}。", input.ContentItem.Id, result.Error);
            return false;
        }

        await _repository.UpsertContentEmbeddingAsync(new ContentEmbeddingRecord(input.ContentItem.Id, _config.Llm.Embedding.Model, _config.Llm.Embedding.Version, _config.Llm.Embedding.Dimensions, input.SourceTextHash, result.Embedding, now, now), cancellationToken);
        return true;
    }

    private async Task<bool> EmbedEventAsync(string runId, EventEmbeddingInput input, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var result = await _client.EmbedAsync(new EmbeddingRequest(runId, null, input.Event.Id, input.SourceText), cancellationToken);
        if (!result.Success)
        {
            _logger.LogWarning("事件 embedding 生成失败，事件编号={EventId}，错误={Error}。", input.Event.Id, result.Error);
            return false;
        }

        await _repository.UpsertEventEmbeddingAsync(new EventEmbeddingRecord(input.Event.Id, _config.Llm.Embedding.Model, _config.Llm.Embedding.Version, _config.Llm.Embedding.Dimensions, input.SourceTextHash, result.Embedding, now, now), cancellationToken);
        return true;
    }
}
