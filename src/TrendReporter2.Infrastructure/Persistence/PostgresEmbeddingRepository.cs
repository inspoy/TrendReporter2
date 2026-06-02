using System.Globalization;
using Dapper;
using Npgsql;
using TrendReporter2.Core.Content;
using TrendReporter2.Core.Embeddings;
using TrendReporter2.Core.Events;
using TrendReporter2.Core.Tags;

namespace TrendReporter2.Infrastructure.Persistence;

public sealed class PostgresEmbeddingRepository : IEmbeddingRepository
{
    private readonly NpgsqlDataSource _dataSource;

    static PostgresEmbeddingRepository()
    {
        DefaultTypeMap.MatchNamesWithUnderscores = true;
    }

    public PostgresEmbeddingRepository(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task<IReadOnlyList<ContentEmbeddingInput>> LoadRunContentEmbeddingInputsAsync(string runId, string model, string version, int dimensions, int limit, CancellationToken cancellationToken)
    {
        if (limit <= 0 || string.IsNullOrWhiteSpace(model))
        {
            return [];
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<ContentEmbeddingInputRow>(new CommandDefinition("""
        select ci.*, ce.source_text_hash as ExistingSourceTextHash
        from content_item ci
        left join content_embedding ce on ce.content_item_id = ci.id
            and ce.model = @Model
            and ce.version = @Version
            and ce.dimensions = @Dimensions
        where ci.last_seen_run_id = @RunId
        order by ci.last_seen_rank, ci.source, ci.title;
        """, new { RunId = runId, Model = model, Version = version, Dimensions = dimensions }, cancellationToken: cancellationToken));

        return rows
            .Select(row => new { Item = ToContentItem(row), Text = EmbeddingTextBuilder.BuildContentText(ToContentItem(row)), row.ExistingSourceTextHash })
            .Where(row => !string.IsNullOrWhiteSpace(row.Text))
            .Select(row => new ContentEmbeddingInput(row.Item, row.Text, EmbeddingTextBuilder.HashSourceText(row.Text)) { })
            .Where(input => rows.First(row => row.Id == input.ContentItem.Id).ExistingSourceTextHash != input.SourceTextHash)
            .Take(limit)
            .ToList();
    }

    public async Task<IReadOnlyList<EventEmbeddingInput>> LoadRunEventEmbeddingInputsAsync(string runId, string model, string version, int dimensions, int limit, CancellationToken cancellationToken)
    {
        if (limit <= 0 || string.IsNullOrWhiteSpace(model))
        {
            return [];
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        var rows = (await connection.QueryAsync<EventEmbeddingInputRow>(new CommandDefinition("""
        select
            e.*,
            ee.source_text_hash as ExistingSourceTextHash,
            et.confidence as TagConfidence,
            et.source as TagSource,
            et.created_at as TagCreatedAt,
            t.id as TagId,
            t.name as TagName,
            t.display_name as TagDisplayName,
            t.category as TagCategory,
            t.created_at as TagRecordCreatedAt
        from event e
        join event_item ei on ei.event_id = e.id
        join content_item ci on ci.id = ei.content_item_id
        left join event_embedding ee on ee.event_id = e.id
            and ee.model = @Model
            and ee.version = @Version
            and ee.dimensions = @Dimensions
        left join event_tag et on et.event_id = e.id
        left join tag t on t.id = et.tag_id
        where ci.last_seen_run_id = @RunId
        order by e.last_seen_at desc, e.id, et.confidence desc nulls last;
        """, new { RunId = runId, Model = model, Version = version, Dimensions = dimensions }, cancellationToken: cancellationToken))).ToList();

        return rows
            .GroupBy(row => row.Id, StringComparer.Ordinal)
            .Select(group =>
            {
                var first = group.First();
                var eventAggregate = ToEventAggregate(first);
                var tags = group.Where(row => !string.IsNullOrWhiteSpace(row.TagId)).Select(ToEventTag).ToList();
                var text = EmbeddingTextBuilder.BuildEventText(eventAggregate, tags);
                return new { eventAggregate, tags, text, hash = EmbeddingTextBuilder.HashSourceText(text), first.ExistingSourceTextHash };
            })
            .Where(input => !string.IsNullOrWhiteSpace(input.text) && input.ExistingSourceTextHash != input.hash)
            .Select(input => new EventEmbeddingInput(input.eventAggregate, input.tags, input.text, input.hash))
            .Take(limit)
            .ToList();
    }

    public async Task UpsertContentEmbeddingAsync(ContentEmbeddingRecord embedding, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition("""
        insert into content_embedding (content_item_id, model, version, dimensions, source_text_hash, embedding, created_at, updated_at)
        values (@ContentItemId, @Model, @Version, @Dimensions, @SourceTextHash, @Embedding::vector, @CreatedAt, @UpdatedAt)
        on conflict (content_item_id, model, version, dimensions) do update
        set source_text_hash = excluded.source_text_hash,
            embedding = excluded.embedding,
            updated_at = excluded.updated_at;
        """, ToParameters(embedding), cancellationToken: cancellationToken));
    }

    public async Task UpsertEventEmbeddingAsync(EventEmbeddingRecord embedding, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition("""
        insert into event_embedding (event_id, model, version, dimensions, source_text_hash, embedding, created_at, updated_at)
        values (@EventId, @Model, @Version, @Dimensions, @SourceTextHash, @Embedding::vector, @CreatedAt, @UpdatedAt)
        on conflict (event_id, model, version, dimensions) do update
        set source_text_hash = excluded.source_text_hash,
            embedding = excluded.embedding,
            updated_at = excluded.updated_at;
        """, ToParameters(embedding), cancellationToken: cancellationToken));
    }

    public async Task<ContentEmbeddingRecord?> GetContentEmbeddingAsync(string contentItemId, string model, string version, int dimensions, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<EmbeddingRecordRow>(new CommandDefinition("""
        select content_item_id as OwnerId, model, version, dimensions, source_text_hash, embedding::text as EmbeddingText, created_at, updated_at
        from content_embedding
        where content_item_id = @ContentItemId
          and model = @Model
          and version = @Version
          and dimensions = @Dimensions;
        """, new { ContentItemId = contentItemId, Model = model, Version = version, Dimensions = dimensions }, cancellationToken: cancellationToken));
        return row is null
            ? null
            : new ContentEmbeddingRecord(row.OwnerId, row.Model, row.Version, row.Dimensions, row.SourceTextHash, ParseVector(row.EmbeddingText), row.CreatedAt, row.UpdatedAt);
    }

    public async Task<IReadOnlyList<VectorEventCandidate>> QuerySimilarEventsAsync(float[] embedding, string model, string version, int dimensions, DateTimeOffset now, int historyHours, int archiveRecallDays, double threshold, int limit, CancellationToken cancellationToken)
    {
        if (embedding.Length == 0 || limit <= 0)
        {
            return [];
        }

        var activeCutoff = now.AddHours(-Math.Max(1, historyHours));
        var staleCutoff = now.AddDays(-Math.Max(1, archiveRecallDays));
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<SimilarEventRow>(new CommandDefinition("""
        select e.*, (1 - (ee.embedding <=> @Embedding::vector)) as Similarity
        from event_embedding ee
        join event e on e.id = ee.event_id
        where ee.model = @Model
          and ee.version = @Version
          and ee.dimensions = @Dimensions
          and e.is_blacklisted = false
          and (
              (e.status = @Active and e.last_seen_at >= @ActiveCutoff)
              or (e.status = @Stale and e.last_seen_at >= @StaleCutoff)
          )
          and (1 - (ee.embedding <=> @Embedding::vector)) >= @Threshold
        order by ee.embedding <=> @Embedding::vector, e.last_seen_at desc, e.id
        limit @Limit;
        """, new
        {
            Embedding = ToVectorLiteral(embedding),
            Model = model,
            Version = version,
            Dimensions = dimensions,
            Active = EventStatus.Active,
            Stale = EventStatus.Stale,
            ActiveCutoff = PostgresTimestamp.ToUtc(activeCutoff),
            StaleCutoff = PostgresTimestamp.ToUtc(staleCutoff),
            Threshold = threshold,
            Limit = limit
        }, cancellationToken: cancellationToken));

        return rows.Select(row => new VectorEventCandidate(ToEventAggregate(row), row.Similarity, $"cosine_similarity:{row.Similarity:F4}")).ToList();
    }

    private static object ToParameters(ContentEmbeddingRecord embedding)
        => new
        {
            embedding.ContentItemId,
            embedding.Model,
            embedding.Version,
            embedding.Dimensions,
            embedding.SourceTextHash,
            Embedding = ToVectorLiteral(embedding.Embedding),
            CreatedAt = PostgresTimestamp.ToUtc(embedding.CreatedAt),
            UpdatedAt = PostgresTimestamp.ToUtc(embedding.UpdatedAt)
        };

    private static object ToParameters(EventEmbeddingRecord embedding)
        => new
        {
            embedding.EventId,
            embedding.Model,
            embedding.Version,
            embedding.Dimensions,
            embedding.SourceTextHash,
            Embedding = ToVectorLiteral(embedding.Embedding),
            CreatedAt = PostgresTimestamp.ToUtc(embedding.CreatedAt),
            UpdatedAt = PostgresTimestamp.ToUtc(embedding.UpdatedAt)
        };

    private static string ToVectorLiteral(IReadOnlyList<float> embedding)
        => "[" + string.Join(',', embedding.Select(value => value.ToString("R", CultureInfo.InvariantCulture))) + "]";

    private static float[] ParseVector(string value)
        => value.Trim('[', ']')
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => float.Parse(part, CultureInfo.InvariantCulture))
            .ToArray();

