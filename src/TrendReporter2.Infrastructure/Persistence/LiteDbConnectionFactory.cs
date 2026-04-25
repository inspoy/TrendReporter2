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
        var databasePath = ResolveDatabasePath(_config.Database.Path);
        var dataDirectory = Path.GetDirectoryName(databasePath);

        if (!string.IsNullOrWhiteSpace(dataDirectory))
        {
            Directory.CreateDirectory(dataDirectory);
        }

        return new LiteDatabase($"Filename={databasePath};Connection=shared");
    }

    public static string ResolveDatabasePath(string configuredPath)
    {
        var expandedPath = Environment.ExpandEnvironmentVariables(configuredPath);
        return Path.GetFullPath(expandedPath, Environment.CurrentDirectory);
    }
}
