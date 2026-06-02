create schema if not exists metrics;

-- ============================================================================
-- Run health views
-- ============================================================================

drop view if exists metrics.run_success_rate cascade;
create view metrics.run_success_rate as
select
    date(finished_at) as run_date,
    count(*) as total_runs,
    count(*) filter (where status = 'succeeded') as succeeded_runs,
    count(*) filter (where status = 'partial') as partial_runs,
    count(*) filter (where status = 'failed') as failed_runs,
    round(
        (100.0 * count(*) filter (where status in ('succeeded', 'partial'))
        / nullif(count(*), 0))::numeric,
        2
    ) as success_rate_pct,
    coalesce(sum(estimated_llm_cost), 0) as total_llm_cost
from fetch_run
where finished_at is not null
  and finished_at >= now() - interval '30 days'
group by date(finished_at)
order by run_date desc;

drop view if exists metrics.run_source_failure_rate cascade;
create view metrics.run_source_failure_rate as
select
    frs.source_id,
    coalesce(s.display_name, frs.source_id) as display_name,
    s.provider,
    s.content_kind,
    date(frs.created_at) as fetch_date,
    count(*) as total_fetches,
    count(*) filter (where frs.status = 'succeeded') as succeeded_fetches,
    count(*) filter (where frs.status = 'failed') as failed_fetches,
    round(
        (100.0 * count(*) filter (where frs.status = 'failed')
        / nullif(count(*), 0))::numeric,
        2
    ) as failure_rate_pct,
    string_agg(
        distinct case when frs.status = 'failed' and frs.error is not null then frs.error end,
        '; '
    ) filter (where frs.status = 'failed' and frs.error is not null) as recent_errors
from fetch_run_source frs
left join source s on frs.source_id = s.id
where frs.created_at >= now() - interval '30 days'
group by frs.source_id, s.display_name, s.provider, s.content_kind, date(frs.created_at)
order by fetch_date desc, failure_rate_pct desc;

drop view if exists metrics.run_stage_duration cascade;
create view metrics.run_stage_duration as
select
    stage,
    date(started_at) as run_date,
    count(*) as stage_count,
    round(avg(duration_ms)) as avg_duration_ms,
    percentile_cont(0.5) within group (order by duration_ms) as p50_duration_ms,
    percentile_cont(0.95) within group (order by duration_ms) as p95_duration_ms,
    min(duration_ms) as min_duration_ms,
    max(duration_ms) as max_duration_ms
from fetch_run_stage
where started_at >= now() - interval '30 days'
  and status != 'skipped'
group by stage, date(started_at)
order by run_date desc, stage;

-- ============================================================================
-- LLM cost views
-- ============================================================================

drop view if exists metrics.llm_daily_cost cascade;
create view metrics.llm_daily_cost as
select
    stage,
    model,
    date(created_at) as usage_date,
    count(*) as call_count,
    coalesce(sum(input_tokens), 0) as total_input_tokens,
    coalesce(sum(output_tokens), 0) as total_output_tokens,
    coalesce(sum(cache_read_tokens), 0) as total_cache_read_tokens,
    sum(estimated_cost) as total_estimated_cost,
    round(avg(duration_ms)) as avg_duration_ms,
    count(*) filter (where not success) as failed_calls
from llm_usage
where created_at >= now() - interval '30 days'
group by stage, model, date(created_at)
order by usage_date desc, stage, model;

drop view if exists metrics.llm_cost_trend_7d cascade;
create view metrics.llm_cost_trend_7d as
select
    date(created_at) as usage_date,
    count(*) as call_count,
    sum(estimated_cost) as total_estimated_cost,
    coalesce(sum(input_tokens), 0) as total_input_tokens,
    coalesce(sum(output_tokens), 0) as total_output_tokens
from llm_usage
where created_at >= now() - interval '7 days'
group by date(created_at)
order by usage_date desc;

drop view if exists metrics.llm_stage_cost_pct cascade;
create view metrics.llm_stage_cost_pct as
select
    stage,
    sum(estimated_cost) as total_cost,
    count(*) as call_count,
    round(
        (100.0 * sum(estimated_cost)
        / nullif((select sum(estimated_cost) from llm_usage), 0))::numeric,
        2
    ) as cost_pct
from llm_usage
group by stage
order by total_cost desc;

