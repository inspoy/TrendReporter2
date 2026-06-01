using Dapper;
using Npgsql;
using PostgresException = Npgsql.PostgresException;
using TrendReporter2.Core.Content;
using TrendReporter2.Core.Events;

namespace TrendReporter2.Infrastructure.Persistence;

public sealed class PostgresEventRepository : IEventRepository
{
    private readonly NpgsqlDataSource _dataSource;

    static PostgresEventRepository()
    {
        DefaultTypeMap.MatchNamesWithUnderscores = true;
    }

    public PostgresEventRepository(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public async Task<IReadOnlyList<ContentItem>> LoadUnmappedRunContentItemsAsync(string runId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<ContentItemRow>(new CommandDefinition("""
        select ci.*
        from content_item ci
        where ci.last_seen_run_id = @RunId
          and not exists (
              select 1 from event_item ei where ei.content_item_id = ci.id
          )
        order by ci.last_seen_rank, ci.source, ci.title;
        """, new { RunId = runId }, cancellationToken: cancellationToken));
        return rows.Select(ToContentItem).ToList();
    }

    public async Task<IReadOnlyList<EventAggregate>> LoadRecallCandidatesAsync(
        DateTimeOffset now,
        int historyHours,
        int staleHours,
        int archiveRecallDays,
        CancellationToken cancellationToken)
    {
        var activeCutoff = now.AddHours(-Math.Max(1, historyHours));
        var staleCutoff = now.AddDays(-Math.Max(1, archiveRecallDays));
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<EventAggregateRow>(new CommandDefinition("""
        select *
        from event
        where (status = @Active and last_seen_at >= @ActiveCutoff)
           or (status = @Stale and last_seen_at >= @StaleCutoff);
        """, new { Active = EventStatus.Active, Stale = EventStatus.Stale, ActiveCutoff = PostgresTimestamp.ToUtc(activeCutoff), StaleCutoff = PostgresTimestamp.ToUtc(staleCutoff) }, cancellationToken: cancellationToken));
        return rows.Select(ToEventAggregate).ToList();
    }

    public async Task MarkStaleEventsAsync(DateTimeOffset now, int staleHours, CancellationToken cancellationToken)
    {
        var staleCutoff = now.AddHours(-Math.Max(1, staleHours));
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition("""
        update event
        set status = @Stale,
            updated_at = @Now
        where status = @Active
          and last_seen_at < @StaleCutoff;
        """, new { Active = EventStatus.Active, Stale = EventStatus.Stale, Now = PostgresTimestamp.ToUtc(now), StaleCutoff = PostgresTimestamp.ToUtc(staleCutoff) }, cancellationToken: cancellationToken));
    }

    public async Task<EventAggregate?> GetEventAsync(string eventId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        var row = await connection.QuerySingleOrDefaultAsync<EventAggregateRow>(new CommandDefinition("""
        select * from event where id = @EventId;
        """, new { EventId = eventId }, cancellationToken: cancellationToken));
        return row is null ? null : ToEventAggregate(row);
    }

    public async Task UpsertEventAsync(EventAggregate eventAggregate, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await UpsertEventAsync(connection, eventAggregate, transaction: null, cancellationToken);
    }

