using Microsoft.Extensions.Logging;
using TrendReporter2.Core.Configuration;
using TrendReporter2.Core.Content;
using TrendReporter2.Core.Embeddings;

namespace TrendReporter2.Core.Events;

public sealed class VectorEventCandidateService
{
    private readonly AppConfig _config;
    private readonly IEmbeddingRepository _embeddingRepository;

    public VectorEventCandidateService(AppConfig config, IEmbeddingRepository embeddingRepository)
    {
        _config = config;
        _embeddingRepository = embeddingRepository;
    }

    public async Task<IReadOnlyList<EventCandidate>> RecallAsync(ContentItem item, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var embeddingConfig = _config.Llm.Embedding;
        if (string.IsNullOrWhiteSpace(embeddingConfig.Model))
        {
            return [];
        }

        var contentEmbedding = await _embeddingRepository.GetContentEmbeddingAsync(
            item.Id,
            embeddingConfig.Model,
            embeddingConfig.Version,
            embeddingConfig.Dimensions,
            cancellationToken);
        if (contentEmbedding is null)
        {
            return [];
        }

        var similarEvents = await _embeddingRepository.QuerySimilarEventsAsync(
            contentEmbedding.Embedding,
            embeddingConfig.Model,
            embeddingConfig.Version,
            embeddingConfig.Dimensions,
            now,
            _config.Analysis.HistoryHours,
            _config.Analysis.Event.ArchiveRecallDays,
            embeddingConfig.VectorSimilarityThreshold,
            Math.Max(1, embeddingConfig.VectorCandidateLimit),
            cancellationToken);

        return similarEvents
            .Select(candidate => new EventCandidate(
                candidate.Event,
                Math.Round(candidate.Similarity, 4),
                ["vector_cosine_similarity", candidate.Reason]))
            .OrderByDescending(candidate => candidate.Score)
            .ThenByDescending(candidate => candidate.Event.LastSeenAt)
            .ThenBy(candidate => candidate.Event.Id, StringComparer.Ordinal)
            .ToList();
    }
}

public sealed class CompositeEventCandidateService : IEventCandidateService
{
    private readonly AppConfig _config;
    private readonly EventCandidateService _ruleCandidateService;
    private readonly VectorEventCandidateService _vectorCandidateService;
    private readonly ILogger _logger;

    public CompositeEventCandidateService(
        AppConfig config,
        EventCandidateService ruleCandidateService,
        VectorEventCandidateService vectorCandidateService,
        ILoggerFactory loggerFactory)
    {
        _config = config;
        _ruleCandidateService = ruleCandidateService;
        _vectorCandidateService = vectorCandidateService;
        _logger = loggerFactory.CreateLogger("EventCandidate.Composite");
    }

    public async Task<IReadOnlyList<EventCandidate>> RecallAsync(ContentItem item, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var ruleCandidates = await _ruleCandidateService.RecallAsync(item, now, cancellationToken);
        IReadOnlyList<EventCandidate> vectorCandidates = [];
        try
        {
            vectorCandidates = await _vectorCandidateService.RecallAsync(item, now, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "向量召回失败，内容条目编号={ContentItemId}；将仅使用规则召回。", item.Id);
        }

        return ruleCandidates
            .Concat(vectorCandidates)
            .GroupBy(candidate => candidate.Event.Id, StringComparer.Ordinal)
            .Select(group => Merge(group))
            .OrderByDescending(candidate => candidate.Score)
            .ThenByDescending(candidate => candidate.Event.LastSeenAt)
            .ThenBy(candidate => candidate.Event.Id, StringComparer.Ordinal)
            .Take(Math.Max(1, _config.Analysis.Event.CandidateLimit))
            .ToList();
    }

    private static EventCandidate Merge(IEnumerable<EventCandidate> candidates)
    {
        var list = candidates.ToList();
        var best = list.OrderByDescending(candidate => candidate.Score).First();
        var features = list
            .SelectMany(candidate => candidate.MatchedFeatures)
            .Where(feature => !string.IsNullOrWhiteSpace(feature))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new EventCandidate(best.Event, list.Max(candidate => candidate.Score), features);
    }
}
