using LiteDB;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using System.Text;
using TrendReporter2.Core.Content;
using TrendReporter2.Core.Enrichment;
using TrendReporter2.Core.Persistence;
using TrendReporter2.Core.Sources;

namespace TrendReporter2.Infrastructure.Persistence;

public sealed class ContentIngestService : IContentIngestService
{
    private readonly LiteDbConnectionFactory _connectionFactory;
    private readonly IEnrichmentPolicy _enrichmentPolicy;
    private readonly ILogger _logger;

    public ContentIngestService(
        LiteDbConnectionFactory connectionFactory,
        IEnrichmentPolicy enrichmentPolicy,
        ILoggerFactory loggerFactory)
    {
        _connectionFactory = connectionFactory;
        _enrichmentPolicy = enrichmentPolicy;
        _logger = loggerFactory.CreateLogger("ContentIngest");
    }

    public Task<ContentIngestResult> IngestAsync(
        string runId,
        IReadOnlyList<FetchedContentItem> items,
        DateTimeOffset capturedAt,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using var database = _connectionFactory.Open();
        var contentItems = database.GetCollection<ContentItem>(TrendCollectionNames.ContentItem);
        var snapshots = database.GetCollection<ContentSnapshot>(TrendCollectionNames.ContentSnapshot);

        var inserted = 0;
        var updated = 0;
        var snapshotCount = 0;
        var visualOrder = 0;

        foreach (var item in OrderForDisplay(items))
        {
            cancellationToken.ThrowIfCancellationRequested();
            visualOrder++;

            var dedupKey = BuildDedupKey(item);
            var existing = contentItems.FindOne(x => x.DedupKey == dedupKey);
            ContentItem persisted;

            if (existing is null)
            {
                var needEnrichment = _enrichmentPolicy.NeedEnrichment(item);
                var summary = BuildPreferredSummary(item);
                persisted = new ContentItem
                {
                    Id = BuildContentItemId(item),
                    DedupKey = dedupKey,
                    Source = item.SourceId,
                    SourceId = item.SourceId,
                    Category = item.Category,
                    ContentKind = item.ContentKind,
                    SourceItemId = item.SourceItemId,
                    Title = item.Title,
                    Url = item.Url,
                    MobileUrl = item.MobileUrl,
                    PubTime = item.PublishedAt,
                    HoverText = item.HoverText,
                    Summary = summary.Value,
                    SummarySource = summary.Source,
                    NeedEnrichment = needEnrichment,
                    EnrichmentStatus = needEnrichment ? EnrichmentStatuses.Pending : EnrichmentStatuses.Skipped,
                    CreatedAt = capturedAt,
                    UpdatedAt = capturedAt,
                    LastSeenRunId = runId,
                    LastSeenAt = capturedAt,
                    LastSeenRank = item.Rank ?? 0,
                    RawPayload = item.RawPayload
                };

                contentItems.Insert(persisted);
                inserted++;
            }
            else
            {
                existing.Category = item.Category;
                existing.Source = item.SourceId;
                existing.SourceId = item.SourceId;
                existing.ContentKind = item.ContentKind;
                existing.Title = item.Title;
                existing.Url = item.Url;
                existing.MobileUrl = item.MobileUrl;
                existing.PubTime = item.PublishedAt;
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
                existing.LastSeenRank = item.Rank ?? 0;
                existing.RawPayload = item.RawPayload;

                contentItems.Update(existing);
                persisted = existing;
                updated++;
            }

            snapshots.Insert(new ContentSnapshot
            {
                Id = BuildSnapshotId(runId, visualOrder, item),
                RunId = runId,
                ContentItemId = persisted.Id,
                CapturedAt = capturedAt,
                Source = item.SourceId,
                SourceId = item.SourceId,
                Category = item.Category,
                ContentKind = item.ContentKind,
                VisualOrder = visualOrder,
                Rank = item.Rank,
                SourceListSize = item.SourceListSize,
                NormalizedRankScore = PostgresContentRepository.CalculateNormalizedRankScore(item.Rank, item.SourceListSize),
                FreshnessScore = PostgresContentRepository.CalculateFreshnessScore(item.ContentKind, item.PublishedAt, capturedAt)
            });
            snapshotCount++;
        }

        _logger.LogInformation(
            "已入库 {TotalCount} 条内容，运行编号={RunId}。新增={InsertedCount}，更新={UpdatedCount}，快照={SnapshotCount}。",
            items.Count,
            runId,
            inserted,
            updated,
            snapshotCount);

        return Task.FromResult(new ContentIngestResult(items.Count, inserted, updated, snapshotCount));
    }

    private static string BuildDedupKey(FetchedContentItem item)
        => string.IsNullOrWhiteSpace(item.DedupKey)
            ? $"{item.SourceId.Trim().ToLowerInvariant()}|{item.SourceItemId.Trim()}"
            : item.DedupKey.Trim().ToLowerInvariant();

    private static IEnumerable<FetchedContentItem> OrderForDisplay(IEnumerable<FetchedContentItem> items)
        => items
            .OrderBy(item => item.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.SourceId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Rank ?? int.MaxValue)
            .ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase);

    private static bool ShouldRefreshSourceSummary(string? summary, string? source)
        => string.IsNullOrWhiteSpace(summary) ||
            string.Equals(source, SummarySources.TitleOnly, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(source, SummarySources.HoverText, StringComparison.OrdinalIgnoreCase);

    private static (string Value, string Source) BuildPreferredSummary(FetchedContentItem item)
    {
        var summary = string.IsNullOrWhiteSpace(item.SummaryText) ? item.HoverText : item.SummaryText;
        return string.IsNullOrWhiteSpace(summary)
            ? (item.Title.Trim(), SummarySources.TitleOnly)
            : (summary.Trim(), SummarySources.HoverText);
    }

    private static string BuildContentItemId(FetchedContentItem item)
        => $"ci:{SafeIdPart(item.Category)}:{SafeIdPart(item.SourceId)}:{ShortHash(item.SourceItemId)}";

    private static string BuildSnapshotId(string runId, int visualOrder, FetchedContentItem item)
        => $"{runId}:snap:{visualOrder:D5}:{SafeIdPart(item.Category)}:{SafeIdPart(item.SourceId)}:r{(item.Rank ?? 0):D4}";

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

}
