using TrendReporter2.Core.Configuration;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace TrendReporter2.Infrastructure.Configuration;

public sealed class YamlAppConfigLoader : IAppConfigLoader
{
    private readonly IDeserializer _deserializer;

    public YamlAppConfigLoader()
    {
        _deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();
    }

    public AppConfig Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Config path must not be empty.", nameof(path));
        }

        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Config file was not found: {path}", path);
        }

        var yaml = File.ReadAllText(path)
            .Replace("web_extract_url:", "webExtractUrl:", StringComparison.Ordinal);
        var config = _deserializer.Deserialize<AppConfig>(yaml)
            ?? throw new InvalidOperationException($"Config file is empty: {path}");

        AppConfigValidator.Validate(config);
        return config;
    }
}
