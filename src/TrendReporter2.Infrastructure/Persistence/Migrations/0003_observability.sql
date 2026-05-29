alter table fetch_run
    add column if not exists estimated_llm_cost numeric(18, 8) not null default 0;

create table if not exists fetch_run_source (
    run_id text not null references fetch_run (id) on delete cascade,
    source_id text not null,
    category text not null,
    source text not null,
    status text not null,
    duration_ms integer not null,
    item_count integer not null default 0,
    error text,
    created_at timestamptz not null,
    constraint pk_fetch_run_source primary key (run_id, source_id)
);

create table if not exists fetch_run_stage (
    id text primary key,
    run_id text not null references fetch_run (id) on delete cascade,
    stage text not null,
    started_at timestamptz not null,
    finished_at timestamptz not null,
    duration_ms integer not null,
    status text not null,
    error text
);

create table if not exists llm_usage (
    id text primary key,
    run_id text references fetch_run (id) on delete set null,
    stage text not null,
    model text not null,
    request_id text,
    content_item_id text references content_item (id) on delete set null,
    event_id text references event (id) on delete set null,
    input_tokens integer,
    output_tokens integer,
    cache_read_tokens integer,
    estimated_cost numeric(18, 8) not null default 0,
    duration_ms integer not null,
    success boolean not null,
    retry_count integer not null default 0,
    error text,
    created_at timestamptz not null
);

create index if not exists ix_fetch_run_source_run_id on fetch_run_source (run_id);
create index if not exists ix_fetch_run_source_status on fetch_run_source (status);
create index if not exists ix_fetch_run_stage_run_stage on fetch_run_stage (run_id, stage);
create index if not exists ix_fetch_run_stage_started_at on fetch_run_stage (started_at);
create index if not exists ix_llm_usage_run_stage on llm_usage (run_id, stage);
create index if not exists ix_llm_usage_created_at on llm_usage (created_at desc);
create index if not exists ix_llm_usage_model_created_at on llm_usage (model, created_at desc);
