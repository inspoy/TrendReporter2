using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using TrendReporter2.Infrastructure.Persistence;

namespace TrendReporter2.Tests;

public sealed class PostgresSchemaMigrationTests
{
    private static readonly string[] RequiredTables =
    [
        "source",
        "content_item",
        "content_snapshot",
        "event",
        "event_item",
        "event_score_snapshot",
        "push_log",
        "fetch_run",
        "app_state"
    ];

    private static readonly string[] RequiredIndexes =
    [
        "ix_content_item_source",
        "ix_content_item_category",
        "ix_content_item_last_seen_at",
        "ix_content_item_enrichment_status",
        "ix_content_snapshot_run_id",
        "ix_content_snapshot_content_item_id",
        "ix_content_snapshot_captured_at",
        "ix_event_status",
        "ix_event_type",
        "ix_event_last_seen_at",
        "ix_event_is_blacklisted",
        "ix_event_updated_at",
        "ix_event_item_event_id",
        "ix_event_item_content_item_id",
        "ix_event_item_matched_at",
        "ix_event_score_snapshot_event_id",
        "ix_event_score_snapshot_run_id",
        "ix_event_score_snapshot_calculated_at",
        "ix_event_score_snapshot_total_score",
        "ix_push_log_event_id",
        "ix_push_log_push_type",
        "ix_push_log_pushed_at",
        "ix_fetch_run_started_at",
        "ix_fetch_run_status",
        "ix_app_state_updated_at"
    ];

    private static readonly string[] ObservabilityTables =
    [
        "fetch_run_source",
        "fetch_run_stage",
        "llm_usage"
    ];

    private static readonly string[] ObservabilityIndexes =
    [
        "ix_fetch_run_source_run_id",
        "ix_fetch_run_source_status",
        "ix_fetch_run_stage_run_stage",
        "ix_fetch_run_stage_started_at",
        "ix_llm_usage_run_stage",
        "ix_llm_usage_created_at",
        "ix_llm_usage_model_created_at"
    ];

    private static readonly string[] SourceAndFlashIndexes =
    [
        "ix_source_provider_external_kind",
        "ix_source_content_kind",
        "ix_source_provider_enabled",
        "ix_content_item_source_id",
        "ix_content_item_content_kind",
        "ix_content_item_source_id_kind",
        "ix_content_snapshot_source_id",
        "ix_content_snapshot_content_kind",
        "ix_content_snapshot_source_kind_captured_at",
        "ix_content_snapshot_freshness_score",
        "ix_event_score_snapshot_flash_score",
        "ix_event_score_snapshot_freshness_score"
    ];

    private static readonly string[] TagAndReportTables =
    [
        "tag",
        "content_item_tag",
        "event_tag",
        "report_snapshot"
    ];

    private static readonly string[] TagAndReportIndexes =
    [
        "ix_tag_category",
        "ix_content_item_tag_tag_id",
        "ix_event_tag_tag_id",
        "ix_report_snapshot_slot_time",
        "ix_report_snapshot_generated_at",
        "ix_report_snapshot_report_type"
    ];

    [Fact]
    public void InitMigration_DefinesRequiredExtensionTablesAndRunnerCompatibility()
    {
        var sql = ReadInitMigrationSql();
        var normalized = NormalizeSql(sql);

        Assert.Contains("create extension if not exists vector", normalized);
        Assert.Contains("create table if not exists schema_migration", normalized);
        Assert.Contains("version text primary key", normalized);
        Assert.Contains("name text not null", normalized);
        Assert.Contains("checksum text not null", normalized);
        Assert.Contains("applied_at timestamptz not null default now()", normalized);

        foreach (var table in RequiredTables)
        {
            Assert.Contains($"create table if not exists {table}", normalized);
            Assert.Contains("id text primary key", TableBody(normalized, table));
        }
    }

