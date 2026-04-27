using LiteDB;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography;
using System.Text;
using TrendReporter2.Core.Content;
using TrendReporter2.Core.Enrichment;
using TrendReporter2.Core.News;
using TrendReporter2.Core.Persistence;

namespace TrendReporter2.Infrastructure.Persistence;

public sealed class ContentIngestService : IContentIngestService
{
    private readonly LiteDbConnectionFactory _connectionFactory;
    private readonly IEnrichmentPolicy _enrichmentPolicy;
    private readonly ILogger<ContentIngestService> _logger;

    public ContentIngestService(
        LiteDbConnectionFactory connectionFactory,
        IEnrichmentPolicy enrichmentPolicy,
        ILogger<ContentIngestService> logger)
    {
        _connectionFactory = connectionFactory;
        _enrichmentPolicy = enrichmentPolicy;
        _logger = logger;
    }

    public Task<ContentIngestResult> IngestAsync(
        string runId,
        IReadOnlyList<NewsItem> items,
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

            var dedupKey = BuildDedupKey(item.Source, item.SourceItemId);
            var existing = contentItems.FindOne(x => x.DedupKey == dedupKey);
            ContentItem persisted;

            if (existing is null)
            {
                var needEnrichment = _enrichmentPolicy.NeedEnrichment(item);
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
                    Summary = BuildTitleOnlySummary(item),
                    SummarySource = SummarySources.TitleOnly,
                    NeedEnrichment = needEnrichment,
                    EnrichmentStatus = needEnrichment ? EnrichmentStatuses.Pending : EnrichmentStatuses.Skipped,
                    CreatedAt = capturedAt,
                    UpdatedAt = capturedAt,
                    LastSeenRunId = runId,
                    LastSeenAt = capturedAt,
                    LastSeenRank = item.Rank,
                    RawPayload = item.RawPayload
                };

                contentItems.Insert(persisted);
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
                if (string.IsNullOrWhiteSpace(existing.Summary) || string.Equals(existing.SummarySource, SummarySources.TitleOnly, StringComparison.OrdinalIgnoreCase))
                {
                    existing.Summary = BuildTitleOnlySummary(item);
                    existing.SummarySource = SummarySources.TitleOnly;
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
                Source = item.Source,
                Category = item.Category,
                VisualOrder = visualOrder,
                Rank = item.Rank,
                SourceListSize = item.SourceListSize,
                NormalizedRankScore = CalculateNormalizedRankScore(item.Rank, item.SourceListSize)
            });
            snapshotCount++;
        }

        _logger.LogInformation(
            "Ingested {TotalCount} content items for run={RunId}. Inserted={InsertedCount}, Updated={UpdatedCount}, Snapshots={SnapshotCount}.",
            items.Count,
            runId,
            inserted,
            updated,
            snapshotCount);

        return Task.FromResult(new ContentIngestResult(items.Count, inserted, updated, snapshotCount));
    }

    private static string BuildDedupKey(string source, string sourceItemId)
        => $"{source.Trim().ToLowerInvariant()}|{sourceItemId.Trim()}";

    private static IEnumerable<NewsItem> OrderForDisplay(IEnumerable<NewsItem> items)
        => items
            .OrderBy(item => item.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Source, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Rank)
            .ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase);

    private static string BuildTitleOnlySummary(NewsItem item)
        => string.IsNullOrWhiteSpace(item.HoverText)
            ? item.Title.Trim()
            : $"{item.Title.Trim()} {item.HoverText.Trim()}";

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
}
