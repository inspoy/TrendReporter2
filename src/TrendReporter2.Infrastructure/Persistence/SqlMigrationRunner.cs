using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Dapper;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace TrendReporter2.Infrastructure.Persistence;

public sealed class SqlMigrationRunner
{
    public const string DefaultRelativeMigrationDirectory = "Persistence/Migrations";

    private const long AdvisoryLockKey = 7845124021934721;
    private static readonly Regex MigrationFileNamePattern = new(@"^(?<version>\d+)_(?<name>.+)\.sql$", RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture);

    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger _logger;
    private readonly string _migrationsDirectory;

    public SqlMigrationRunner(NpgsqlDataSource dataSource, ILoggerFactory loggerFactory)
        : this(dataSource, loggerFactory, Path.Combine(AppContext.BaseDirectory, DefaultRelativeMigrationDirectory))
    {
    }

    public SqlMigrationRunner(NpgsqlDataSource dataSource, ILoggerFactory loggerFactory, string migrationsDirectory)
    {
        _dataSource = dataSource;
        _logger = loggerFactory.CreateLogger("Postgres.Migrations");
        _migrationsDirectory = migrationsDirectory;
    }

    public async Task<SqlMigrationRunResult> RunAsync(CancellationToken cancellationToken = default)
    {
        var migrations = DiscoverMigrations(_migrationsDirectory);
        var appliedCount = 0;
        var skippedCount = 0;

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await connection.ExecuteAsync(new CommandDefinition("""
        create table if not exists schema_migration (
            version text primary key,
            name text not null,
            checksum text not null,
            applied_at timestamptz not null default now()
        );
        """, transaction: transaction, cancellationToken: cancellationToken));

        await connection.ExecuteAsync(new CommandDefinition(
            "select pg_advisory_xact_lock(@Key);",
            new { Key = AdvisoryLockKey },
            transaction: transaction,
            cancellationToken: cancellationToken));

        var appliedMigrations = await connection.QueryAsync<AppliedSqlMigration>(new CommandDefinition("""
        select version as Version, name as Name, checksum as Checksum
        from schema_migration
        order by version;
        """, transaction: transaction, cancellationToken: cancellationToken));
        var appliedByVersion = appliedMigrations.ToDictionary(migration => migration.Version, StringComparer.Ordinal);

        foreach (var migration in migrations)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (appliedByVersion.TryGetValue(migration.Version, out var appliedMigration))
            {
                EnsureChecksumMatches(migration, appliedMigration);
                skippedCount++;
                continue;
            }

            var sql = await File.ReadAllTextAsync(migration.Path, cancellationToken);
            await connection.ExecuteAsync(new CommandDefinition(sql, transaction: transaction, cancellationToken: cancellationToken));
            await connection.ExecuteAsync(new CommandDefinition("""
            insert into schema_migration (version, name, checksum, applied_at)
            values (@Version, @Name, @Checksum, now());
            """, new { migration.Version, migration.Name, migration.Checksum }, transaction: transaction, cancellationToken: cancellationToken));
            appliedCount++;
        }

        await transaction.CommitAsync(cancellationToken);

        _logger.LogInformation(
            "PostgreSQL 迁移完成：应用 {AppliedCount} 个，跳过 {SkippedCount} 个。",
            appliedCount,
            skippedCount);

        return new SqlMigrationRunResult(appliedCount, skippedCount);
    }

    public static IReadOnlyList<SqlMigration> DiscoverMigrations(string migrationsDirectory)
    {
        if (!Directory.Exists(migrationsDirectory))
        {
            return [];
        }

        return Directory.EnumerateFiles(migrationsDirectory, "*.sql", SearchOption.TopDirectoryOnly)
            .OrderBy(Path.GetFileName, StringComparer.Ordinal)
            .Select(ParseMigrationFile)
            .ToList();
    }

    public static SqlMigration ParseMigrationFile(string migrationPath)
    {
        var fileName = Path.GetFileName(migrationPath);
        var match = MigrationFileNamePattern.Match(fileName);
        if (!match.Success)
        {
            throw new InvalidOperationException($"SQL migration filename '{fileName}' must match '<version>_<name>.sql', for example '0001_init.sql'.");
        }

        return new SqlMigration(
            migrationPath,
            fileName,
            match.Groups["version"].Value,
            match.Groups["name"].Value,
            ComputeChecksumFromFile(migrationPath));
    }

    public static string ComputeChecksumFromFile(string migrationPath)
        => ComputeChecksum(File.ReadAllText(migrationPath));

    public static string ComputeChecksum(string content)
    {
        var normalizedContent = NormalizeContent(content);
        var bytes = Encoding.UTF8.GetBytes(normalizedContent);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static void EnsureChecksumMatches(SqlMigration migration, AppliedSqlMigration appliedMigration)
    {
        if (!string.Equals(migration.Checksum, appliedMigration.Checksum, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Applied SQL migration '{migration.FileName}' checksum mismatch for version '{migration.Version}'. " +
                $"Database has '{appliedMigration.Checksum}', file has '{migration.Checksum}'.");
        }
    }

    private static string NormalizeContent(string content)
    {
        var normalizedContent = content.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\r", "\n", StringComparison.Ordinal);
        return normalizedContent.Length > 0 && normalizedContent[0] == '\uFEFF'
            ? normalizedContent[1..]
            : normalizedContent;
    }
}

public sealed record SqlMigration(string Path, string FileName, string Version, string Name, string Checksum);

public sealed record AppliedSqlMigration(string Version, string Name, string Checksum);

public sealed record SqlMigrationRunResult(int AppliedCount, int SkippedCount);
