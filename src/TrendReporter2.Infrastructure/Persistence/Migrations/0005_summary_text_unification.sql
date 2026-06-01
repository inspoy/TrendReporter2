do $$
begin
    if exists (
        select 1
        from information_schema.columns
        where table_schema = current_schema()
          and table_name = 'content_item'
          and column_name = 'hover_text'
    ) then
        update content_item
        set summary = hover_text,
            summary_source = 'SummaryText'
        where nullif(summary, '') is null
          and nullif(hover_text, '') is not null;

        alter table content_item drop column hover_text;
    end if;

    update content_item
    set summary_source = 'SummaryText'
    where summary_source = 'HoverText';
end $$;
