alter table source
    add column if not exists provider text,
    add column if not exists external_id text,
    add column if not exists content_kind text,
    add column if not exists weight double precision not null default 1.0;

update source
set provider = coalesce(nullif(provider, ''), 'newsnow'),
    external_id = coalesce(nullif(external_id, ''), nullif(name, ''), id),
    content_kind = coalesce(nullif(content_kind, ''), 'ranked_news'),
    display_name = coalesce(nullif(display_name, ''), nullif(name, ''), id),
    weight = coalesce(weight, 1.0),
    updated_at = coalesce(updated_at, now());

alter table source
    alter column provider set not null,
    alter column external_id set not null,
    alter column content_kind set not null,
    alter column display_name set not null;

alter table source drop constraint if exists uq_source_category_name;

do $$
begin
    if not exists (
        select 1
        from pg_constraint
        where conname = 'ck_source_content_kind'
          and conrelid = 'source'::regclass
    ) then
        alter table source
            add constraint ck_source_content_kind check (content_kind in ('ranked_news', 'flash_feed', 'topic'));
    end if;

    if not exists (
        select 1
        from pg_constraint
        where conname = 'uq_source_provider_external_kind'
          and conrelid = 'source'::regclass
    ) then
        alter table source
            add constraint uq_source_provider_external_kind unique (provider, external_id, content_kind);
    end if;
end $$;

alter table content_item
    add column if not exists source_id text references source (id) on delete set null,
    add column if not exists content_kind text not null default 'ranked_news';

update content_item ci
set source_id = s.id
from source s
where ci.source_id is null
  and s.provider = 'newsnow'
  and s.external_id = ci.source
  and s.content_kind = ci.content_kind;

do $$
begin
    if not exists (
        select 1
        from pg_constraint
        where conname = 'ck_content_item_content_kind'
          and conrelid = 'content_item'::regclass
    ) then
        alter table content_item
            add constraint ck_content_item_content_kind check (content_kind in ('ranked_news', 'flash_feed', 'topic'));
    end if;
end $$;

alter table content_snapshot
    add column if not exists source_id text references source (id) on delete set null,
    add column if not exists content_kind text not null default 'ranked_news',
    alter column rank drop not null,
    alter column source_list_size drop not null,
    alter column normalized_rank_score drop not null,
    add column if not exists freshness_score double precision not null default 0;

update content_snapshot cs
set source_id = s.id
from source s
where cs.source_id is null
  and s.provider = 'newsnow'
  and s.external_id = cs.source
  and s.content_kind = cs.content_kind;

do $$
begin
    if not exists (
        select 1
        from pg_constraint
        where conname = 'ck_content_snapshot_content_kind'
          and conrelid = 'content_snapshot'::regclass
    ) then
        alter table content_snapshot
            add constraint ck_content_snapshot_content_kind check (content_kind in ('ranked_news', 'flash_feed', 'topic'));
    end if;
end $$;

alter table event_score_snapshot
    add column if not exists flash_score double precision not null default 0,
    add column if not exists freshness_score double precision not null default 0,
    add column if not exists ranked_source_count integer not null default 0,
    add column if not exists flash_source_count integer not null default 0;

update event_score_snapshot
set ranked_source_count = unique_source_count
where ranked_source_count = 0
  and unique_source_count > 0;

create index if not exists ix_source_provider_external_kind on source (provider, external_id, content_kind);
create index if not exists ix_source_content_kind on source (content_kind);
create index if not exists ix_source_provider_enabled on source (provider, enabled);

create index if not exists ix_content_item_source_id on content_item (source_id);
create index if not exists ix_content_item_content_kind on content_item (content_kind);
create index if not exists ix_content_item_source_id_kind on content_item (source_id, content_kind);

create index if not exists ix_content_snapshot_source_id on content_snapshot (source_id);
create index if not exists ix_content_snapshot_content_kind on content_snapshot (content_kind);
create index if not exists ix_content_snapshot_source_kind_captured_at on content_snapshot (source_id, content_kind, captured_at desc);
create index if not exists ix_content_snapshot_freshness_score on content_snapshot (freshness_score desc);

create index if not exists ix_event_score_snapshot_flash_score on event_score_snapshot (flash_score desc);
create index if not exists ix_event_score_snapshot_freshness_score on event_score_snapshot (freshness_score desc);