    public async Task<bool> MapEventItemIfMissingAsync(EventItem eventItem, CancellationToken cancellationToken)
    {
        eventItem.DedupKey = BuildDedupKey(eventItem.EventId, eventItem.ContentItemId);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        try
        {
            var affected = await connection.ExecuteAsync(new CommandDefinition("""
            insert into event_item (id, dedup_key, event_id, content_item_id, confidence, matched_at, match_reason)
            values (@Id, @DedupKey, @EventId, @ContentItemId, @Confidence, @MatchedAt, @MatchReason)
            on conflict do nothing;
            """, ToParameters(eventItem), cancellationToken: cancellationToken));
            return affected > 0;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<RunEventScoringInput>> LoadRunEventScoringInputsAsync(string runId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        var rows = (await connection.QueryAsync<ScoringInputRow>(new CommandDefinition("""
        select
            e.id as EventId,
            e.type as EventTypeValue,
            e.canonical_title as CanonicalTitle,
            e.summary as EventSummary,
            e.aliases as Aliases,
            e.entities as Entities,
            e.places as Places,
            e.key_terms as KeyTerms,
            e.representative_titles as RepresentativeTitles,
            e.current_stage as CurrentStage,
            e.progress_summary as ProgressSummary,
            e.milestones as Milestones,
            e.progress_updated_at as ProgressUpdatedAt,
            e.status as EventStatusValue,
            e.first_seen_at as FirstSeenAt,
            e.last_seen_at as LastSeenAt,
            e.last_activated_at as LastActivatedAt,
            e.last_pushed_at as LastPushedAt,
            e.push_count as PushCount,
            e.last_push_score as LastPushScore,
            e.last_push_rank_score as LastPushRankScore,
            e.last_push_source_count as LastPushSourceCount,
            e.is_blacklisted as IsBlacklisted,
            e.blacklist_reason as BlacklistReason,
            e.created_at as EventCreatedAt,
            e.updated_at as EventUpdatedAt,
            ci.id as ContentId,
            ci.dedup_key as ContentDedupKey,
            ci.source as ContentSource,
            ci.source_id as ContentSourceId,
            ci.category as ContentCategory,
            ci.type as ContentType,
            ci.content_kind as ContentKind,
            ci.source_item_id as SourceItemId,
            ci.title as ContentTitle,
            ci.url as ContentUrl,
            ci.mobile_url as MobileUrl,
            ci.pub_time as PubTime,
            ci.summary as ContentSummary,
            ci.summary_source as SummarySource,
            ci.need_enrichment as NeedEnrichment,
            ci.enrichment_status as EnrichmentStatus,
            ci.enrichment_tried_at as EnrichmentTriedAt,
            ci.created_at as ContentCreatedAt,
            ci.updated_at as ContentUpdatedAt,
            ci.last_seen_run_id as LastSeenRunId,
            ci.last_seen_at as ContentLastSeenAt,
            ci.last_seen_rank as LastSeenRank,
            ci.raw_payload as RawPayload,
            cs.id as SnapshotId,
            cs.run_id as SnapshotRunId,
            cs.content_item_id as SnapshotContentItemId,
            cs.captured_at as CapturedAt,
            cs.source as SnapshotSource,
            cs.source_id as SnapshotSourceId,
            cs.category as SnapshotCategory,
            cs.content_kind as SnapshotContentKind,
            cs.visual_order as VisualOrder,
            cs.rank as SnapshotRank,
            cs.source_list_size as SourceListSize,
            cs.normalized_rank_score as NormalizedRankScore,
            cs.freshness_score as FreshnessScore,
            ei.matched_at as MatchedAt
        from content_snapshot cs
        join event_item ei on ei.content_item_id = cs.content_item_id
        join event e on e.id = ei.event_id
        join content_item ci on ci.id = cs.content_item_id
        where cs.run_id = @RunId
        order by e.last_seen_at, e.id, cs.rank nulls last, cs.visual_order;
        """, new { RunId = runId }, cancellationToken: cancellationToken))).ToList();

        return rows
            .GroupBy(row => row.EventId, StringComparer.Ordinal)
            .Select(group => new RunEventScoringInput(
                ToEventAggregate(group.First()),
                group.Select(row => new RunEventContentEvidence(ToContentItem(row), ToContentSnapshot(row), row.MatchedAt))
                    .OrderBy(evidence => evidence.Snapshot.Rank ?? int.MaxValue)
                    .ToList()))
            .OrderBy(input => input.Event.LastSeenAt)
            .ToList();
    }

    public async Task<IReadOnlyList<EventScoreSnapshot>> LoadRecentScoreSnapshotsAsync(IReadOnlyList<string> eventIds, DateTimeOffset since, CancellationToken cancellationToken)
    {
        if (eventIds.Count == 0)
        {
            return [];
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<EventScoreSnapshotRow>(new CommandDefinition("""
        select *
        from event_score_snapshot
        where event_id = any(@EventIds)
          and calculated_at >= @Since
        order by calculated_at;
        """, new { EventIds = eventIds.ToArray(), Since = PostgresTimestamp.ToUtc(since) }, cancellationToken: cancellationToken));
        return rows.Select(ToEventScoreSnapshot).ToList();
    }

    public async Task<IReadOnlyList<DigestCandidate>> LoadDigestCandidatesAsync(DateTimeOffset since, int limit, CancellationToken cancellationToken)
    {
        if (limit <= 0)
        {
            return [];
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        var rows = await connection.QueryAsync<DigestCandidateRow>(new CommandDefinition("""
        with latest_scores as (
            select distinct on (ess.event_id) ess.*
            from event_score_snapshot ess
            join event e on e.id = ess.event_id
            where e.status = @Active
              and e.last_seen_at >= @Since
              and e.is_blacklisted = false
              and ess.calculated_at >= @Since
            order by ess.event_id, ess.calculated_at desc, ess.total_score desc, ess.id
        )
        select
            e.id as EventId,
            e.type as EventTypeValue,
            e.canonical_title as CanonicalTitle,
            e.summary as EventSummary,
            e.aliases as Aliases,
            e.entities as Entities,
            e.places as Places,
            e.key_terms as KeyTerms,
            e.representative_titles as RepresentativeTitles,
            e.current_stage as CurrentStage,
            e.progress_summary as ProgressSummary,
            e.milestones as Milestones,
            e.progress_updated_at as ProgressUpdatedAt,
            e.status as EventStatusValue,
            e.first_seen_at as FirstSeenAt,
            e.last_seen_at as LastSeenAt,
            e.last_activated_at as LastActivatedAt,
            e.last_pushed_at as LastPushedAt,
            e.push_count as PushCount,
            e.last_push_score as LastPushScore,
            e.last_push_rank_score as LastPushRankScore,
            e.last_push_source_count as LastPushSourceCount,
            e.is_blacklisted as IsBlacklisted,
            e.blacklist_reason as BlacklistReason,
            e.created_at as EventCreatedAt,
            e.updated_at as EventUpdatedAt,
            ls.id as ScoreId,
            ls.event_id as ScoreEventId,
            ls.run_id as ScoreRunId,
            ls.calculated_at as CalculatedAt,
            ls.coverage_score as CoverageScore,
            ls.rank_score as RankScore,
            ls.flash_score as FlashScore,
            ls.freshness_score as FreshnessScore,
            ls.trend_score as TrendScore,
            ls.persistence_score as PersistenceScore,
            ls.llm_boost_score as LlmBoostScore,
            ls.reactivation_bonus as ReactivationBonus,
            ls.total_score as TotalScore,
            ls.unique_source_count as UniqueSourceCount,
            ls.ranked_source_count as RankedSourceCount,
            ls.flash_source_count as FlashSourceCount,
            ls.avg_rank as AvgRank,
            ls.avg_normalized_rank as AvgNormalizedRank,
            ls.heat_value as HeatValue,
            ls.smoothed_heat_value as SmoothedHeatValue,
            ls.trend_evidence_count as TrendEvidenceCount,
            ls.current_stage as ScoreCurrentStage,
            ls.trigger_reasons as TriggerReasons
        from latest_scores ls
        join event e on e.id = ls.event_id
        order by ls.total_score desc, ls.calculated_at desc, e.last_seen_at desc, e.id
        limit @Limit;
        """, new { Active = EventStatus.Active, Since = PostgresTimestamp.ToUtc(since), Limit = limit }, cancellationToken: cancellationToken));
        return rows.Select(row => new DigestCandidate(ToEventAggregate(row), ToEventScoreSnapshot(row))).ToList();
    }

    public async Task InsertEventScoreSnapshotAsync(EventScoreSnapshot snapshot, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition("""
        insert into event_score_snapshot (id, event_id, run_id, calculated_at, coverage_score, rank_score, flash_score,
            freshness_score, trend_score, persistence_score, llm_boost_score, reactivation_bonus, total_score,
            unique_source_count, ranked_source_count, flash_source_count, avg_rank, avg_normalized_rank, heat_value,
            smoothed_heat_value, trend_evidence_count, current_stage, trigger_reasons)
        values (@Id, @EventId, @RunId, @CalculatedAt, @CoverageScore, @RankScore, @FlashScore,
            @FreshnessScore, @TrendScore, @PersistenceScore, @LlmBoostScore, @ReactivationBonus, @TotalScore,
            @UniqueSourceCount, @RankedSourceCount, @FlashSourceCount, @AvgRank, @AvgNormalizedRank, @HeatValue,
            @SmoothedHeatValue, @TrendEvidenceCount, @CurrentStage, @TriggerReasons::jsonb)
        on conflict (id) do update
        set calculated_at = excluded.calculated_at,
            coverage_score = excluded.coverage_score,
            rank_score = excluded.rank_score,
            flash_score = excluded.flash_score,
            freshness_score = excluded.freshness_score,
            trend_score = excluded.trend_score,
            persistence_score = excluded.persistence_score,
            llm_boost_score = excluded.llm_boost_score,
            reactivation_bonus = excluded.reactivation_bonus,
            total_score = excluded.total_score,
            unique_source_count = excluded.unique_source_count,
            ranked_source_count = excluded.ranked_source_count,
            flash_source_count = excluded.flash_source_count,
            avg_rank = excluded.avg_rank,
            avg_normalized_rank = excluded.avg_normalized_rank,
            heat_value = excluded.heat_value,
            smoothed_heat_value = excluded.smoothed_heat_value,
            trend_evidence_count = excluded.trend_evidence_count,
            current_stage = excluded.current_stage,
            trigger_reasons = excluded.trigger_reasons;
        """, ToParameters(snapshot), cancellationToken: cancellationToken));
    }

    public async Task<bool> InsertPushLogIfMissingAsync(PushLog pushLog, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        try
        {
            var affected = await connection.ExecuteAsync(new CommandDefinition("""
            insert into push_log (id, event_id, push_type, pushed_at, title, reason, content, payload, dedup_key, success, error)
            values (@Id, @EventId, @PushType, @PushedAt, @Title, @Reason, @Content, @Payload::jsonb, @DedupKey, @Success, @Error)
            on conflict (dedup_key) do nothing;
            """, ToParameters(pushLog), cancellationToken: cancellationToken));
            return affected > 0;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return false;
        }
    }

    public async Task UpdatePushLogAsync(PushLog pushLog, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await connection.ExecuteAsync(new CommandDefinition("""
        update push_log
        set event_id = @EventId,
            push_type = @PushType,
            pushed_at = @PushedAt,
            title = @Title,
            reason = @Reason,
            content = @Content,
            payload = @Payload::jsonb,
            success = @Success,
            error = @Error
        where id = @Id;
        """, ToParameters(pushLog), cancellationToken: cancellationToken));
    }

    public async Task UpdateEventsAsync(IReadOnlyList<EventAggregate> events, CancellationToken cancellationToken)
    {
        if (events.Count == 0)
        {
            return;
        }

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        foreach (var eventAggregate in events)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await UpsertEventAsync(connection, eventAggregate, transaction, cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static Task UpsertEventAsync(Npgsql.NpgsqlConnection connection, EventAggregate eventAggregate, Npgsql.NpgsqlTransaction? transaction, CancellationToken cancellationToken)
        => connection.ExecuteAsync(new CommandDefinition("""
        insert into event (id, type, canonical_title, summary, aliases, entities, places, key_terms, representative_titles,
            current_stage, progress_summary, milestones, progress_updated_at, status, first_seen_at, last_seen_at,
            last_activated_at, last_pushed_at, push_count, last_push_score, last_push_rank_score, last_push_source_count,
            is_blacklisted, blacklist_reason, created_at, updated_at)
        values (@Id, @Type, @CanonicalTitle, @Summary, @Aliases::jsonb, @Entities::jsonb, @Places::jsonb, @KeyTerms::jsonb, @RepresentativeTitles::jsonb,
            @CurrentStage, @ProgressSummary, @Milestones::jsonb, @ProgressUpdatedAt, @Status, @FirstSeenAt, @LastSeenAt,
            @LastActivatedAt, @LastPushedAt, @PushCount, @LastPushScore, @LastPushRankScore, @LastPushSourceCount,
            @IsBlacklisted, @BlacklistReason, @CreatedAt, @UpdatedAt)
        on conflict (id) do update
        set type = excluded.type,
            canonical_title = excluded.canonical_title,
            summary = excluded.summary,
            aliases = excluded.aliases,
            entities = excluded.entities,
            places = excluded.places,
            key_terms = excluded.key_terms,
            representative_titles = excluded.representative_titles,
            current_stage = excluded.current_stage,
            progress_summary = excluded.progress_summary,
            milestones = excluded.milestones,
            progress_updated_at = excluded.progress_updated_at,
            status = excluded.status,
            first_seen_at = excluded.first_seen_at,
            last_seen_at = excluded.last_seen_at,
            last_activated_at = excluded.last_activated_at,
            last_pushed_at = excluded.last_pushed_at,
            push_count = excluded.push_count,
            last_push_score = excluded.last_push_score,
            last_push_rank_score = excluded.last_push_rank_score,
            last_push_source_count = excluded.last_push_source_count,
            is_blacklisted = excluded.is_blacklisted,
            blacklist_reason = excluded.blacklist_reason,
            updated_at = excluded.updated_at;
        """, ToParameters(eventAggregate), transaction, cancellationToken: cancellationToken));

    private static object ToParameters(EventAggregate eventAggregate)
        => new
        {
            eventAggregate.Id,
            eventAggregate.Type,
            eventAggregate.CanonicalTitle,
            eventAggregate.Summary,
            Aliases = PostgresJson.Serialize(eventAggregate.Aliases),
            Entities = PostgresJson.Serialize(eventAggregate.Entities),
            Places = PostgresJson.Serialize(eventAggregate.Places),
            KeyTerms = PostgresJson.Serialize(eventAggregate.KeyTerms),
            RepresentativeTitles = PostgresJson.Serialize(eventAggregate.RepresentativeTitles),
            eventAggregate.CurrentStage,
            eventAggregate.ProgressSummary,
            Milestones = PostgresJson.Serialize(eventAggregate.Milestones),
            ProgressUpdatedAt = PostgresTimestamp.ToUtc(eventAggregate.ProgressUpdatedAt),
            eventAggregate.Status,
            FirstSeenAt = PostgresTimestamp.ToUtc(eventAggregate.FirstSeenAt),
            LastSeenAt = PostgresTimestamp.ToUtc(eventAggregate.LastSeenAt),
            LastActivatedAt = PostgresTimestamp.ToUtc(eventAggregate.LastActivatedAt),
            LastPushedAt = PostgresTimestamp.ToUtc(eventAggregate.LastPushedAt),
            eventAggregate.PushCount,
            eventAggregate.LastPushScore,
            eventAggregate.LastPushRankScore,
            eventAggregate.LastPushSourceCount,
            eventAggregate.IsBlacklisted,
            eventAggregate.BlacklistReason,
            CreatedAt = PostgresTimestamp.ToUtc(eventAggregate.CreatedAt),
            UpdatedAt = PostgresTimestamp.ToUtc(eventAggregate.UpdatedAt)
        };

    private static object ToParameters(EventItem eventItem)
        => new
        {
            eventItem.Id,
            eventItem.DedupKey,
            eventItem.EventId,
            eventItem.ContentItemId,
            eventItem.Confidence,
            MatchedAt = PostgresTimestamp.ToUtc(eventItem.MatchedAt),
            eventItem.MatchReason
        };

    private static object ToParameters(EventScoreSnapshot snapshot)
        => new
        {
            snapshot.Id,
            snapshot.EventId,
            snapshot.RunId,
            CalculatedAt = PostgresTimestamp.ToUtc(snapshot.CalculatedAt),
            snapshot.CoverageScore,
            snapshot.RankScore,
            snapshot.FlashScore,
            snapshot.FreshnessScore,
            snapshot.TrendScore,
            snapshot.PersistenceScore,
            snapshot.LlmBoostScore,
            snapshot.ReactivationBonus,
            snapshot.TotalScore,
            snapshot.UniqueSourceCount,
            snapshot.RankedSourceCount,
            snapshot.FlashSourceCount,
            snapshot.AvgRank,
            snapshot.AvgNormalizedRank,
            snapshot.HeatValue,
            snapshot.SmoothedHeatValue,
            snapshot.TrendEvidenceCount,
            snapshot.CurrentStage,
            TriggerReasons = PostgresJson.Serialize(snapshot.TriggerReasons)
        };

    private static object ToParameters(PushLog pushLog)
        => new
        {
            pushLog.Id,
            pushLog.EventId,
            pushLog.PushType,
            PushedAt = PostgresTimestamp.ToUtc(pushLog.PushedAt),
            pushLog.Title,
            pushLog.Reason,
            pushLog.Content,
            Payload = PostgresJson.EmptyObjectIfBlank(pushLog.Payload),
            pushLog.DedupKey,
            pushLog.Success,
            pushLog.Error
        };

    private static EventAggregate ToEventAggregate(EventAggregateRow row)
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

    private static EventAggregate ToEventAggregate(ScoringInputRow row)
        => new()
        {
            Id = row.EventId,
            Type = row.EventTypeValue,
            CanonicalTitle = row.CanonicalTitle,
            Summary = row.EventSummary,
            Aliases = PostgresJson.DeserializeList<string>(row.Aliases),
            Entities = PostgresJson.DeserializeList<string>(row.Entities),
            Places = PostgresJson.DeserializeList<string>(row.Places),
            KeyTerms = PostgresJson.DeserializeList<string>(row.KeyTerms),
            RepresentativeTitles = PostgresJson.DeserializeList<string>(row.RepresentativeTitles),
            CurrentStage = row.CurrentStage,
            ProgressSummary = row.ProgressSummary,
            Milestones = PostgresJson.DeserializeList<EventMilestone>(row.Milestones),
            ProgressUpdatedAt = row.ProgressUpdatedAt,
            Status = row.EventStatusValue,
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
            CreatedAt = row.EventCreatedAt,
            UpdatedAt = row.EventUpdatedAt
        };

    private static EventAggregate ToEventAggregate(DigestCandidateRow row)
        => new()
        {
            Id = row.EventId,
            Type = row.EventTypeValue,
            CanonicalTitle = row.CanonicalTitle,
            Summary = row.EventSummary,
            Aliases = PostgresJson.DeserializeList<string>(row.Aliases),
            Entities = PostgresJson.DeserializeList<string>(row.Entities),
            Places = PostgresJson.DeserializeList<string>(row.Places),
            KeyTerms = PostgresJson.DeserializeList<string>(row.KeyTerms),
            RepresentativeTitles = PostgresJson.DeserializeList<string>(row.RepresentativeTitles),
            CurrentStage = row.CurrentStage,
            ProgressSummary = row.ProgressSummary,
            Milestones = PostgresJson.DeserializeList<EventMilestone>(row.Milestones),
            ProgressUpdatedAt = row.ProgressUpdatedAt,
            Status = row.EventStatusValue,
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
            CreatedAt = row.EventCreatedAt,
            UpdatedAt = row.EventUpdatedAt
        };

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

    private static ContentItem ToContentItem(ScoringInputRow row)
        => new()
        {
            Id = row.ContentId,
            DedupKey = row.ContentDedupKey,
            Source = row.ContentSource,
            SourceId = row.ContentSourceId,
            Category = row.ContentCategory,
            Type = row.ContentType,
            ContentKind = row.ContentKind,
            SourceItemId = row.SourceItemId,
            Title = row.ContentTitle,
            Url = row.ContentUrl,
            MobileUrl = row.MobileUrl,
            PubTime = row.PubTime,
            Summary = row.ContentSummary,
            SummarySource = row.SummarySource,
            NeedEnrichment = row.NeedEnrichment,
            EnrichmentStatus = row.EnrichmentStatus,
            EnrichmentTriedAt = row.EnrichmentTriedAt,
            CreatedAt = row.ContentCreatedAt,
            UpdatedAt = row.ContentUpdatedAt,
            LastSeenRunId = row.LastSeenRunId,
            LastSeenAt = row.ContentLastSeenAt,
            LastSeenRank = row.LastSeenRank,
            RawPayload = PostgresJson.EmptyObjectIfBlank(row.RawPayload)
        };

    private static ContentSnapshot ToContentSnapshot(ScoringInputRow row)
        => new()
        {
            Id = row.SnapshotId,
            RunId = row.SnapshotRunId,
            ContentItemId = row.SnapshotContentItemId,
            CapturedAt = row.CapturedAt,
            Source = row.SnapshotSource,
            SourceId = row.SnapshotSourceId,
            Category = row.SnapshotCategory,
            ContentKind = row.SnapshotContentKind,
            VisualOrder = row.VisualOrder,
            Rank = row.SnapshotRank,
            SourceListSize = row.SourceListSize,
            NormalizedRankScore = row.NormalizedRankScore,
            FreshnessScore = row.FreshnessScore
        };

    private static EventScoreSnapshot ToEventScoreSnapshot(EventScoreSnapshotRow row)
        => new()
        {
            Id = row.Id,
            EventId = row.EventId,
            RunId = row.RunId,
            CalculatedAt = row.CalculatedAt,
            CoverageScore = row.CoverageScore,
            RankScore = row.RankScore,
            FlashScore = row.FlashScore,
            FreshnessScore = row.FreshnessScore,
            TrendScore = row.TrendScore,
            PersistenceScore = row.PersistenceScore,
            LlmBoostScore = row.LlmBoostScore,
            ReactivationBonus = row.ReactivationBonus,
            TotalScore = row.TotalScore,
            UniqueSourceCount = row.UniqueSourceCount,
            RankedSourceCount = row.RankedSourceCount,
            FlashSourceCount = row.FlashSourceCount,
            AvgRank = row.AvgRank,
            AvgNormalizedRank = row.AvgNormalizedRank,
            HeatValue = row.HeatValue,
            SmoothedHeatValue = row.SmoothedHeatValue,
            TrendEvidenceCount = row.TrendEvidenceCount,
            CurrentStage = row.CurrentStage,
            TriggerReasons = PostgresJson.DeserializeList<string>(row.TriggerReasons)
        };

    private static EventScoreSnapshot ToEventScoreSnapshot(DigestCandidateRow row)
        => new()
        {
            Id = row.ScoreId,
            EventId = row.ScoreEventId,
            RunId = row.ScoreRunId,
            CalculatedAt = row.CalculatedAt,
            CoverageScore = row.CoverageScore,
            RankScore = row.RankScore,
            FlashScore = row.FlashScore,
            FreshnessScore = row.FreshnessScore,
            TrendScore = row.TrendScore,
            PersistenceScore = row.PersistenceScore,
            LlmBoostScore = row.LlmBoostScore,
            ReactivationBonus = row.ReactivationBonus,
            TotalScore = row.TotalScore,
            UniqueSourceCount = row.UniqueSourceCount,
            RankedSourceCount = row.RankedSourceCount,
            FlashSourceCount = row.FlashSourceCount,
            AvgRank = row.AvgRank,
            AvgNormalizedRank = row.AvgNormalizedRank,
            HeatValue = row.HeatValue,
            SmoothedHeatValue = row.SmoothedHeatValue,
            TrendEvidenceCount = row.TrendEvidenceCount,
            CurrentStage = row.ScoreCurrentStage,
            TriggerReasons = PostgresJson.DeserializeList<string>(row.TriggerReasons)
        };

    public static string BuildDedupKey(string eventId, string contentItemId)
        => $"{eventId.Trim()}|{contentItemId.Trim()}";

    private sealed class ContentItemRow
    {
        public string Id { get; set; } = "";
        public string DedupKey { get; set; } = "";
        public string Source { get; set; } = "";
        public string? SourceId { get; set; }
        public string Category { get; set; } = "";
        public string Type { get; set; } = "";
        public string ContentKind { get; set; } = "ranked_news";
        public string SourceItemId { get; set; } = "";
        public string Title { get; set; } = "";
        public string Url { get; set; } = "";
        public string? MobileUrl { get; set; }
        public DateTimeOffset? PubTime { get; set; }
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

    private sealed class EventAggregateRow
    {
        public string Id { get; set; } = "";
        public string Type { get; set; } = "";
        public string CanonicalTitle { get; set; } = "";
        public string Summary { get; set; } = "";
        public string? Aliases { get; set; }
        public string? Entities { get; set; }
        public string? Places { get; set; }
        public string? KeyTerms { get; set; }
        public string? RepresentativeTitles { get; set; }
        public string? CurrentStage { get; set; }
        public string? ProgressSummary { get; set; }
        public string? Milestones { get; set; }
        public DateTimeOffset? ProgressUpdatedAt { get; set; }
        public string Status { get; set; } = "";
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

    private sealed class EventScoreSnapshotRow
    {
        public string Id { get; set; } = "";
        public string EventId { get; set; } = "";
        public string RunId { get; set; } = "";
        public DateTimeOffset CalculatedAt { get; set; }
        public double CoverageScore { get; set; }
        public double RankScore { get; set; }
        public double FlashScore { get; set; }
        public double FreshnessScore { get; set; }
        public double TrendScore { get; set; }
        public double PersistenceScore { get; set; }
        public double LlmBoostScore { get; set; }
        public double ReactivationBonus { get; set; }
        public double TotalScore { get; set; }
        public int UniqueSourceCount { get; set; }
        public int RankedSourceCount { get; set; }
        public int FlashSourceCount { get; set; }
        public double AvgRank { get; set; }
        public double AvgNormalizedRank { get; set; }
        public double HeatValue { get; set; }
        public double SmoothedHeatValue { get; set; }
        public int TrendEvidenceCount { get; set; }
        public string? CurrentStage { get; set; }
        public string? TriggerReasons { get; set; }
    }

    private sealed class ScoringInputRow
    {
        public string EventId { get; set; } = "";
        public string EventTypeValue { get; set; } = "";
        public string CanonicalTitle { get; set; } = "";
        public string EventSummary { get; set; } = "";
        public string? Aliases { get; set; }
        public string? Entities { get; set; }
        public string? Places { get; set; }
        public string? KeyTerms { get; set; }
        public string? RepresentativeTitles { get; set; }
        public string? CurrentStage { get; set; }
        public string? ProgressSummary { get; set; }
        public string? Milestones { get; set; }
        public DateTimeOffset? ProgressUpdatedAt { get; set; }
        public string EventStatusValue { get; set; } = "";
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
        public DateTimeOffset EventCreatedAt { get; set; }
        public DateTimeOffset EventUpdatedAt { get; set; }
        public string ContentId { get; set; } = "";
        public string ContentDedupKey { get; set; } = "";
        public string ContentSource { get; set; } = "";
        public string? ContentSourceId { get; set; }
        public string ContentCategory { get; set; } = "";
        public string ContentType { get; set; } = "";
        public string ContentKind { get; set; } = "ranked_news";
        public string SourceItemId { get; set; } = "";
        public string ContentTitle { get; set; } = "";
        public string ContentUrl { get; set; } = "";
        public string? MobileUrl { get; set; }
        public DateTimeOffset? PubTime { get; set; }
        public string? ContentSummary { get; set; }
        public string? SummarySource { get; set; }
        public bool NeedEnrichment { get; set; }
        public string EnrichmentStatus { get; set; } = "";
        public DateTimeOffset? EnrichmentTriedAt { get; set; }
        public DateTimeOffset ContentCreatedAt { get; set; }
        public DateTimeOffset ContentUpdatedAt { get; set; }
        public string? LastSeenRunId { get; set; }
        public DateTimeOffset? ContentLastSeenAt { get; set; }
        public int LastSeenRank { get; set; }
        public string? RawPayload { get; set; }
        public string SnapshotId { get; set; } = "";
        public string SnapshotRunId { get; set; } = "";
        public string SnapshotContentItemId { get; set; } = "";
        public DateTimeOffset CapturedAt { get; set; }
        public string SnapshotSource { get; set; } = "";
        public string? SnapshotSourceId { get; set; }
        public string SnapshotCategory { get; set; } = "";
        public string SnapshotContentKind { get; set; } = "ranked_news";
        public int VisualOrder { get; set; }
        public int? SnapshotRank { get; set; }
        public int? SourceListSize { get; set; }
        public double? NormalizedRankScore { get; set; }
        public double FreshnessScore { get; set; }
        public DateTimeOffset MatchedAt { get; set; }
    }

    private sealed class DigestCandidateRow
    {
        public string EventId { get; set; } = "";
        public string EventTypeValue { get; set; } = "";
        public string CanonicalTitle { get; set; } = "";
        public string EventSummary { get; set; } = "";
        public string? Aliases { get; set; }
        public string? Entities { get; set; }
        public string? Places { get; set; }
        public string? KeyTerms { get; set; }
        public string? RepresentativeTitles { get; set; }
        public string? CurrentStage { get; set; }
        public string? ProgressSummary { get; set; }
        public string? Milestones { get; set; }
        public DateTimeOffset? ProgressUpdatedAt { get; set; }
        public string EventStatusValue { get; set; } = "";
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
        public DateTimeOffset EventCreatedAt { get; set; }
        public DateTimeOffset EventUpdatedAt { get; set; }
        public string ScoreId { get; set; } = "";
        public string ScoreEventId { get; set; } = "";
        public string ScoreRunId { get; set; } = "";
        public DateTimeOffset CalculatedAt { get; set; }
        public double CoverageScore { get; set; }
        public double RankScore { get; set; }
        public double FlashScore { get; set; }
        public double FreshnessScore { get; set; }
        public double TrendScore { get; set; }
        public double PersistenceScore { get; set; }
        public double LlmBoostScore { get; set; }
        public double ReactivationBonus { get; set; }
        public double TotalScore { get; set; }
        public int UniqueSourceCount { get; set; }
        public int RankedSourceCount { get; set; }
        public int FlashSourceCount { get; set; }
        public double AvgRank { get; set; }
        public double AvgNormalizedRank { get; set; }
        public double HeatValue { get; set; }
        public double SmoothedHeatValue { get; set; }
        public int TrendEvidenceCount { get; set; }
        public string? ScoreCurrentStage { get; set; }
        public string? TriggerReasons { get; set; }
    }
}
