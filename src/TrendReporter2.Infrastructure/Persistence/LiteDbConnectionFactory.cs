using LiteDB;
using TrendReporter2.Core.Configuration;

namespace TrendReporter2.Infrastructure.Persistence;

public sealed class LiteDbConnectionFactory
{
    private readonly AppConfig _config;

    public LiteDbConnectionFactory(AppConfig config)
    {
        _config = config;
    }

    public LiteDatabase Open()
    {
        var database = _config.Database ?? throw new InvalidOperationException("database 不能为空。");
        var databasePath = ResolveDatabasePath(database.ConnectionString);
        var dataDirectory = Path.GetDirectoryName(databasePath);

        if (!string.IsNullOrWhiteSpace(dataDirectory))
        {
            Directory.CreateDirectory(dataDirectory);
        }

        return new LiteDatabase($"Filename={databasePath};Connection=shared");
    }

    public static string ResolveDatabasePath(string configuredPath)
    {
        var filePath = configuredPath;

        var filenamePrefix = "Filename=";
        if (configuredPath.StartsWith(filenamePrefix, StringComparison.OrdinalIgnoreCase))
        {
            var semicolonIndex = configuredPath.IndexOf(';');
            filePath = semicolonIndex >= 0
                ? configuredPath[(filenamePrefix.Length)..semicolonIndex]
                : configuredPath[filenamePrefix.Length..];
        }

        var expandedPath = Environment.ExpandEnvironmentVariables(filePath);
        return Path.GetFullPath(expandedPath, Environment.CurrentDirectory);
    }
}
