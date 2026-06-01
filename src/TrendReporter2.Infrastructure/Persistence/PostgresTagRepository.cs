using System.Security.Cryptography;
using System.Text;
using Dapper;
using Npgsql;
using TrendReporter2.Core.Content;
using TrendReporter2.Core.Tags;

namespace TrendReporter2.Infrastructure.Persistence;

public sealed class PostgresTagRepository : ITagRepository
{
    private readonly NpgsqlDataSource _dataSource;

    static PostgresTagRepository()
    {
        DefaultTypeMap.MatchNamesWithUnderscores = true;
    }

    public PostgresTagRepository(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task UpsertContentTagsAsync(string contentItemId, IReadOnlyList<TagAssignment> tags, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (tags.Count == 0)
        {
            return;
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var tag in tags)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await UpsertTagAsync(connection, transaction, tag, now, cancellationToken);
            await connection.ExecuteAsync(new CommandDefinition("""
            insert into content_item_tag (content_item_id, tag_id, confidence, source, created_at)
            values (@ContentItemId, @TagId, @Confidence, @Source, @CreatedAt)
            on conflict (content_item_id, tag_id) do update
            set confidence = greatest(content_item_tag.confidence, excluded.confidence),
                source = case when content_item_tag.source = 'web_extract' then content_item_tag.source else excluded.source end;
            """, new
            {
                ContentItemId = contentItemId,
                TagId = BuildTagId(tag.Name),
                tag.Confidence,
                tag.Source,
                CreatedAt = PostgresTimestamp.ToUtc(now)
            }, transaction, cancellationToken: cancellationToken));
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ContentItem>> LoadRunContentItemsWithoutTagsAsync(string runId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<ContentItemRow>(new CommandDefinition("""
        select ci.*
        from content_item ci
        where ci.last_seen_run_id = @RunId
          and not exists (
              select 1 from content_item_tag cit where cit.content_item_id = ci.id
          )
        order by ci.last_seen_rank, ci.source, ci.title;
        """, new { RunId = runId }, cancellationToken: cancellationToken));
        return rows.Select(ToContentItem).ToList();
    }

    public async Task RefreshEventTagsForRunAsync(string runId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition("""
        insert into event_tag (event_id, tag_id, confidence, source, created_at)
        select ei.event_id,
               cit.tag_id,
               max(cit.confidence) as confidence,
               case
                   when bool_or(cit.source = 'web_extract') then 'web_extract'
                   when bool_or(cit.source = 'llm') then 'llm'
                   else 'rule'
               end as source,
               @CreatedAt as created_at
        from event_item ei
        join content_item ci on ci.id = ei.content_item_id
        join content_item_tag cit on cit.content_item_id = ci.id
        where ci.last_seen_run_id = @RunId
        group by ei.event_id, cit.tag_id
        on conflict (event_id, tag_id) do update
        set confidence = greatest(event_tag.confidence, excluded.confidence),
            source = case
                    when event_tag.source in ('manual', 'web_extract') then event_tag.source
                    when event_tag.source = 'llm' and excluded.source = 'rule' then event_tag.source
                    else excluded.source
                end;
        """, new { RunId = runId, CreatedAt = PostgresTimestamp.ToUtc(now) }, cancellationToken: cancellationToken));
    }

    public async Task UpsertEventTagsAsync(string eventId, IReadOnlyList<TagAssignment> tags, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (tags.Count == 0)
        {
            return;
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var tag in tags)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await UpsertTagAsync(connection, transaction, tag, now, cancellationToken);
            await connection.ExecuteAsync(new CommandDefinition("""
            insert into event_tag (event_id, tag_id, confidence, source, created_at)
            values (@EventId, @TagId, @Confidence, @Source, @CreatedAt)
            on conflict (event_id, tag_id) do update
            set confidence = greatest(event_tag.confidence, excluded.confidence),
                source = case
                    when event_tag.source in ('manual', 'web_extract') then event_tag.source
                    when event_tag.source = 'llm' and excluded.source = 'rule' then event_tag.source
                    else excluded.source
                end;
            """, new
            {
                EventId = eventId,
                TagId = BuildTagId(tag.Name),
                tag.Confidence,
                tag.Source,
                CreatedAt = PostgresTimestamp.ToUtc(now)
            }, transaction, cancellationToken: cancellationToken));
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<EventTag>> LoadEventTagsAsync(IReadOnlyList<string> eventIds, CancellationToken cancellationToken)
    {
        if (eventIds.Count == 0)
        {
            return [];
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<EventTagRow>(new CommandDefinition("""
        select et.event_id, et.confidence, et.source, et.created_at, t.id as tag_id, t.name, t.display_name, t.category, t.created_at as tag_created_at
        from event_tag et
        join tag t on t.id = et.tag_id
        where et.event_id = any(@EventIds)
        order by et.confidence desc, t.display_name;
        """, new { EventIds = eventIds.ToArray() }, cancellationToken: cancellationToken));
        return rows.Select(ToEventTag).ToList();
    }

    public async Task<IReadOnlyList<string>> LoadEventIdsByTagAsync(string tagName, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<string>(new CommandDefinition("""
        select et.event_id
        from event_tag et
        join tag t on t.id = et.tag_id
        where t.name = @TagName
        order by et.confidence desc, et.event_id;
        """, new { TagName = tagName }, cancellationToken: cancellationToken));
        return rows.ToList();
    }

    private static Task UpsertTagAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, TagAssignment tag, DateTimeOffset now, CancellationToken cancellationToken)
        => connection.ExecuteAsync(new CommandDefinition("""
        insert into tag (id, name, display_name, category, created_at)
        values (@Id, @Name, @DisplayName, @Category, @CreatedAt)
        on conflict (name) do update
        set display_name = excluded.display_name,
            category = excluded.category;
        """, new
        {
            Id = BuildTagId(tag.Name),
            tag.Name,
            tag.DisplayName,
            tag.Category,
            CreatedAt = PostgresTimestamp.ToUtc(now)
        }, transaction, cancellationToken: cancellationToken));

    private static EventTag ToEventTag(EventTagRow row)
        => new(row.EventId, new Tag
        {
            Id = row.TagId,
            Name = row.Name,
            DisplayName = row.DisplayName,
            Category = row.Category,
            CreatedAt = row.TagCreatedAt
        }, row.Confidence, row.Source, row.CreatedAt);

    private static ContentItem ToContentItem(ContentItemRow row)
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

    private static string BuildTagId(string name)
        => "tag:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(name.Trim().ToLowerInvariant())))[..24].ToLowerInvariant();

    private sealed class EventTagRow
    {
        public string EventId { get; set; } = string.Empty;
        public string TagId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public double Confidence { get; set; }
        public string Source { get; set; } = string.Empty;
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset TagCreatedAt { get; set; }
    }

    private sealed class ContentItemRow
    {
        public string Id { get; set; } = string.Empty;
        public string DedupKey { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
        public string? SourceId { get; set; }
        public string Category { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public string ContentKind { get; set; } = string.Empty;
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
}