    [Fact]
    public void InitMigration_UsesTimestamptzAndJsonbForDomainPayloads()
    {
        var normalized = NormalizeSql(ReadInitMigrationSql());

        Assert.Contains("pub_time timestamptz", TableBody(normalized, "content_item"));
        Assert.Contains("created_at timestamptz not null", TableBody(normalized, "content_item"));
        Assert.Contains("raw_payload jsonb not null default '{}'::jsonb", TableBody(normalized, "content_item"));
        Assert.Contains("captured_at timestamptz not null", TableBody(normalized, "content_snapshot"));
        Assert.Contains("aliases jsonb not null default '[]'::jsonb", TableBody(normalized, "event"));
        Assert.Contains("milestones jsonb not null default '[]'::jsonb", TableBody(normalized, "event"));
        Assert.Contains("matched_at timestamptz not null", TableBody(normalized, "event_item"));
        Assert.Contains("trigger_reasons jsonb not null default '[]'::jsonb", TableBody(normalized, "event_score_snapshot"));
        Assert.Contains("payload jsonb not null default '{}'::jsonb", TableBody(normalized, "push_log"));
        Assert.Contains("errors jsonb not null default '[]'::jsonb", TableBody(normalized, "fetch_run"));
        Assert.Contains("updated_at timestamptz not null", TableBody(normalized, "app_state"));
    }

    [Fact]
    public void InitMigration_DefinesRequiredUniqueConstraintsAndIndexes()
    {
        var normalized = NormalizeSql(ReadInitMigrationSql());

        Assert.Contains("constraint uq_content_item_dedup_key unique (dedup_key)", normalized);
        Assert.Contains("constraint uq_event_item_dedup_key unique (dedup_key)", normalized);
        Assert.Contains("constraint uq_event_item_content_item_id unique (content_item_id)", normalized);
        Assert.Contains("constraint uq_push_log_dedup_key unique (dedup_key)", normalized);
        Assert.Contains("constraint uq_app_state_key unique (key)", normalized);

        foreach (var index in RequiredIndexes)
        {
            Assert.Contains($"create index if not exists {index}", normalized);
        }
    }

    [Fact]
    public void ObservabilityMigration_DefinesSourceStageAndLlmUsageTables()
    {
        var normalized = NormalizeSql(File.ReadAllText(Path.Combine(InitMigrationDirectory(), "0003_observability.sql")));

        Assert.Contains("alter table fetch_run add column if not exists estimated_llm_cost numeric(18, 8) not null default 0", normalized);
        foreach (var table in ObservabilityTables)
        {
            Assert.Contains($"create table if not exists {table}", normalized);
        }

        Assert.Contains("constraint pk_fetch_run_source primary key (run_id, source_id)", normalized);
        Assert.Contains("run_id text references fetch_run (id) on delete set null", TableBody(normalized, "llm_usage"));
        Assert.Contains("retry_count integer not null default 0", TableBody(normalized, "llm_usage"));
        foreach (var index in ObservabilityIndexes)
        {
            Assert.Contains($"create index if not exists {index}", normalized);
        }
    }

