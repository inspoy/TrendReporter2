using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using TrendReporter2.Infrastructure.Persistence;

namespace TrendReporter2.Tests;

public sealed class SqlMigrationRunnerTests
{
    [Fact]
    public void DiscoverMigrations_SortsOrdinallyByFileName()
    {
        using var directory = TempDirectory.Create();
        WriteMigration(directory.Path, "0002_second.sql", "select 2;");
        WriteMigration(directory.Path, "0001_init.sql", "select 1;");

        var migrations = SqlMigrationRunner.DiscoverMigrations(directory.Path);

        Assert.Equal(["0001_init.sql", "0002_second.sql"], migrations.Select(migration => migration.FileName).ToArray());
    }

    [Fact]
    public void ParseMigrationFile_ExtractsVersionAndName()
    {
        using var directory = TempDirectory.Create();
        var path = WriteMigration(directory.Path, "0001_init_schema.sql", "select 1;");

        var migration = SqlMigrationRunner.ParseMigrationFile(path);

        Assert.Equal("0001", migration.Version);
        Assert.Equal("init_schema", migration.Name);
        Assert.Equal("0001_init_schema.sql", migration.FileName);
    }

    [Fact]
    public void ParseMigrationFile_RejectsInvalidFileName()
    {
        using var directory = TempDirectory.Create();
        var path = WriteMigration(directory.Path, "init.sql", "select 1;");

        var exception = Assert.Throws<InvalidOperationException>(() => SqlMigrationRunner.ParseMigrationFile(path));

        Assert.Contains("must match '<version>_<name>.sql'", exception.Message);
    }

    [Fact]
    public void ComputeChecksum_NormalizesLineEndingsAndBom()
    {
        var unixChecksum = SqlMigrationRunner.ComputeChecksum("select 1;\n");
        var windowsChecksum = SqlMigrationRunner.ComputeChecksum("select 1;\r\n");
        var bomChecksum = SqlMigrationRunner.ComputeChecksum("\uFEFFselect 1;\n");

        Assert.Equal(unixChecksum, windowsChecksum);
        Assert.Equal(unixChecksum, bomChecksum);
    }

    [Fact]
    public void EnsureChecksumMatches_FailsWhenAppliedChecksumDiffers()
    {
        var migration = new SqlMigration("/tmp/0001_init.sql", "0001_init.sql", "0001", "init", "new-checksum");
        var applied = new AppliedSqlMigration("0001", "init", "old-checksum");

        var exception = Assert.Throws<InvalidOperationException>(() => SqlMigrationRunner.EnsureChecksumMatches(migration, applied));

        Assert.Contains("checksum mismatch", exception.Message);
        Assert.Contains("0001", exception.Message);
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task RunAsync_WhenPostgresIsAvailable_AppliesMigrationsIdempotently()
    {
        var connectionString = Environment.GetEnvironmentVariable("TRENDREPORTER2_POSTGRES_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        using var directory = TempDirectory.Create();
        WriteMigration(directory.Path, "0001_init.sql", "create table sample_item (id integer primary key, name text not null);");
        WriteMigration(directory.Path, "0002_seed.sql", "insert into sample_item (id, name) values (1, 'first');");

        await using var adminDataSource = NpgsqlDataSource.Create(connectionString);
        var schema = "tr_migration_test_" + Guid.NewGuid().ToString("N");
        await using var adminConnection = await adminDataSource.OpenConnectionAsync();
        await adminConnection.ExecuteAsync($"create schema {QuoteIdentifier(schema)};");

        try
        {
            var builder = new NpgsqlConnectionStringBuilder(connectionString)
            {
                SearchPath = schema
            };
            await using var dataSource = NpgsqlDataSource.Create(builder.ConnectionString);
            var runner = new SqlMigrationRunner(dataSource, NullLoggerFactory.Instance, directory.Path);

            var first = await runner.RunAsync(CancellationToken.None);
            var second = await runner.RunAsync(CancellationToken.None);

            Assert.Equal(new SqlMigrationRunResult(2, 0), first);
            Assert.Equal(new SqlMigrationRunResult(0, 2), second);
            await using var verifyConnection = await dataSource.OpenConnectionAsync();
            var itemCount = await verifyConnection.ExecuteScalarAsync<int>("select count(*) from sample_item;");
            var migrationCount = await verifyConnection.ExecuteScalarAsync<int>("select count(*) from schema_migration;");
            Assert.Equal(1, itemCount);
            Assert.Equal(2, migrationCount);
        }
        finally
        {
            await adminConnection.ExecuteAsync($"drop schema if exists {QuoteIdentifier(schema)} cascade;");
        }
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task RunAsync_WhenPostgresIsAvailable_FailsOnChecksumMismatch()
    {
        var connectionString = Environment.GetEnvironmentVariable("TRENDREPORTER2_POSTGRES_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        using var directory = TempDirectory.Create();
        var migrationPath = WriteMigration(directory.Path, "0001_init.sql", "create table checksum_item (id integer primary key);");

        await using var adminDataSource = NpgsqlDataSource.Create(connectionString);
        var schema = "tr_migration_test_" + Guid.NewGuid().ToString("N");
        await using var adminConnection = await adminDataSource.OpenConnectionAsync();
        await adminConnection.ExecuteAsync($"create schema {QuoteIdentifier(schema)};");

        try
        {
            var builder = new NpgsqlConnectionStringBuilder(connectionString)
            {
                SearchPath = schema
            };
            await using var dataSource = NpgsqlDataSource.Create(builder.ConnectionString);
            var runner = new SqlMigrationRunner(dataSource, NullLoggerFactory.Instance, directory.Path);

            await runner.RunAsync(CancellationToken.None);
            await File.WriteAllTextAsync(migrationPath, "create table checksum_item (id bigint primary key);", CancellationToken.None);

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(CancellationToken.None));

            Assert.Contains("checksum mismatch", exception.Message);
        }
        finally
        {
            await adminConnection.ExecuteAsync($"drop schema if exists {QuoteIdentifier(schema)} cascade;");
        }
    }

    private static string WriteMigration(string directory, string fileName, string content)
    {
        var path = Path.Combine(directory, fileName);
        File.WriteAllText(path, content);
        return path;
    }

    private static string QuoteIdentifier(string identifier)
        => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "TrendReporter2.Tests", Guid.NewGuid().ToString("N"));

        private TempDirectory()
        {
            Directory.CreateDirectory(Path);
        }

        public static TempDirectory Create()
            => new();

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
