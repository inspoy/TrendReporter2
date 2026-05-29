using Dapper;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using TrendReporter2.Core.Configuration;
using TrendReporter2.Core.Content;
using TrendReporter2.Core.Enrichment;
using TrendReporter2.Core.Events;
using TrendReporter2.Core.Fetch;
using TrendReporter2.Core.Observability;
using TrendReporter2.Core.Sources;
using TrendReporter2.Infrastructure.Persistence;

namespace TrendReporter2.Tests;

public sealed class PostgresRepositoryIntegrationTests
{
    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task ContentAndFetchRunRepositories_WhenPostgresIsAvailable_UpsertContentAndSnapshots()
    {
        await using var fixture = await PostgresFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var fetchRuns = new PostgresFetchRunRepository(fixture.DataSource);
        var sourceRepository = new PostgresSourceRepository(fixture.DataSource);
        var content = new PostgresContentRepository(fixture.DataSource, new EnrichmentPolicy(new AppConfig()), NullLoggerFactory.Instance);
        var startedAt = DateTimeOffset.UtcNow;
        await sourceRepository.UpsertSourcesAsync([Source("source-a", "tech", ContentKind.RankedNews)], CancellationToken.None);
        var run = await fetchRuns.CreateAsync(1, startedAt, CancellationToken.None);
        var pubTimeWithOffset = DateTimeOffset.Parse("2026-05-05T16:20:00+08:00");
        var item = new FetchedContentItem
        {
            Category = "tech",
            SourceId = "source-a",
            ContentKind = ContentKind.RankedNews,
            SourceItemId = "item-1",
            DedupKey = "source-a:item-1",
            Title = "AI company announces major product update",
            Url = "https://example.test/news/1",
            PublishedAt = pubTimeWithOffset,
            HoverText = "AI company announces major product update with enough context for matching.",
            Rank = 1,
            SourceListSize = 10,
            RawPayload = "{\"id\":1}"
        };

        var first = await content.IngestAsync(run.Id, [item], startedAt, CancellationToken.None);
        var second = await content.IngestAsync(run.Id, [new FetchedContentItem
        {
            Category = item.Category,
            SourceId = item.SourceId,
            ContentKind = item.ContentKind,
            SourceItemId = item.SourceItemId,
            DedupKey = item.DedupKey,
            Title = "AI company announces updated product details",
            Url = item.Url,
            PublishedAt = pubTimeWithOffset,
            HoverText = item.HoverText,
            Rank = item.Rank,
            SourceListSize = item.SourceListSize,
            RawPayload = item.RawPayload
        }], startedAt.AddMinutes(1), CancellationToken.None);
        run.Status = FetchRunStatuses.Succeeded;
        run.SuccessSourceCount = 1;
        run.FetchedItemCount = second.TotalCount;
        run.FinishedAt = startedAt.AddMinutes(2);
        await fetchRuns.CompleteAsync(run, CancellationToken.None);

        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        Assert.Equal(new ContentIngestResult(1, 1, 0, 1), first);
        Assert.Equal(new ContentIngestResult(1, 0, 1, 1), second);
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>("select count(*) from content_item;"));
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>("select count(*) from content_snapshot;"));
        Assert.Equal(
            pubTimeWithOffset.ToUniversalTime(),
            await connection.ExecuteScalarAsync<DateTimeOffset>("select pub_time from content_item where source_item_id = @SourceItemId;", new { item.SourceItemId }));
        Assert.Equal(FetchRunStatuses.Succeeded, await connection.ExecuteScalarAsync<string>("select status from fetch_run where id = @Id;", new { run.Id }));
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task EventAndAppStateRepositories_WhenPostgresIsAvailable_PreserveDedupAndDigestSemantics()
    {
        await using var fixture = await PostgresFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var fetchRuns = new PostgresFetchRunRepository(fixture.DataSource);
        var sourceRepository = new PostgresSourceRepository(fixture.DataSource);
        var content = new PostgresContentRepository(fixture.DataSource, new EnrichmentPolicy(new AppConfig()), NullLoggerFactory.Instance);
        var events = new PostgresEventRepository(fixture.DataSource);
        var appState = new PostgresAppStateRepository(fixture.DataSource);
        await sourceRepository.UpsertSourcesAsync([Source("source-a", "world", ContentKind.RankedNews)], CancellationToken.None);
        var run = await fetchRuns.CreateAsync(1, now, CancellationToken.None);
        await content.IngestAsync(run.Id, [new FetchedContentItem
        {
            Category = "world",
            SourceId = "source-a",
            ContentKind = ContentKind.RankedNews,
            SourceItemId = "item-1",
            DedupKey = "source-a:item-1",
            Title = "Major summit reaches new climate agreement",
            Url = "https://example.test/news/1",
            HoverText = "Major summit reaches a new climate agreement after all-night talks.",
            Rank = 2,
            SourceListSize = 10,
            RawPayload = "{}"
        }], now, CancellationToken.None);

        var unmapped = await events.LoadUnmappedRunContentItemsAsync(run.Id, CancellationToken.None);
        var eventAggregate = new EventAggregate
        {
            Id = "ev:test:1",
            CanonicalTitle = "Climate summit agreement",
            Summary = "A major climate summit reached a new agreement.",
            Entities = ["Climate Summit"],
            RepresentativeTitles = ["Major summit reaches new climate agreement"],
            KeyTerms = ["climate", "summit"],
            Status = EventStatus.Active,
            FirstSeenAt = now,
            LastSeenAt = now,
            LastActivatedAt = now,
            CreatedAt = now,
            UpdatedAt = now
        };
        await events.UpsertEventAsync(eventAggregate, CancellationToken.None);

        var mapped = await events.MapEventItemIfMissingAsync(new EventItem
        {
            Id = "ei:test:1",
            EventId = eventAggregate.Id,
            ContentItemId = unmapped.Single().Id,
            Confidence = 0.9,
            MatchedAt = now,
            MatchReason = "test"
        }, CancellationToken.None);
        var mappedAgain = await events.MapEventItemIfMissingAsync(new EventItem
        {
            Id = "ei:test:duplicate",
            EventId = eventAggregate.Id,
            ContentItemId = unmapped.Single().Id,
            Confidence = 0.9,
            MatchedAt = now,
            MatchReason = "test"
        }, CancellationToken.None);

        await events.InsertEventScoreSnapshotAsync(new EventScoreSnapshot
        {
            Id = "ess:test:1",
            EventId = eventAggregate.Id,
            RunId = run.Id,
            CalculatedAt = now,
            CoverageScore = 1,
            RankScore = 0.9,
            FlashScore = 0.7,
            FreshnessScore = 0.8,
            TotalScore = 80,
            UniqueSourceCount = 2,
            RankedSourceCount = 1,
            FlashSourceCount = 1,
            AvgRank = 2,
            AvgNormalizedRank = 0.9,
            HeatValue = 0.9,
            SmoothedHeatValue = 0.9,
            TrendEvidenceCount = 1,
            CurrentStage = EventProgressStages.Expanding,
            TriggerReasons = [TriggerReasons.CoverageRank]
        }, CancellationToken.None);

        var pushLog = new PushLog
        {
            Id = "pl:test:1",
            EventId = eventAggregate.Id,
            PushType = PushTypes.Instant,
            PushedAt = now,
            Title = "Climate summit agreement",
            Reason = TriggerReasons.CoverageRank,
            Content = "content",
            Payload = "{}",
            DedupKey = "instant:test:1",
            Success = false,
            Error = "待处理"
        };
        var insertedPush = await events.InsertPushLogIfMissingAsync(pushLog, CancellationToken.None);
        var duplicatePush = await events.InsertPushLogIfMissingAsync(pushLog, CancellationToken.None);
        pushLog.Success = true;
        pushLog.Error = null;
        await events.UpdatePushLogAsync(pushLog, CancellationToken.None);

        await appState.UpsertAsync(new AppState { Key = "digest:processed:test", Value = "first", UpdatedAt = now }, CancellationToken.None);
        await appState.UpsertAsync(new AppState { Key = "digest:processed:test", Value = "second", UpdatedAt = now.AddMinutes(1) }, CancellationToken.None);

        var scoringInputs = await events.LoadRunEventScoringInputsAsync(run.Id, CancellationToken.None);
        var recentScores = await events.LoadRecentScoreSnapshotsAsync([eventAggregate.Id], now.AddHours(-1), CancellationToken.None);
        var digestCandidates = await events.LoadDigestCandidatesAsync(now.AddHours(-1), 10, CancellationToken.None);
        var state = await appState.GetAsync("digest:processed:test", CancellationToken.None);

        Assert.True(mapped);
        Assert.False(mappedAgain);
        Assert.True(insertedPush);
        Assert.False(duplicatePush);
        Assert.Single(scoringInputs);
        var recentScore = Assert.Single(recentScores);
        Assert.Equal(0.7, recentScore.FlashScore, 3);
        Assert.Equal(0.8, recentScore.FreshnessScore, 3);
        Assert.Equal(1, recentScore.RankedSourceCount);
        Assert.Equal(1, recentScore.FlashSourceCount);
        var digestScore = Assert.Single(digestCandidates).Score;
        Assert.Equal(0.7, digestScore.FlashScore, 3);
        Assert.Equal(0.8, digestScore.FreshnessScore, 3);
        Assert.Equal(1, digestScore.RankedSourceCount);
        Assert.Equal(1, digestScore.FlashSourceCount);
        Assert.Equal("second", state?.Value);
    }

    [Fact]
    [Trait("Category", "PostgresIntegration")]
    public async Task RunTelemetryRecorder_WhenPostgresIsAvailable_WritesSourceStageAndLlmUsage()
    {
        await using var fixture = await PostgresFixture.CreateAsync();
        if (fixture is null)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var fetchRuns = new PostgresFetchRunRepository(fixture.DataSource);
        var telemetry = new PostgresRunTelemetryRecorder(fixture.DataSource);
        var run = await fetchRuns.CreateAsync(1, now, CancellationToken.None);

        await telemetry.RecordSourceAsync(new RunSourceTelemetry(
            run.Id,
            "tech/source-a",
            "tech",
            "source-a",
            RunTelemetryStatuses.Succeeded,
            123,
            5,
            null,
            now), CancellationToken.None);
        await telemetry.RecordStageAsync(new RunStageTelemetry(
            "frs:test:1",
            run.Id,
            RunStageNames.Fetch,
            now,
            now.AddMilliseconds(123),
            123,
            RunTelemetryStatuses.Succeeded,
            null), CancellationToken.None);
        await telemetry.RecordLlmUsageAsync(new LlmUsageRecord(
            "lu:test:1",
            run.Id,
            LlmUsageStages.Cluster,
            "model-a",
            "chatcmpl-1",
            null,
            null,
            1000,
            500,
            200,
            0.00170000m,
            321,
            true,
            1,
            null,
            now), CancellationToken.None);

        var summary = await telemetry.GetLlmUsageSummaryAsync(run.Id, CancellationToken.None);
        run.EstimatedLlmCost = summary.EstimatedCost;
        run.Status = FetchRunStatuses.Succeeded;
        run.SuccessSourceCount = 1;
        run.FinishedAt = now.AddSeconds(1);
        await fetchRuns.CompleteAsync(run, CancellationToken.None);

        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>("select count(*) from fetch_run_source where run_id = @RunId;", new { RunId = run.Id }));
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>("select count(*) from fetch_run_stage where run_id = @RunId;", new { RunId = run.Id }));
        Assert.Equal(1, summary.CallCount);
        Assert.Equal(0.00170000m, summary.EstimatedCost);
        Assert.Equal(0.00170000m, await connection.ExecuteScalarAsync<decimal>("select estimated_llm_cost from fetch_run where id = @RunId;", new { RunId = run.Id }));
    }

    [Fact]
    public void PostgresContentRepository_CalculateFreshnessScore_ForFlashItemsDoesNotRequireRank()
    {
        var capturedAt = new DateTimeOffset(2026, 5, 5, 12, 0, 0, TimeSpan.Zero);

        var fresh = PostgresContentRepository.CalculateFreshnessScore(ContentKind.FlashFeed, capturedAt.AddHours(-6), capturedAt);
        var old = PostgresContentRepository.CalculateFreshnessScore(ContentKind.FlashFeed, capturedAt.AddHours(-48), capturedAt);

        Assert.Null(PostgresContentRepository.CalculateNormalizedRankScore(null, null));
        Assert.InRange(fresh, 0, 1);
        Assert.InRange(old, 0, 1);
        Assert.True(fresh > old);
    }

    private sealed class PostgresFixture : IAsyncDisposable
    {
        private readonly NpgsqlDataSource _adminDataSource;
        private readonly NpgsqlConnection _adminConnection;

        private PostgresFixture(NpgsqlDataSource adminDataSource, NpgsqlConnection adminConnection, NpgsqlDataSource dataSource, string schema)
        {
            _adminDataSource = adminDataSource;
            _adminConnection = adminConnection;
            DataSource = dataSource;
            Schema = schema;
        }

        public NpgsqlDataSource DataSource { get; }

        private string Schema { get; }

        public static async Task<PostgresFixture?> CreateAsync()
        {
            var connectionString = Environment.GetEnvironmentVariable("TRENDREPORTER2_POSTGRES_TEST_CONNECTION");
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                return null;
            }

            var adminDataSource = NpgsqlDataSource.Create(connectionString);
            var adminConnection = await adminDataSource.OpenConnectionAsync();
            var schema = "tr_repo_test_" + Guid.NewGuid().ToString("N");
            await adminConnection.ExecuteAsync($"create schema {QuoteIdentifier(schema)};");

            var builder = new NpgsqlConnectionStringBuilder(connectionString)
            {
                SearchPath = schema
            };
            var dataSource = NpgsqlDataSource.Create(builder.ConnectionString);
            var runner = new SqlMigrationRunner(dataSource, NullLoggerFactory.Instance, InitMigrationDirectory());
            await runner.RunAsync(CancellationToken.None);
            return new PostgresFixture(adminDataSource, adminConnection, dataSource, schema);
        }

        public async ValueTask DisposeAsync()
        {
            await _adminConnection.ExecuteAsync($"drop schema if exists {QuoteIdentifier(Schema)} cascade;");
            await DataSource.DisposeAsync();
            await _adminConnection.DisposeAsync();
            await _adminDataSource.DisposeAsync();
        }

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

        private static string QuoteIdentifier(string identifier)
            => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    private static SourceDefinition Source(string id, string category, string contentKind)
        => new(id, SourceProviders.NewsNow, id, category, id, contentKind, true, 1.0);
}
