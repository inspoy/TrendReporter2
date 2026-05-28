using System.Security.Cryptography;
using System.Text;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;
using TrendReporter2.Core.Content;
using TrendReporter2.Core.Enrichment;
using TrendReporter2.Core.News;

namespace TrendReporter2.Infrastructure.Persistence;

public sealed class PostgresContentRepository : IContentIngestService
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly IEnrichmentPolicy _enrichmentPolicy;
    private readonly ILogger _logger;

    static PostgresContentRepository()
    {
        DefaultTypeMap.MatchNamesWithUnderscores = true;
    }

    public PostgresContentRepository(
        NpgsqlDataSource dataSource,
        IEnrichmentPolicy enrichmentPolicy,
        ILoggerFactory loggerFactory)
    {
        _dataSource = dataSource;
        _enrichmentPolicy = enrichmentPolicy;
        _logger = loggerFactory.CreateLogger("ContentIngest");
    }

    public async Task<ContentIngestResult> IngestAsync(
        string runId,
        IReadOnlyList<NewsItem> items,
        DateTimeOffset capturedAt,
        CancellationToken cancellationToken)
    {
        var inserted = 0;
        var updated = 0;
        var snapshotCount = 0;
        var visualOrder = 0;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        foreach (var item in OrderForDisplay(items))
        {
            cancellationToken.ThrowIfCancellationRequested();
            visualOrder++;

            var dedupKey = BuildDedupKey(item.Source, item.SourceItemId);
            var existing = await LoadByDedupKeyAsync(connection, dedupKey, transaction, cancellationToken);
            ContentItem persisted;

            if (existing is null)
            {
                var needEnrichment = _enrichmentPolicy.NeedEnrichment(item);
                var summary = BuildPreferredSummary(item);
                persisted = new ContentItem
                {
                    Id = BuildContentItemId(item),
                    DedupKey = dedupKey,
                    Source = item.Source,
                    Category = item.Category,
                    SourceItemId = item.SourceItemId,
                    Title = item.Title,
                    Url = item.Url,
                    MobileUrl = item.MobileUrl,
                    PubTime = item.PubTime,
                    HoverText = item.HoverText,
                    Summary = summary.Value,
                    SummarySource = summary.Source,
                    NeedEnrichment = needEnrichment,
                    EnrichmentStatus = needEnrichment ? EnrichmentStatuses.Pending : EnrichmentStatuses.Skipped,
                    CreatedAt = capturedAt,
                    UpdatedAt = capturedAt,
                    LastSeenRunId = runId,
                    LastSeenAt = capturedAt,
                    LastSeenRank = item.Rank,
                    RawPayload = PostgresJson.EmptyObjectIfBlank(item.RawPayload)
                };
                await UpsertContentItemAsync(connection, persisted, transaction, cancellationToken);
                inserted++;
            }
            else
            {
                existing.Category = item.Category;
                existing.Title = item.Title;
                existing.Url = item.Url;
                existing.MobileUrl = item.MobileUrl;
                existing.PubTime = item.PubTime;
                existing.HoverText = item.HoverText;
                existing.NeedEnrichment = _enrichmentPolicy.NeedEnrichment(item);
                if (ShouldRefreshSourceSummary(existing.Summary, existing.SummarySource))
                {
                    var summary = BuildPreferredSummary(item);
                    existing.Summary = summary.Value;
                    existing.SummarySource = summary.Source;
                }

                if (existing.NeedEnrichment && !string.Equals(existing.EnrichmentStatus, EnrichmentStatuses.Succeeded, StringComparison.OrdinalIgnoreCase))
                {
                    existing.EnrichmentStatus = EnrichmentStatuses.Pending;
                }
                else if (!existing.NeedEnrichment && !string.Equals(existing.EnrichmentStatus, EnrichmentStatuses.Succeeded, StringComparison.OrdinalIgnoreCase))
                {
                    existing.EnrichmentStatus = EnrichmentStatuses.Skipped;
                }

                existing.UpdatedAt = capturedAt;
                existing.LastSeenRunId = runId;
                existing.LastSeenAt = capturedAt;
                existing.LastSeenRank = item.Rank;
                existing.RawPayload = PostgresJson.EmptyObjectIfBlank(item.RawPayload);
                await UpsertContentItemAsync(connection, existing, transaction, cancellationToken);
                persisted = existing;
                updated++;
            }

            var snapshot = new ContentSnapshot
            {
                Id = BuildSnapshotId(runId, visualOrder, item),
                RunId = runId,
                ContentItemId = persisted.Id,
                CapturedAt = capturedAt,
                Source = item.Source,
                Category = item.Category,
                VisualOrder = visualOrder,
                Rank = item.Rank,
                SourceListSize = item.SourceListSize,
                NormalizedRankScore = CalculateNormalizedRankScore(item.Rank, item.SourceListSize)
            };
            await InsertSnapshotAsync(connection, snapshot, transaction, cancellationToken);
            snapshotCount++;
        }

        await transaction.CommitAsync(cancellationToken);

        _logger.LogInformation(
            "已入库 {TotalCount} 条内容，运行编号={RunId}。新增={InsertedCount}，更新={UpdatedCount}，快照={SnapshotCount}。",
            items.Count,
            runId,
            inserted,
            updated,
            snapshotCount);

        return new ContentIngestResult(items.Count, inserted, updated, snapshotCount);
    }

    public async Task<IReadOnlyList<ContentItem>> LoadEnrichmentCandidatesAsync(
        string runId,
        DateTimeOffset cooldownCutoff,
        CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<ContentItemRow>(new CommandDefinition("""
        select *
        from content_item
        where last_seen_run_id = @RunId
          and need_enrichment = true
          and enrichment_status <> @Succeeded
          and (enrichment_tried_at is null or enrichment_tried_at <= @CooldownCutoff)
        order by last_seen_rank, source;
        """, new { RunId = runId, Succeeded = EnrichmentStatuses.Succeeded, CooldownCutoff = PostgresTimestamp.ToUtc(cooldownCutoff) }, cancellationToken: cancellationToken));
        return rows.Select(ToContentItem).ToList();
    }

    public async Task SaveAsync(ContentItem item, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await UpsertContentItemAsync(connection, item, transaction: null, cancellationToken);
    }

    private static async Task<ContentItem?> LoadByDedupKeyAsync(
        Npgsql.NpgsqlConnection connection,
        string dedupKey,
        Npgsql.NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        var row = await connection.QuerySingleOrDefaultAsync<ContentItemRow>(new CommandDefinition("""
        select * from content_item where dedup_key = @DedupKey;
        """, new { DedupKey = dedupKey }, transaction, cancellationToken: cancellationToken));
        return row is null ? null : ToContentItem(row);
    }

    private static Task UpsertContentItemAsync(
        Npgsql.NpgsqlConnection connection,
        ContentItem item,
        Npgsql.NpgsqlTransaction? transaction,
        CancellationToken cancellationToken)
        => connection.ExecuteAsync(new CommandDefinition("""
        insert into content_item (id, dedup_key, source, category, type, source_item_id, title, url, mobile_url, pub_time,
            hover_text, summary, summary_source, need_enrichment, enrichment_status, enrichment_tried_at, created_at,
            updated_at, last_seen_run_id, last_seen_at, last_seen_rank, raw_payload)
        values (@Id, @DedupKey, @Source, @Category, @Type, @SourceItemId, @Title, @Url, @MobileUrl, @PubTime,
            @HoverText, @Summary, @SummarySource, @NeedEnrichment, @EnrichmentStatus, @EnrichmentTriedAt, @CreatedAt,
            @UpdatedAt, @LastSeenRunId, @LastSeenAt, @LastSeenRank, @RawPayload::jsonb)
        on conflict (dedup_key) do update
        set source = excluded.source,
            category = excluded.category,
            type = excluded.type,
            source_item_id = excluded.source_item_id,
            title = excluded.title,
            url = excluded.url,
            mobile_url = excluded.mobile_url,
            pub_time = excluded.pub_time,
            hover_text = excluded.hover_text,
            summary = excluded.summary,
            summary_source = excluded.summary_source,
            need_enrichment = excluded.need_enrichment,
            enrichment_status = excluded.enrichment_status,
            enrichment_tried_at = excluded.enrichment_tried_at,
            updated_at = excluded.updated_at,
            last_seen_run_id = excluded.last_seen_run_id,
            last_seen_at = excluded.last_seen_at,
            last_seen_rank = excluded.last_seen_rank,
            raw_payload = excluded.raw_payload;
        """, ToParameters(item), transaction, cancellationToken: cancellationToken));

    private static Task InsertSnapshotAsync(
        Npgsql.NpgsqlConnection connection,
        ContentSnapshot snapshot,
        Npgsql.NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
        => connection.ExecuteAsync(new CommandDefinition("""
        insert into content_snapshot (id, run_id, content_item_id, captured_at, source, category, visual_order, rank, source_list_size, normalized_rank_score)
        values (@Id, @RunId, @ContentItemId, @CapturedAt, @Source, @Category, @VisualOrder, @Rank, @SourceListSize, @NormalizedRankScore)
        on conflict (run_id, content_item_id) do nothing;
        """, ToParameters(snapshot), transaction, cancellationToken: cancellationToken));

    private static object ToParameters(ContentItem item)
        => new
        {
            item.Id,
            item.DedupKey,
            item.Source,
            item.Category,
            item.Type,
            item.SourceItemId,
            item.Title,
            item.Url,
            item.MobileUrl,
            PubTime = PostgresTimestamp.ToUtc(item.PubTime),
            item.HoverText,
            item.Summary,
            item.SummarySource,
            item.NeedEnrichment,
            item.EnrichmentStatus,
            EnrichmentTriedAt = PostgresTimestamp.ToUtc(item.EnrichmentTriedAt),
            CreatedAt = PostgresTimestamp.ToUtc(item.CreatedAt),
            UpdatedAt = PostgresTimestamp.ToUtc(item.UpdatedAt),
            item.LastSeenRunId,
            LastSeenAt = PostgresTimestamp.ToUtc(item.LastSeenAt),
            item.LastSeenRank,
            RawPayload = PostgresJson.EmptyObjectIfBlank(item.RawPayload)
        };

    private static object ToParameters(ContentSnapshot snapshot)
        => new
        {
            snapshot.Id,
            snapshot.RunId,
            snapshot.ContentItemId,
            CapturedAt = PostgresTimestamp.ToUtc(snapshot.CapturedAt),
            snapshot.Source,
            snapshot.Category,
            snapshot.VisualOrder,
            snapshot.Rank,
            snapshot.SourceListSize,
            snapshot.NormalizedRankScore
        };

    private static ContentItem ToContentItem(ContentItemRow row)
        => new()
        {
            Id = row.Id,
            DedupKey = row.DedupKey,
            Source = row.Source,
            Category = row.Category,
            Type = row.Type,
            SourceItemId = row.SourceItemId,
            Title = row.Title,
            Url = row.Url,
            MobileUrl = row.MobileUrl,
            PubTime = row.PubTime,
            HoverText = row.HoverText,
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

    private static string BuildDedupKey(string source, string sourceItemId)
        => $"{source.Trim().ToLowerInvariant()}|{sourceItemId.Trim()}";

    private static IEnumerable<NewsItem> OrderForDisplay(IEnumerable<NewsItem> items)
        => items
            .OrderBy(item => item.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Source, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Rank)
            .ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase);

    private static bool ShouldRefreshSourceSummary(string? summary, string? source)
        => string.IsNullOrWhiteSpace(summary) ||
            string.Equals(source, SummarySources.TitleOnly, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(source, SummarySources.HoverText, StringComparison.OrdinalIgnoreCase);

    private static (string Value, string Source) BuildPreferredSummary(NewsItem item)
        => string.IsNullOrWhiteSpace(item.HoverText)
            ? (item.Title.Trim(), SummarySources.TitleOnly)
            : (item.HoverText.Trim(), SummarySources.HoverText);

    private static string BuildContentItemId(NewsItem item)
        => $"ci:{SafeIdPart(item.Category)}:{SafeIdPart(item.Source)}:{ShortHash(item.SourceItemId)}";

    private static string BuildSnapshotId(string runId, int visualOrder, NewsItem item)
        => $"{runId}:snap:{visualOrder:D5}:{SafeIdPart(item.Category)}:{SafeIdPart(item.Source)}:r{item.Rank:D4}";

    private static string SafeIdPart(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value.Trim().ToLowerInvariant())
        {
            builder.Append(char.IsLetterOrDigit(ch) ? ch : '-');
        }

        var result = builder.ToString().Trim('-');
        return string.IsNullOrWhiteSpace(result) ? "unknown" : result;
    }

    private static string ShortHash(string value)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(value.Trim()));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    private static double CalculateNormalizedRankScore(int rank, int sourceListSize)
    {
        if (sourceListSize <= 1)
        {
            return 1;
        }

        return Math.Clamp(1 - ((double)rank - 1) / (sourceListSize - 1), 0, 1);
    }

    private sealed class ContentItemRow
    {
        public string Id { get; set; } = "";
        public string DedupKey { get; set; } = "";
        public string Source { get; set; } = "";
        public string Category { get; set; } = "";
        public string Type { get; set; } = "";
        public string SourceItemId { get; set; } = "";
        public string Title { get; set; } = "";
        public string Url { get; set; } = "";
        public string? MobileUrl { get; set; }
        public DateTimeOffset? PubTime { get; set; }
        public string? HoverText { get; set; }
        public string? Summary { get; set; }
        public string? SummarySource { get; set; }
        public bool NeedEnrichment { get; set; }
        public string EnrichmentStatus { get; set; } = "";
        public DateTimeOffset? EnrichmentTriedAt { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
        public string? LastSeenRunId { get; set; }
        public DateTimeOffset? LastSeenAt { get; set; }
        public int LastSeenRank { get; set; }
        public string? RawPayload { get; set; }
    }
}
