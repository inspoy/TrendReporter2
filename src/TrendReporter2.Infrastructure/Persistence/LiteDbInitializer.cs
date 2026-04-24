using LiteDB;
using Microsoft.Extensions.Logging;
using TrendReporter2.Core.Configuration;
using TrendReporter2.Core.Persistence;

namespace TrendReporter2.Infrastructure.Persistence;

public sealed class LiteDbInitializer : ITrendDatabaseInitializer
{
    private readonly AppConfig _config;
    private readonly ILogger<LiteDbInitializer> _logger;

    public LiteDbInitializer(AppConfig config, ILogger<LiteDbInitializer> logger)
    {
        _config = config;
        _logger = logger;
    }

    public void Initialize()
    {
        var databasePath = ResolveDatabasePath(_config.Database.Path);
        var dataDirectory = Path.GetDirectoryName(databasePath);

        if (!string.IsNullOrWhiteSpace(dataDirectory))
        {
            Directory.CreateDirectory(dataDirectory);
        }

        using var database = new LiteDatabase($"Filename={databasePath};Connection=shared");

        EnsureContentItemIndexes(database);
        EnsureContentSnapshotIndexes(database);
        EnsureEventIndexes(database);
        EnsureEventItemIndexes(database);
        EnsureEventScoreSnapshotIndexes(database);
        EnsurePushLogIndexes(database);
        EnsureFetchRunIndexes(database);
        EnsureAppStateIndexes(database);

        _logger.LogInformation(
            "LiteDB initialized at {DatabasePath} with {CollectionCount} collections.",
            databasePath,
            TrendCollectionNames.All.Count);
    }

    private static string ResolveDatabasePath(string configuredPath)
    {
        var expandedPath = Environment.ExpandEnvironmentVariables(configuredPath);
        return Path.GetFullPath(expandedPath, Environment.CurrentDirectory);
    }

    private static void EnsureContentItemIndexes(ILiteDatabase database)
    {
        var collection = database.GetCollection(TrendCollectionNames.ContentItem);
        collection.EnsureIndex("Source");
        collection.EnsureIndex("SourceItemId");
        collection.EnsureIndex("Category");
        collection.EnsureIndex("CreatedAt");
        collection.EnsureIndex("NeedEnrichment");
        collection.EnsureIndex("EnrichmentStatus");
    }

    private static void EnsureContentSnapshotIndexes(ILiteDatabase database)
    {
        var collection = database.GetCollection(TrendCollectionNames.ContentSnapshot);
        collection.EnsureIndex("RunId");
        collection.EnsureIndex("ContentItemId");
        collection.EnsureIndex("Source");
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
