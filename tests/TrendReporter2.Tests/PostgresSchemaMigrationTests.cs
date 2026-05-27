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

            Assert.Equal(new SqlMigrationRunResult(1, 0), result);
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

            Assert.Contains("schema_migration", tables);
            Assert.Contains("uq_content_item_dedup_key", constraintNames);
            Assert.Contains("uq_event_item_dedup_key", constraintNames);
            Assert.Contains("uq_event_item_content_item_id", constraintNames);
            Assert.Contains("uq_push_log_dedup_key", constraintNames);
            Assert.Contains("uq_app_state_key", constraintNames);

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