-- ============================================================================
-- Event and content views
-- ============================================================================

drop view if exists metrics.event_daily_counts cascade;
create view metrics.event_daily_counts as
with
new_events as (
    select
        date(first_seen_at) as event_date,
        count(*) as new_events,
        count(*) filter (where type = 'NewsEvent') as news_events,
        count(*) filter (where type = 'Topic') as topic_events
    from event
    where status != 'Merged'
      and first_seen_at >= now() - interval '30 days'
    group by date(first_seen_at)
),
pushed_events as (
    select
        date(e.last_pushed_at) as push_date,
        count(distinct e.id) as pushed_event_count
    from event e
    where e.status != 'Merged'
      and e.last_pushed_at is not null
      and e.last_pushed_at >= now() - interval '30 days'
    group by date(e.last_pushed_at)
),
push_counts as (
    select
        date(pushed_at) as push_date,
        count(*) filter (where push_type = 'Instant') as instant_pushes,
        count(*) filter (where push_type = 'Digest') as digest_pushes
    from push_log
    where pushed_at >= now() - interval '30 days'
    group by date(pushed_at)
)
select
    coalesce(n.event_date, pe.push_date, pc.push_date) as event_date,
    coalesce(n.new_events, 0) as new_events,
    coalesce(n.news_events, 0) as news_events,
    coalesce(n.topic_events, 0) as topic_events,
    coalesce(pe.pushed_event_count, 0) as pushed_events,
    coalesce(pc.instant_pushes, 0) as instant_pushes,
    coalesce(pc.digest_pushes, 0) as digest_pushes
from new_events n
full outer join pushed_events pe on n.event_date = pe.push_date
full outer join push_counts pc
    on coalesce(n.event_date, pe.push_date) = pc.push_date
order by event_date desc;

drop view if exists metrics.event_score_distribution cascade;
create view metrics.event_score_distribution as
with latest_run as (
    select id from fetch_run
    where finished_at is not null
    order by finished_at desc
    limit 1
)
select
    case
        when ess.total_score < 30 then '0-30'
        when ess.total_score < 60 then '30-60'
        when ess.total_score < 80 then '60-80'
        else '80-100'
    end as score_bucket,
    count(*) as event_count,
    round(avg(ess.total_score)::numeric, 1) as avg_score_in_bucket,
    round(avg(ess.coverage_score)::numeric, 2) as avg_coverage_score,
    round(avg(ess.rank_score)::numeric, 2) as avg_rank_score,
    round(avg(ess.flash_score)::numeric, 2) as avg_flash_score,
    round(avg(ess.heat_value)::numeric, 2) as avg_heat
from event_score_snapshot ess
join event e on ess.event_id = e.id
where ess.run_id = (select id from latest_run)
  and e.is_blacklisted = false
  and e.status != 'Merged'
group by
    case
        when ess.total_score < 30 then '0-30'
        when ess.total_score < 60 then '30-60'
        when ess.total_score < 80 then '60-80'
        else '80-100'
    end
order by score_bucket;

drop view if exists metrics.event_tag_distribution cascade;
create view metrics.event_tag_distribution as
select
    t.category,
    t.name as tag_name,
    count(distinct et.event_id) as event_count,
    round(avg(et.confidence)::numeric, 2) as avg_confidence
from event_tag et
join tag t on et.tag_id = t.id
join event e on et.event_id = e.id
where e.status != 'Merged'
group by t.category, t.name
order by event_count desc;

-- ============================================================================
-- Composite dashboard views
-- ============================================================================