    [Fact]
    public void SourceAndFlashMigration_EvolvesSourcesContentAndScoresSafely()
    {
        var normalized = NormalizeSql(File.ReadAllText(Path.Combine(InitMigrationDirectory(), "0004_sources_and_flash.sql")));

        Assert.Contains("alter table source add column if not exists provider text", normalized);
        Assert.Contains("add column if not exists external_id text", normalized);
        Assert.Contains("add column if not exists content_kind text", normalized);
        Assert.Contains("add column if not exists weight double precision not null default 1.0", normalized);
        Assert.Contains("display_name = coalesce(nullif(display_name, ''), nullif(name, ''), id)", normalized);
        Assert.Contains("constraint ck_source_content_kind check (content_kind in ('ranked_news', 'flash_feed', 'topic'))", normalized);
        Assert.Contains("constraint uq_source_provider_external_kind unique (provider, external_id, content_kind)", normalized);

        Assert.Contains("alter table content_item add column if not exists source_id text references source (id) on delete set null", normalized);
        Assert.Contains("add column if not exists content_kind text not null default 'ranked_news'", normalized);
        Assert.Contains("constraint ck_content_item_content_kind check (content_kind in ('ranked_news', 'flash_feed', 'topic'))", normalized);

        Assert.Contains("alter table content_snapshot add column if not exists source_id text references source (id) on delete set null", normalized);
        Assert.Contains("alter column rank drop not null", normalized);
        Assert.Contains("alter column source_list_size drop not null", normalized);
        Assert.Contains("alter column normalized_rank_score drop not null", normalized);
        Assert.Contains("add column if not exists freshness_score double precision not null default 0", normalized);
        Assert.Contains("constraint ck_content_snapshot_content_kind check (content_kind in ('ranked_news', 'flash_feed', 'topic'))", normalized);

        Assert.Contains("alter table event_score_snapshot add column if not exists flash_score double precision not null default 0", normalized);
        Assert.Contains("add column if not exists freshness_score double precision not null default 0", normalized);
        Assert.Contains("add column if not exists ranked_source_count integer not null default 0", normalized);
        Assert.Contains("add column if not exists flash_source_count integer not null default 0", normalized);

        foreach (var index in SourceAndFlashIndexes)
        {
            Assert.Contains($"create index if not exists {index}", normalized);
        }
    }

    [Fact]
    public void SummaryTextUnificationMigration_BackfillsAndDropsHoverText()
    {
        var normalized = NormalizeSql(File.ReadAllText(Path.Combine(InitMigrationDirectory(), "0005_summary_text_unification.sql")));

        Assert.Contains("set summary = hover_text", normalized);
        Assert.Contains("summary_source = 'summarytext'", normalized);
        Assert.Contains("nullif(summary, '') is null", normalized);
        Assert.Contains("nullif(hover_text, '') is not null", normalized);
        Assert.Contains("alter table content_item drop column hover_text", normalized);
        Assert.Contains("where summary_source = 'hovertext'", normalized);
    }

