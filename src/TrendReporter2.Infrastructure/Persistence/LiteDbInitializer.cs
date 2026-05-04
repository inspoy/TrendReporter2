using LiteDB;
using Microsoft.Extensions.Logging;
using TrendReporter2.Core.Configuration;
using TrendReporter2.Core.Persistence;

namespace TrendReporter2.Infrastructure.Persistence;

public sealed class LiteDbInitializer : ITrendDatabaseInitializer
{
    private readonly AppConfig _config;
    private readonly LiteDbConnectionFactory _connectionFactory;
    private readonly ILogger<LiteDbInitializer> _logger;

    public LiteDbInitializer(
        AppConfig config,
        LiteDbConnectionFactory connectionFactory,
        ILogger<LiteDbInitializer> logger)
    {
        _config = config;
        _connectionFactory = connectionFactory;
        _logger = logger;
    }

    public void Initialize()
    {
        var databasePath = LiteDbConnectionFactory.ResolveDatabasePath(_config.Database.Path);
        using var database = _connectionFactory.Open();

        EnsureContentItemIndexes(database);
        EnsureContentSnapshotIndexes(database);
        EnsureEventIndexes(database);
        EnsureEventItemIndexes(database);
        EnsureEventScoreSnapshotIndexes(database);
        EnsurePushLogIndexes(database);
        EnsureFetchRunIndexes(database);
        EnsureAppStateIndexes(database);

        _logger.LogInformation(
            "LiteDB 已在 {DatabasePath} 初始化完成，共 {CollectionCount} 个集合。",
            databasePath,
            TrendCollectionNames.All.Count);
    }

    private static void EnsureContentItemIndexes(ILiteDatabase database)
    {
        var collection = database.GetCollection(TrendCollectionNames.ContentItem);
        collection.EnsureIndex("DedupKey", unique: true);
        collection.EnsureIndex("Source");
        collection.EnsureIndex("SourceItemId");
        collection.EnsureIndex("Category");
        collection.EnsureIndex("CreatedAt");
        collection.EnsureIndex("UpdatedAt");
        collection.EnsureIndex("LastSeenRunId");
        collection.EnsureIndex("LastSeenAt");
        collection.EnsureIndex("LastSeenRank");
        collection.EnsureIndex("NeedEnrichment");
        collection.EnsureIndex("EnrichmentStatus");
        collection.EnsureIndex("EnrichmentTriedAt");
        collection.EnsureIndex("SummarySource");
    }

    private static void EnsureContentSnapshotIndexes(ILiteDatabase database)
    {
        var collection = database.GetCollection(TrendCollectionNames.ContentSnapshot);
        collection.EnsureIndex("RunId");
        collection.EnsureIndex("ContentItemId");
        collection.EnsureIndex("Source");
        collection.EnsureIndex("Category");
        collection.EnsureIndex("VisualOrder");
        collection.EnsureIndex("CapturedAt");
    }

    private static void EnsureEventIndexes(ILiteDatabase database)
    {
        var collection = database.GetCollection(TrendCollectionNames.Event);
        collection.EnsureIndex("Status");
        collection.EnsureIndex("Type");
        collection.EnsureIndex("LastSeenAt");
        collection.EnsureIndex("IsBlacklisted");
        collection.EnsureIndex("UpdatedAt");
    }

    private static void EnsureEventItemIndexes(ILiteDatabase database)
    {
        var collection = database.GetCollection(TrendCollectionNames.EventItem);
        collection.EnsureIndex("DedupKey", unique: true);
        collection.EnsureIndex("EventId");
        collection.EnsureIndex("ContentItemId");
        collection.EnsureIndex("MatchedAt");
    }

    private static void EnsureEventScoreSnapshotIndexes(ILiteDatabase database)
    {
        var collection = database.GetCollection(TrendCollectionNames.EventScoreSnapshot);
        collection.EnsureIndex("EventId");
        collection.EnsureIndex("RunId");
        collection.EnsureIndex("CalculatedAt");
        collection.EnsureIndex("TotalScore");
    }

    private static void EnsurePushLogIndexes(ILiteDatabase database)
    {
        var collection = database.GetCollection(TrendCollectionNames.PushLog);
        collection.EnsureIndex("EventId");
        collection.EnsureIndex("PushType");
        collection.EnsureIndex("PushedAt");
        collection.EnsureIndex("DedupKey", unique: true);
    }

    private static void EnsureFetchRunIndexes(ILiteDatabase database)
    {
        var collection = database.GetCollection(TrendCollectionNames.FetchRun);
        collection.EnsureIndex("StartedAt");
        collection.EnsureIndex("Status");
    }

    private static void EnsureAppStateIndexes(ILiteDatabase database)
    {
        var collection = database.GetCollection(TrendCollectionNames.AppState);
        collection.EnsureIndex("Key", unique: true);
        collection.EnsureIndex("UpdatedAt");
    }
}
