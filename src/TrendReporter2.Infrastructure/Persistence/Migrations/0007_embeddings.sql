create table if not exists content_embedding (
    content_item_id text not null references content_item (id) on delete cascade,
    model text not null,
    version text not null,
    dimensions integer not null,
    source_text_hash text not null,
    embedding vector(768) not null,
    created_at timestamptz not null,
    updated_at timestamptz not null,
    constraint pk_content_embedding primary key (content_item_id, model, version, dimensions)
);

create table if not exists event_embedding (
    event_id text not null references event (id) on delete cascade,
    model text not null,
    version text not null,
    dimensions integer not null,
    source_text_hash text not null,
    embedding vector(768) not null,
    created_at timestamptz not null,
    updated_at timestamptz not null,
    constraint pk_event_embedding primary key (event_id, model, version, dimensions)
);

create index if not exists ix_content_embedding_model_version_dimensions on content_embedding (model, version, dimensions);
create index if not exists ix_content_embedding_source_text_hash on content_embedding (source_text_hash);
create index if not exists ix_content_embedding_updated_at on content_embedding (updated_at desc);

create index if not exists ix_event_embedding_model_version_dimensions on event_embedding (model, version, dimensions);
create index if not exists ix_event_embedding_source_text_hash on event_embedding (source_text_hash);
create index if not exists ix_event_embedding_updated_at on event_embedding (updated_at desc);
create index if not exists ix_event_embedding_embedding_hnsw_cosine on event_embedding using hnsw (embedding vector_cosine_ops);
