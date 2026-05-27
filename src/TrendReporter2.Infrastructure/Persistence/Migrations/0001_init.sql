create extension if not exists vector;

create table if not exists schema_migration (
    version text primary key,
    name text not null,
    checksum text not null,
    applied_at timestamptz not null default now()
);

create table if not exists source (
    id text primary key,
    category text not null,
    name text not null,
    display_name text,
    enabled boolean not null default true,
    created_at timestamptz not null,
    updated_at timestamptz not null,
    constraint uq_source_category_name unique (category, name)
);

create table if not exists fetch_run (
    id text primary key,
    started_at timestamptz not null,
    finished_at timestamptz,
    status text not null,
    source_count integer not null default 0,
    success_source_count integer not null default 0,
    failure_source_count integer not null default 0,
    fetched_item_count integer not null default 0,
    enriched_item_count integer not null default 0,
    matched_event_count integer not null default 0,
    pushed_event_count integer not null default 0,
    errors jsonb not null default '[]'::jsonb
);

create table if not exists content_item (
    id text primary key,
    dedup_key text not null,
    source text not null,
    category text not null,
    type text not null default 'News',
    source_item_id text not null,
    title text not null,
    url text not null,
    mobile_url text,
    pub_time timestamptz,
    hover_text text,
    summary text,
    summary_source text,
    need_enrichment boolean not null default false,
    enrichment_status text not null default 'None',
    enrichment_tried_at timestamptz,
    created_at timestamptz not null,
    updated_at timestamptz not null,
    last_seen_run_id text references fetch_run (id) on delete set null,
    last_seen_at timestamptz,
    last_seen_rank integer not null default 0,
    raw_payload jsonb not null default '{}'::jsonb,
    constraint uq_content_item_dedup_key unique (dedup_key)
);

create table if not exists content_snapshot (
    id text primary key,
    run_id text not null references fetch_run (id) on delete cascade,
    content_item_id text not null references content_item (id) on delete cascade,
    captured_at timestamptz not null,
    source text not null,
    category text not null,
    visual_order integer not null,
    rank integer not null,
    source_list_size integer not null,
    normalized_rank_score double precision not null
);

create table if not exists event (
    id text primary key,
    type text not null,
    canonical_title text not null,
    summary text not null,
    aliases jsonb not null default '[]'::jsonb,
    entities jsonb not null default '[]'::jsonb,
    places jsonb not null default '[]'::jsonb,
    key_terms jsonb not null default '[]'::jsonb,
    representative_titles jsonb not null default '[]'::jsonb,
    current_stage text,
    progress_summary text,
    milestones jsonb not null default '[]'::jsonb,
    progress_updated_at timestamptz,
    status text not null,
    first_seen_at timestamptz not null,
    last_seen_at timestamptz not null,
    last_activated_at timestamptz not null,
    last_pushed_at timestamptz,
    push_count integer not null default 0,
    last_push_score double precision,
    last_push_rank_score double precision,
    last_push_source_count integer,
    is_blacklisted boolean not null default false,
    blacklist_reason text,
    created_at timestamptz not null,
    updated_at timestamptz not null
);

create table if not exists event_item (
    id text primary key,
    dedup_key text not null,
    event_id text not null references event (id) on delete cascade,
    content_item_id text not null references content_item (id) on delete cascade,
    confidence double precision not null,
    matched_at timestamptz not null,
    match_reason text,
    constraint uq_event_item_dedup_key unique (dedup_key),
    constraint uq_event_item_content_item_id unique (content_item_id)
);

create table if not exists event_score_snapshot (
    id text primary key,
    event_id text not null references event (id) on delete cascade,
    run_id text not null references fetch_run (id) on delete cascade,
    calculated_at timestamptz not null,
    coverage_score double precision not null,
    rank_score double precision not null,
    trend_score double precision not null,
    persistence_score double precision not null,
    llm_boost_score double precision not null,
    reactivation_bonus double precision not null,
    total_score double precision not null,
    unique_source_count integer not null,
    avg_rank double precision not null,
    avg_normalized_rank double precision not null,
    heat_value double precision not null,
    smoothed_heat_value double precision not null,
    trend_evidence_count integer not null,
    current_stage text,
    trigger_reasons jsonb not null default '[]'::jsonb
);

create table if not exists push_log (
    id text primary key,
    event_id text references event (id) on delete set null,
    push_type text not null,
    pushed_at timestamptz not null,
    title text not null,
    reason text not null,
    content text not null,
    payload jsonb not null default '{}'::jsonb,
    dedup_key text not null,
    success boolean not null,
    error text,
    constraint uq_push_log_dedup_key unique (dedup_key)
);

create table if not exists app_state (
    id text primary key,
    key text not null,
    value text not null,
    updated_at timestamptz not null,
    constraint uq_app_state_key unique (key)
);

create index if not exists ix_source_category on source (category);
create index if not exists ix_source_updated_at on source (updated_at);

create index if not exists ix_content_item_source on content_item (source);
create index if not exists ix_content_item_source_item_id on content_item (source_item_id);
create index if not exists ix_content_item_category on content_item (category);
create index if not exists ix_content_item_created_at on content_item (created_at);
create index if not exists ix_content_item_updated_at on content_item (updated_at);
create index if not exists ix_content_item_last_seen_run_id on content_item (last_seen_run_id);
create index if not exists ix_content_item_last_seen_at on content_item (last_seen_at);
create index if not exists ix_content_item_last_seen_rank on content_item (last_seen_rank);
create index if not exists ix_content_item_need_enrichment on content_item (need_enrichment);
create index if not exists ix_content_item_enrichment_status on content_item (enrichment_status);
create index if not exists ix_content_item_enrichment_tried_at on content_item (enrichment_tried_at);
create index if not exists ix_content_item_summary_source on content_item (summary_source);

create index if not exists ix_content_snapshot_run_id on content_snapshot (run_id);
create index if not exists ix_content_snapshot_content_item_id on content_snapshot (content_item_id);
create index if not exists ix_content_snapshot_source on content_snapshot (source);
create index if not exists ix_content_snapshot_category on content_snapshot (category);
create index if not exists ix_content_snapshot_visual_order on content_snapshot (visual_order);
create index if not exists ix_content_snapshot_captured_at on content_snapshot (captured_at);

create index if not exists ix_event_status on event (status);
create index if not exists ix_event_type on event (type);
create index if not exists ix_event_last_seen_at on event (last_seen_at);
create index if not exists ix_event_is_blacklisted on event (is_blacklisted);
create index if not exists ix_event_updated_at on event (updated_at);

create index if not exists ix_event_item_event_id on event_item (event_id);
create index if not exists ix_event_item_content_item_id on event_item (content_item_id);
create index if not exists ix_event_item_matched_at on event_item (matched_at);

create index if not exists ix_event_score_snapshot_event_id on event_score_snapshot (event_id);
create index if not exists ix_event_score_snapshot_run_id on event_score_snapshot (run_id);
create index if not exists ix_event_score_snapshot_calculated_at on event_score_snapshot (calculated_at);
create index if not exists ix_event_score_snapshot_total_score on event_score_snapshot (total_score);

create index if not exists ix_push_log_event_id on push_log (event_id);
create index if not exists ix_push_log_push_type on push_log (push_type);
create index if not exists ix_push_log_pushed_at on push_log (pushed_at);

create index if not exists ix_fetch_run_started_at on fetch_run (started_at);
create index if not exists ix_fetch_run_status on fetch_run (status);

create index if not exists ix_app_state_key on app_state (key);
create index if not exists ix_app_state_updated_at on app_state (updated_at);
