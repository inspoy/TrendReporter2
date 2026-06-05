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
            throw new ArgumentException("配置路径不能为空。", nameof(path));
        }

        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"配置文件未找到: {path}", path);
        }

        var yaml = File.ReadAllText(path)
            .Replace("web_extract_url:", "webExtractUrl:", StringComparison.Ordinal);
        RejectDeprecatedKeys(yaml);

        var config = _deserializer.Deserialize<AppConfig>(yaml)
            ?? throw new InvalidOperationException($"配置文件为空: {path}");

        AppConfigValidator.Validate(config);
        return config;
    }

    private static void RejectDeprecatedKeys(string yaml)
    {
        var containsDeprecatedKey = yaml
            .Split('\n')
            .Any(line => line.TrimStart().StartsWith("maxParallelLlm:", StringComparison.Ordinal));
        if (!containsDeprecatedKey)
        {
            return;
        }

        throw new AppConfigValidationException(
        [
            "system.maxParallelLlm 已废弃，请分别配置 llm.cluster.maxParallel、llm.judge.maxParallel、llm.tagging.maxParallel 和 llm.embedding.maxParallel。"
        ]);
    }
}