    private static ContentItem ToContentItem(ContentEmbeddingInputRow row)
        => new()
        {
            Id = row.Id,
            DedupKey = row.DedupKey,
            Source = row.Source,
            SourceId = row.SourceId,
            Category = row.Category,
            Type = row.Type,
            ContentKind = row.ContentKind,
            SourceItemId = row.SourceItemId,
            Title = row.Title,
            Url = row.Url,
            MobileUrl = row.MobileUrl,
            PubTime = row.PubTime,
            Summary = row.Summary,
            SummarySource = row.SummarySource,
            NeedEnrichment = row.NeedEnrichment,
            EnrichmentStatus = row.EnrichmentStatus,
            EnrichmentTriedAt = row.EnrichmentTriedAt,
            CreatedAt = row.CreatedAt,
            UpdatedAt = row.UpdatedAt,
            LastSeenRunId = row.LastSeenRunId,
            LastSeenAt = row.LastSeenAt,
            LastSeenRank = row.LastSeenRank,
            RawPayload = PostgresJson.EmptyObjectIfBlank(row.RawPayload)
        };

    private static EventAggregate ToEventAggregate(EventRow row)
        => new()
        {
            Id = row.Id,
            Type = row.Type,
            CanonicalTitle = row.CanonicalTitle,
            Summary = row.Summary,
            Aliases = PostgresJson.DeserializeList<string>(row.Aliases),
            Entities = PostgresJson.DeserializeList<string>(row.Entities),
            Places = PostgresJson.DeserializeList<string>(row.Places),
            KeyTerms = PostgresJson.DeserializeList<string>(row.KeyTerms),
            RepresentativeTitles = PostgresJson.DeserializeList<string>(row.RepresentativeTitles),
            CurrentStage = row.CurrentStage,
            ProgressSummary = row.ProgressSummary,
            Milestones = PostgresJson.DeserializeList<EventMilestone>(row.Milestones),
            ProgressUpdatedAt = row.ProgressUpdatedAt,
            Status = row.Status,
            FirstSeenAt = row.FirstSeenAt,
            LastSeenAt = row.LastSeenAt,
            LastActivatedAt = row.LastActivatedAt,
            LastPushedAt = row.LastPushedAt,
            PushCount = row.PushCount,
            LastPushScore = row.LastPushScore,
            LastPushRankScore = row.LastPushRankScore,
            LastPushSourceCount = row.LastPushSourceCount,
            IsBlacklisted = row.IsBlacklisted,
            BlacklistReason = row.BlacklistReason,
            CreatedAt = row.CreatedAt,
            UpdatedAt = row.UpdatedAt
        };