drop view if exists metrics.latest_run_summary cascade;
create view metrics.latest_run_summary as
with latest as (
    select * from fetch_run
    where finished_at is not null
    order by finished_at desc
    limit 1
),
stage_durations as (
    select
        run_id,
        max(case when stage = 'fetch' then duration_ms end) as fetch_duration_ms,
        max(case when stage = 'ingest' then duration_ms end) as ingest_duration_ms,
        max(case when stage = 'enrich' then duration_ms end) as enrich_duration_ms,
        max(case when stage = 'match' then duration_ms end) as match_duration_ms,
        max(case when stage = 'score' then duration_ms end) as score_duration_ms,
        max(case when stage = 'push' then duration_ms end) as push_duration_ms,
        max(case when stage = 'report' then duration_ms end) as report_duration_ms,
        max(case when stage = 'tagging' then duration_ms end) as tagging_duration_ms,
        max(case when stage = 'embedding' then duration_ms end) as embedding_duration_ms,
        max(case when stage = 'secondary_merge' then duration_ms end) as secondary_merge_duration_ms
    from fetch_run_stage
    where run_id = (select id from latest)
    group by run_id
)
select
    l.id as run_id,
    l.status,
    l.started_at,
    l.finished_at,
    round((extract(epoch from (l.finished_at - l.started_at)) / 60.0)::numeric, 1) as duration_minutes,
    l.source_count,
    l.success_source_count,
    l.failure_source_count,
    l.fetched_item_count,
    l.enriched_item_count,
    l.matched_event_count,
    l.pushed_event_count,
    l.estimated_llm_cost,
    coalesce(sd.fetch_duration_ms, 0) as fetch_duration_ms,
    coalesce(sd.ingest_duration_ms, 0) as ingest_duration_ms,
    coalesce(sd.enrich_duration_ms, 0) as enrich_duration_ms,
    coalesce(sd.match_duration_ms, 0) as match_duration_ms,
    coalesce(sd.score_duration_ms, 0) as score_duration_ms,
    coalesce(sd.push_duration_ms, 0) as push_duration_ms,
    coalesce(sd.report_duration_ms, 0) as report_duration_ms,
    coalesce(sd.tagging_duration_ms, 0) as tagging_duration_ms,
    coalesce(sd.embedding_duration_ms, 0) as embedding_duration_ms,
    coalesce(sd.secondary_merge_duration_ms, 0) as secondary_merge_duration_ms
from latest l
left join stage_durations sd on l.id = sd.run_id;

drop view if exists metrics.health_snapshot_7d cascade;
create view metrics.health_snapshot_7d as
with runs_7d as (
    select
        count(*) as total_runs,
        count(*) filter (where status in ('succeeded', 'partial')) as successful_runs,
        coalesce(sum(estimated_llm_cost), 0) as total_llm_cost,
        avg(extract(epoch from (finished_at - started_at))) as avg_duration_seconds,
        min(finished_at) as window_start,
        max(finished_at) as window_end
    from fetch_run
    where finished_at is not null
      and finished_at >= now() - interval '7 days'
),
events_7d as (
    select count(*) as new_events
    from event
    where first_seen_at >= now() - interval '7 days'
      and status != 'Merged'
),
active_events as (
    select count(*) as active_event_count
    from event
    where status = 'Active'
),
pushes_7d as (
    select
        count(*) as total_pushes,
        count(*) filter (where push_type = 'Instant') as instant_pushes,
        count(*) filter (where push_type = 'Digest') as digest_pushes
    from push_log
    where pushed_at >= now() - interval '7 days'
),
llm_7d as (
    select
        count(*) as llm_calls,
        sum(estimated_cost) as llm_cost,
        count(*) filter (where not success) as llm_failures
    from llm_usage
    where created_at >= now() - interval '7 days'
)
select
    coalesce(r.total_runs, 0) as total_runs,
    case
        when r.total_runs > 0
        then round((100.0 * r.successful_runs / r.total_runs)::numeric, 2)
        else null
    end as success_rate_pct,
    coalesce(r.total_llm_cost, 0) as total_llm_cost,
    round(coalesce(r.total_llm_cost / nullif(r.total_runs, 0), 0)::numeric, 6) as avg_llm_cost_per_run,
    round(coalesce(r.avg_duration_seconds / 60.0, 0)::numeric, 1) as avg_run_duration_minutes,
    coalesce(e.new_events, 0) as new_events,
    coalesce(a.active_event_count, 0) as active_events,
    round(coalesce(e.new_events::numeric / nullif(r.total_runs, 0), 0)::numeric, 1) as avg_new_events_per_run,
    coalesce(p.total_pushes, 0) as total_pushes,
    coalesce(p.instant_pushes, 0) as instant_pushes,
    coalesce(p.digest_pushes, 0) as digest_pushes,
    coalesce(ll.llm_calls, 0) as llm_calls,
    coalesce(ll.llm_failures, 0) as llm_failures,
    r.window_start,
    r.window_end
from runs_7d r
cross join events_7d e
cross join active_events a
cross join pushes_7d p
cross join llm_7d ll;