    [Fact]
    public void TagsAndReportsMigration_DefinesTagMappingsAndReportSnapshots()
    {
        var normalized = NormalizeSql(File.ReadAllText(Path.Combine(InitMigrationDirectory(), "0006_tags_and_reports.sql")));

        foreach (var table in TagAndReportTables)
        {
            Assert.Contains($"create table if not exists {table}", normalized);
        }

        Assert.Contains("constraint uq_tag_name unique (name)", normalized);
        Assert.Contains("constraint pk_content_item_tag primary key (content_item_id, tag_id)", normalized);
        Assert.Contains("constraint pk_event_tag primary key (event_id, tag_id)", normalized);
        Assert.Contains("payload_json jsonb not null default '{}'::jsonb", TableBody(normalized, "report_snapshot"));
        Assert.Contains("constraint ck_tag_category check (category in ('topic', 'entity', 'domain', 'risk'))", normalized);
        Assert.Contains("constraint ck_content_item_tag_source check (source in ('web_extract', 'rule', 'llm', 'manual'))", normalized);

        foreach (var index in TagAndReportIndexes)
        {
            Assert.Contains($"create index if not exists {index}", normalized);
        }
    }


    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task RunAsync_WhenPostgresIsAvailable_AppliesRealInitSchema()
    {
        var connectionString = Environment.GetEnvironmentVariable("TRENDREPORTER2_POSTGRES_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        await using var adminDataSource = NpgsqlDataSource.Create(connectionString);
        var schema = "tr_schema_test_" + Guid.NewGuid().ToString("N");
        await using var adminConnection = await adminDataSource.OpenConnectionAsync();
        await adminConnection.ExecuteAsync($"create schema {QuoteIdentifier(schema)};");

        try
        {
            var builder = new NpgsqlConnectionStringBuilder(connectionString)
            {
                SearchPath = schema
            };
            await using var dataSource = NpgsqlDataSource.Create(builder.ConnectionString);
            var runner = new SqlMigrationRunner(dataSource, NullLoggerFactory.Instance, InitMigrationDirectory());

            var result = await runner.RunAsync(CancellationToken.None);

            Assert.Equal(new SqlMigrationRunResult(6, 0), result);
            await using var verifyConnection = await dataSource.OpenConnectionAsync();
            var tables = (await verifyConnection.QueryAsync<string>("""
            select table_name
            from information_schema.tables
            where table_schema = current_schema()
            order by table_name;
            """)).ToHashSet(StringComparer.Ordinal);
            var constraintNames = (await verifyConnection.QueryAsync<string>("""
            select conname
            from pg_constraint c
            join pg_namespace n on n.oid = c.connamespace
            where n.nspname = current_schema();
            """)).ToHashSet(StringComparer.Ordinal);

            foreach (var table in RequiredTables)
            {
                Assert.Contains(table, tables);
            }

            foreach (var table in ObservabilityTables)
            {
                Assert.Contains(table, tables);
            }

            foreach (var table in TagAndReportTables)
            {
                Assert.Contains(table, tables);
            }

            var contentItemColumns = (await verifyConnection.QueryAsync<string>("""
            select column_name
            from information_schema.columns
            where table_schema = current_schema()
              and table_name = 'content_item';
            """)).ToHashSet(StringComparer.Ordinal);
            Assert.DoesNotContain("hover_text", contentItemColumns);

            Assert.Contains("schema_migration", tables);
            Assert.Contains("uq_content_item_dedup_key", constraintNames);
            Assert.Contains("uq_event_item_dedup_key", constraintNames);
            Assert.Contains("uq_event_item_content_item_id", constraintNames);
            Assert.Contains("uq_push_log_dedup_key", constraintNames);
            Assert.Contains("uq_app_state_key", constraintNames);
            Assert.Contains("pk_fetch_run_source", constraintNames);
            Assert.Contains("uq_source_provider_external_kind", constraintNames);
            Assert.Contains("ck_source_content_kind", constraintNames);
            Assert.Contains("ck_content_item_content_kind", constraintNames);
            Assert.Contains("ck_content_snapshot_content_kind", constraintNames);
            Assert.Contains("uq_tag_name", constraintNames);
            Assert.Contains("pk_content_item_tag", constraintNames);
            Assert.Contains("pk_event_tag", constraintNames);

            var migrationName = await verifyConnection.QuerySingleAsync<string>("""
            select name
            from schema_migration
            where version = '0001';
            """);
            Assert.Equal("init", migrationName);

            var hasVector = await verifyConnection.ExecuteScalarAsync<bool>("""
            select exists (select 1 from pg_extension where extname = 'vector');
            """);
            Assert.True(hasVector, "PostgreSQL test database must allow CREATE EXTENSION vector; install pgvector or grant extension creation privileges.");
        }
        finally
        {
            await adminConnection.ExecuteAsync($"drop schema if exists {QuoteIdentifier(schema)} cascade;");
        }
    }

    private static string ReadInitMigrationSql()
        => File.ReadAllText(Path.Combine(InitMigrationDirectory(), "0001_init.sql"));

    private static string InitMigrationDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var migrationDirectory = Path.Combine(directory.FullName, "src", "TrendReporter2.Infrastructure", "Persistence", "Migrations");
            if (Directory.Exists(migrationDirectory))
            {
                return migrationDirectory;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not find src/TrendReporter2.Infrastructure/Persistence/Migrations.");
    }

    private static string NormalizeSql(string sql)
        => System.Text.RegularExpressions.Regex.Replace(sql.ToLowerInvariant(), @"\s+", " ");

    private static string TableBody(string normalizedSql, string tableName)
    {
        var startMarker = $"create table if not exists {tableName} (";
        var start = normalizedSql.IndexOf(startMarker, StringComparison.Ordinal);
        Assert.True(start >= 0, $"Migration should create table {tableName}.");
        start += startMarker.Length;
        var end = normalizedSql.IndexOf(");", start, StringComparison.Ordinal);
        Assert.True(end >= 0, $"Migration table {tableName} should end with a semicolon.");
        return normalizedSql[start..end];
    }

    private static string QuoteIdentifier(string identifier)
        => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