    private static EventTag ToEventTag(EventEmbeddingInputRow row)
        => new(row.Id, new Tag
        {
            Id = row.TagId ?? string.Empty,
            Name = row.TagName ?? string.Empty,
            DisplayName = row.TagDisplayName ?? string.Empty,
            Category = row.TagCategory ?? TagCategories.Topic,
            CreatedAt = row.TagRecordCreatedAt ?? DateTimeOffset.MinValue
        }, row.TagConfidence ?? 0, row.TagSource ?? TagSources.Rule, row.TagCreatedAt ?? DateTimeOffset.MinValue);

    private class EventRow
    {
        public string Id { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public string CanonicalTitle { get; set; } = string.Empty;
        public string Summary { get; set; } = string.Empty;
        public string? Aliases { get; set; }
        public string? Entities { get; set; }
        public string? Places { get; set; }
        public string? KeyTerms { get; set; }
        public string? RepresentativeTitles { get; set; }
        public string? CurrentStage { get; set; }
        public string? ProgressSummary { get; set; }
        public string? Milestones { get; set; }
        public DateTimeOffset? ProgressUpdatedAt { get; set; }
        public string Status { get; set; } = string.Empty;
        public DateTimeOffset FirstSeenAt { get; set; }
        public DateTimeOffset LastSeenAt { get; set; }
        public DateTimeOffset LastActivatedAt { get; set; }
        public DateTimeOffset? LastPushedAt { get; set; }
        public int PushCount { get; set; }
        public double? LastPushScore { get; set; }
        public double? LastPushRankScore { get; set; }
        public int? LastPushSourceCount { get; set; }
        public bool IsBlacklisted { get; set; }
        public string? BlacklistReason { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
    }

    private sealed class ContentEmbeddingInputRow : ContentItemRow
    {
        public string? ExistingSourceTextHash { get; set; }
    }

    private class ContentItemRow
    {
        public string Id { get; set; } = string.Empty;
        public string DedupKey { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
        public string? SourceId { get; set; }
        public string Category { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public string ContentKind { get; set; } = "ranked_news";
        public string SourceItemId { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public string? MobileUrl { get; set; }
        public DateTimeOffset? PubTime { get; set; }
        public string? Summary { get; set; }
        public string? SummarySource { get; set; }
        public bool NeedEnrichment { get; set; }
        public string EnrichmentStatus { get; set; } = string.Empty;
        public DateTimeOffset? EnrichmentTriedAt { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
        public string? LastSeenRunId { get; set; }
        public DateTimeOffset? LastSeenAt { get; set; }
        public int LastSeenRank { get; set; }
        public string? RawPayload { get; set; }
    }

    private sealed class EventEmbeddingInputRow : EventRow
    {
        public string? ExistingSourceTextHash { get; set; }
        public double? TagConfidence { get; set; }
        public string? TagSource { get; set; }
        public DateTimeOffset? TagCreatedAt { get; set; }
        public string? TagId { get; set; }
        public string? TagName { get; set; }
        public string? TagDisplayName { get; set; }
        public string? TagCategory { get; set; }
        public DateTimeOffset? TagRecordCreatedAt { get; set; }
    }

    private sealed class SimilarEventRow : EventRow
    {
        public double Similarity { get; set; }
    }

    private sealed class EmbeddingRecordRow
    {
        public string OwnerId { get; set; } = string.Empty;
        public string Model { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public int Dimensions { get; set; }
        public string SourceTextHash { get; set; } = string.Empty;
        public string EmbeddingText { get; set; } = string.Empty;
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
    }
}
