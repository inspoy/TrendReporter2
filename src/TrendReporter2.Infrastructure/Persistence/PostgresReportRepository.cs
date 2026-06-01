using System.Security.Cryptography;
using System.Text;
using Dapper;
using Npgsql;
using TrendReporter2.Core.Events;
using TrendReporter2.Core.Reports;

namespace TrendReporter2.Infrastructure.Persistence;

public sealed class PostgresReportRepository : IReportReadModelQuery, IReportSnapshotRepository
{
    private readonly NpgsqlDataSource _dataSource;

    static PostgresReportRepository()
    {
        DefaultTypeMap.MatchNamesWithUnderscores = true;
    }

    public PostgresReportRepository(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task<ReportPayload> BuildDigestReportAsync(DateTimeOffset windowStart, DateTimeOffset windowEnd, string slotTime, int limit, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        var events = (await connection.QueryAsync<ReportEventRow>(new CommandDefinition("""
        with latest_scores as (
            select distinct on (ess.event_id) ess.*
            from event_score_snapshot ess
            join event e on e.id = ess.event_id
            where e.status = @Active
              and e.is_blacklisted = false
              and e.last_seen_at >= @WindowStart
              and e.last_seen_at <= @WindowEnd
              and ess.calculated_at >= @WindowStart
              and ess.calculated_at <= @WindowEnd
            order by ess.event_id, ess.calculated_at desc, ess.total_score desc, ess.id
        )
        select e.id as event_id,
               e.canonical_title as title,
               e.summary,
               coalesce(e.current_stage, ls.current_stage) as stage,
               e.progress_summary,
               ls.total_score,
               ls.heat_value,
               ls.unique_source_count,
               ls.trigger_reasons
        from latest_scores ls
        join event e on e.id = ls.event_id
        order by ls.total_score desc, ls.heat_value desc, e.last_seen_at desc, e.id
        limit @Limit;
        """, new { Active = EventStatus.Active, WindowStart = PostgresTimestamp.ToUtc(windowStart), WindowEnd = PostgresTimestamp.ToUtc(windowEnd), Limit = Math.Max(1, limit) }, cancellationToken: cancellationToken))).ToList();

        var eventIds = events.Select(row => row.EventId).ToArray();
        var tags = eventIds.Length == 0
            ? []
            : (await connection.QueryAsync<ReportTagRow>(new CommandDefinition("""
            select et.event_id, t.display_name
            from event_tag et
            join tag t on t.id = et.tag_id
            where et.event_id = any(@EventIds)
            order by et.confidence desc, t.display_name;
            """, new { EventIds = eventIds }, cancellationToken: cancellationToken))).ToList();
        var contents = eventIds.Length == 0
            ? []
            : (await connection.QueryAsync<ReportContentRow>(new CommandDefinition("""
            select ei.event_id,
                   ci.id as content_item_id,
                   coalesce(nullif(s.display_name, ''), nullif(ci.source, ''), ci.category) as source,
                   ci.pub_time as published_at,
                   ci.title,
                   ci.url
            from event_item ei
            join content_item ci on ci.id = ei.content_item_id
            left join source s on s.id = ci.source_id
            where ei.event_id = any(@EventIds)
            order by ei.event_id, ci.last_seen_at desc nulls last, ci.last_seen_rank nulls last, ci.title
            """, new { EventIds = eventIds }, cancellationToken: cancellationToken))).ToList();

        return new ReportPayload
        {
            GeneratedAt = windowEnd,
            WindowStart = windowStart,
            WindowEnd = windowEnd,
            SlotTime = slotTime,
            Events = events.Select(row => new ReportEventItem
            {
                EventId = row.EventId,
                Title = row.Title,
                Summary = row.Summary,
                Stage = row.Stage,
                ProgressSummary = row.ProgressSummary,
                TotalScore = row.TotalScore,
                HeatValue = row.HeatValue,
                UniqueSourceCount = row.UniqueSourceCount,
                TriggerReasons = PostgresJson.DeserializeList<string>(row.TriggerReasons),
                Tags = tags.Where(tag => tag.EventId == row.EventId).Select(tag => tag.DisplayName).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
                ContentItems = contents.Where(content => content.EventId == row.EventId).Take(8).Select(content => new ReportContentItem
                {
                    ContentItemId = content.ContentItemId,
                    Source = content.Source,
                    PublishedAt = content.PublishedAt,
                    Title = content.Title,
                    Url = content.Url
                }).ToList()
            }).ToList()
        };
    }

    public async Task UpsertAsync(ReportSnapshot snapshot, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition("""
        insert into report_snapshot (id, report_type, slot_time, generated_at, file_path, public_url, event_count, payload_json)
        values (@Id, @ReportType, @SlotTime, @GeneratedAt, @FilePath, @PublicUrl, @EventCount, @PayloadJson::jsonb)
        on conflict (id) do update
        set generated_at = excluded.generated_at,
            file_path = excluded.file_path,
            public_url = excluded.public_url,
            event_count = excluded.event_count,
            payload_json = excluded.payload_json;
        """, new
        {
            snapshot.Id,
            snapshot.ReportType,
            SlotTime = PostgresTimestamp.ToUtc(snapshot.SlotTime),
            GeneratedAt = PostgresTimestamp.ToUtc(snapshot.GeneratedAt),
            snapshot.FilePath,
            snapshot.PublicUrl,
            snapshot.EventCount,
            PayloadJson = PostgresJson.EmptyObjectIfBlank(snapshot.PayloadJson)
        }, cancellationToken: cancellationToken));
    }

    public static string BuildSnapshotId(string reportType, DateTimeOffset slotTime)
        => $"report:{reportType}:{ShortHash($"{reportType}|{slotTime.UtcDateTime:O}")}";

    private static string ShortHash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..24].ToLowerInvariant();

    private sealed class ReportEventRow
    {
        public string EventId { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Summary { get; set; } = string.Empty;
        public string? Stage { get; set; }
        public string? ProgressSummary { get; set; }
        public double TotalScore { get; set; }
        public double HeatValue { get; set; }
        public int UniqueSourceCount { get; set; }
        public string? TriggerReasons { get; set; }
    }

    private sealed class ReportTagRow
    {
        public string EventId { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
    }

    private sealed class ReportContentRow
    {
        public string EventId { get; set; } = string.Empty;
        public string ContentItemId { get; set; } = string.Empty;
        public string Source { get; set; } = string.Empty;
        public DateTimeOffset? PublishedAt { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
    }
}
