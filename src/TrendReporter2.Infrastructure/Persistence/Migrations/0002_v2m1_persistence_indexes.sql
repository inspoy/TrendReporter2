do $$
begin
    if exists (
        select 1
        from content_snapshot
        group by run_id, content_item_id
        having count(*) > 1
    ) then
        raise exception 'content_snapshot contains duplicate (run_id, content_item_id) rows; clean duplicates before applying V2M1 migration.';
    end if;

    if not exists (
        select 1
        from pg_constraint
        where conname = 'uq_content_snapshot_run_content'
          and conrelid = 'content_snapshot'::regclass
    ) then
        alter table content_snapshot
            add constraint uq_content_snapshot_run_content unique (run_id, content_item_id);
    end if;
end $$;

create index if not exists ix_source_enabled on source (enabled);
create index if not exists ix_content_item_enrichment_candidates on content_item (last_seen_run_id, need_enrichment, enrichment_status, enrichment_tried_at);
create index if not exists ix_content_snapshot_run_rank on content_snapshot (run_id, rank, source, content_item_id);
create index if not exists ix_event_status_last_seen_at on event (status, last_seen_at);
create index if not exists ix_event_score_snapshot_digest on event_score_snapshot (event_id, calculated_at desc, total_score desc, id);
