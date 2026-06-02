create table if not exists event_merge_history (
    id text primary key,
    source_event_id text not null references event (id) on delete cascade,
    target_event_id text not null references event (id) on delete cascade,
    confidence double precision not null,
    reason text not null,
    decided_by text not null,
    evidence_snapshot jsonb not null default '{}'::jsonb,
    created_at timestamptz not null,
    constraint chk_event_merge_history_not_same check (source_event_id <> target_event_id),
    constraint uq_event_merge_history_pair unique (source_event_id, target_event_id)
);

alter table event
    add column if not exists merged_into_event_id text,
    add constraint chk_event_not_merge_self check (merged_into_event_id is null or merged_into_event_id <> id),
    drop constraint if exists event_status_check,
    add constraint event_status_check check (status in ('Active', 'Stale', 'Merged'));

alter table event_item
    add column if not exists is_active boolean not null default true,
    add column if not exists created_by_merge_id text;

create index if not exists ix_event_merged_into_event_id on event (merged_into_event_id);
create index if not exists ix_event_merge_history_source_event_id on event_merge_history (source_event_id);
create index if not exists ix_event_merge_history_target_event_id on event_merge_history (target_event_id);
create index if not exists ix_event_merge_history_created_at on event_merge_history (created_at desc);
create index if not exists ix_event_item_active on event_item (event_id, is_active);
create index if not exists ix_event_item_created_by_merge_id on event_item (created_by_merge_id);

update event_item set is_active = true where is_active is null;
