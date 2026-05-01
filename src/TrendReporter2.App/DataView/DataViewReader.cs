using LiteDB;
using TrendReporter2.Core.Configuration;
using TrendReporter2.Core.Persistence;
using TrendReporter2.Infrastructure.Persistence;

namespace TrendReporter2.App.DataView;

public sealed class DataViewReader
{
    private readonly AppConfig _config;
    private readonly LiteDbConnectionFactory _connectionFactory;

    public DataViewReader(AppConfig config, LiteDbConnectionFactory connectionFactory)
    {
        _config = config;
        _connectionFactory = connectionFactory;
    }

    public DataViewResult Read(string collectionName, int limit)
    {
        if (string.IsNullOrWhiteSpace(collectionName) || !TrendCollectionNames.All.Contains(collectionName))
        {
            throw new ArgumentException($"Unknown collection '{collectionName}'.", nameof(collectionName));
        }

        var databasePath = LiteDbConnectionFactory.ResolveDatabasePath(_config.Database.Path);
        if (!File.Exists(databasePath))
        {
            throw new FileNotFoundException($"LiteDB database file was not found at '{databasePath}'.", databasePath);
        }

        using var database = _connectionFactory.Open();
        var collection = database.GetCollection<BsonDocument>(collectionName);
        var rows = collection
            .FindAll()
            .Take(limit)
            .Select(ToRow)
            .ToList();

        return new DataViewResult(collectionName, limit, rows.Count, rows);
    }

    private static DataViewRow ToRow(BsonDocument document)
        => new(ToFieldMap(document));

    private static Dictionary<string, object?> ToFieldMap(BsonDocument document)
    {
        var fields = new Dictionary<string, object?>(document.Count, StringComparer.Ordinal);

        foreach (var element in document)
        {
            fields[element.Key] = ToValue(element.Value);
        }

        return fields;
    }

    private static object? ToValue(BsonValue value)
    {
        if (value.IsDocument)
        {
            return ToFieldMap(value.AsDocument);
        }

        if (value.IsArray)
        {
            return value.AsArray.Select(ToValue).ToList();
        }

        return value.RawValue;
    }
}
