create table if not exists tag (
    id text primary key,
    name text not null,
    display_name text not null,
    category text not null,
    created_at timestamptz not null,
    constraint uq_tag_name unique (name)
);

create table if not exists content_item_tag (
    content_item_id text not null references content_item (id) on delete cascade,
    tag_id text not null references tag (id) on delete cascade,
    confidence double precision not null,
    source text not null,
    created_at timestamptz not null,
    constraint pk_content_item_tag primary key (content_item_id, tag_id)
);

create table if not exists event_tag (
    event_id text not null references event (id) on delete cascade,
    tag_id text not null references tag (id) on delete cascade,
    confidence double precision not null,
    source text not null,
    created_at timestamptz not null,
    constraint pk_event_tag primary key (event_id, tag_id)
);

create table if not exists report_snapshot (
    id text primary key,
    report_type text not null,
    slot_time timestamptz not null,
    generated_at timestamptz not null,
    file_path text not null,
    public_url text,
    event_count integer not null,
    payload_json jsonb not null default '{}'::jsonb
);

do $$
begin
    if not exists (
        select 1
        from pg_constraint
        where conname = 'ck_tag_category'
          and conrelid = 'tag'::regclass
    ) then
        alter table tag
            add constraint ck_tag_category check (category in ('topic', 'entity', 'domain', 'risk'));
    end if;

    if not exists (
        select 1
        from pg_constraint
        where conname = 'ck_content_item_tag_source'
          and conrelid = 'content_item_tag'::regclass
    ) then
        alter table content_item_tag
            add constraint ck_content_item_tag_source check (source in ('web_extract', 'rule', 'llm', 'manual'));
    end if;

    if not exists (
        select 1
        from pg_constraint
        where conname = 'ck_event_tag_source'
          and conrelid = 'event_tag'::regclass
    ) then
        alter table event_tag
            add constraint ck_event_tag_source check (source in ('web_extract', 'rule', 'llm', 'manual'));
    end if;
end $$;

create index if not exists ix_tag_category on tag (category);
create index if not exists ix_content_item_tag_tag_id on content_item_tag (tag_id);
create index if not exists ix_content_item_tag_source on content_item_tag (source);
create index if not exists ix_event_tag_tag_id on event_tag (tag_id);
create index if not exists ix_event_tag_source on event_tag (source);
create index if not exists ix_report_snapshot_slot_time on report_snapshot (slot_time desc);
create index if not exists ix_report_snapshot_generated_at on report_snapshot (generated_at desc);
create index if not exists ix_report_snapshot_report_type on report_snapshot (report_type);
